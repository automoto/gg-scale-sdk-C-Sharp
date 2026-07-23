using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// The default <see cref="ITransport"/>: JSON over HTTP via
    /// <see cref="HttpClient"/>. Engines that need a different HTTP stack
    /// (e.g. UnityWebRequest) implement ITransport themselves.
    /// </summary>
    public sealed class HttpTransport : ITransport, IDisposable
    {
        private const string UserAgent = "ggscale-csharp/" + SdkVersion.Value;

        private readonly HttpClient _client;
        private readonly bool _ownsClient;

        /// <summary>Creates a transport owning its own HttpClient (30 s timeout).</summary>
        public HttpTransport(string baseUrl)
            : this(baseUrl, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, ownsClient: true)
        {
        }

        /// <summary>Creates a transport over a caller-supplied HttpClient (not disposed).</summary>
        public HttpTransport(string baseUrl, HttpClient client)
            : this(baseUrl, client, ownsClient: false)
        {
        }

        private HttpTransport(string baseUrl, HttpClient client, bool ownsClient)
        {
            if (string.IsNullOrEmpty(baseUrl))
            {
                throw new ArgumentException("baseUrl is required", nameof(baseUrl));
            }
            BaseUrl = baseUrl.TrimEnd('/');
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
        }

        /// <summary>The server base URL (no trailing slash).</summary>
        public string BaseUrl { get; }

        /// <inheritdoc />
        public async Task<JsonValue> CallAsync(GGRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            using var httpReq = BuildRequest(request);
            using var resp = await _client.SendAsync(httpReq, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
#if NET8_0_OR_GREATER
            var body = resp.Content == null
                ? string.Empty
                : await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
            var body = resp.Content == null
                ? string.Empty
                : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif

            var status = (int)resp.StatusCode;
            if (status >= 200 && status < 300)
            {
                return body.Length == 0 ? JsonValue.Null : JsonValue.Parse(body);
            }

            throw MapError(status, body, RetryAfterHeader(resp));
        }

        private HttpRequestMessage BuildRequest(GGRequest request)
        {
            var url = new StringBuilder(BaseUrl).Append(request.Path);
            var first = true;
            foreach (var kv in request.Query)
            {
                url.Append(first ? '?' : '&');
                first = false;
                url.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }

            var httpReq = new HttpRequestMessage(new HttpMethod(request.Method), url.ToString());
            httpReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (request.Body != null)
            {
                httpReq.Content = new StringContent(request.Body.ToString(), Encoding.UTF8, "application/json");
            }
            if (!string.IsNullOrEmpty(request.ApiKey))
            {
                httpReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + request.ApiKey);
            }
            if (!string.IsNullOrEmpty(request.SessionToken))
            {
                httpReq.Headers.TryAddWithoutValidation("X-Session-Token", request.SessionToken);
            }
            if (!string.IsNullOrEmpty(request.IfMatch))
            {
                httpReq.Headers.TryAddWithoutValidation("If-Match", request.IfMatch);
            }
            return httpReq;
        }

        private static TimeSpan? RetryAfterHeader(HttpResponseMessage resp)
        {
            if (!resp.Headers.TryGetValues("Retry-After", out var values))
            {
                return null;
            }
            foreach (var v in values)
            {
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
                {
                    return TimeSpan.FromSeconds(secs);
                }
            }
            return null;
        }

        /// <summary>
        /// Maps a non-2xx response to a <see cref="GGScaleException"/> using
        /// the same tolerant rules as the Go SDK. It reads both the canonical
        /// Huma problem-details shape (title/detail/errors, plus a stable code
        /// extension where emitted) and the legacy error/message envelope,
        /// falling back to the raw text body; a Retry-After header wins. The
        /// server puts machine-readable codes such as "ticket_already_active"
        /// in <c>detail</c>.
        /// </summary>
        internal static GGScaleException MapError(int status, string body, TimeSpan? headerRetryAfter)
        {
            var code = string.Empty;
            var message = string.Empty;
            TimeSpan? retryAfter = null;
            long conflictVersion = 0;
            IReadOnlyList<GGErrorDetail>? details = null;

            if (body.Length > 0)
            {
                JsonValue? parsed = null;
                try
                {
                    parsed = JsonValue.Parse(body);
                }
                catch (FormatException)
                {
                    // Plain-text error body; handled below.
                }
                if (parsed != null && parsed.Kind == JsonKind.Object)
                {
                    // Prefer problem-details, fall back to the legacy envelope.
                    code = FirstNonEmpty(parsed.OptString("code"), parsed.OptString("error"));
                    message = FirstNonEmpty(parsed.OptString("detail"), parsed.OptString("message"), parsed.OptString("title"));
                    details = ParseDetails(parsed.Opt("errors"));
                    var version = parsed.OptLong("version");
                    conflictVersion = version > 0 ? version : parsed.OptLong("current_version");
                    var secs = parsed.OptLong("retry_after_seconds");
                    if (secs > 0)
                    {
                        retryAfter = TimeSpan.FromSeconds(secs);
                    }
                }
            }

            if (code.Length == 0 && message.Length == 0 && body.Length > 0)
            {
                message = body.Trim();
            }
            if (headerRetryAfter != null)
            {
                retryAfter = headerRetryAfter;
            }
            return new GGScaleException(status, code, message, retryAfter, conflictVersion, details);
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrEmpty(v))
                {
                    return v!;
                }
            }
            return string.Empty;
        }

        private static List<GGErrorDetail>? ParseDetails(JsonValue? errors)
        {
            if (errors == null || errors.Kind != JsonKind.Array || errors.Items.Count == 0)
            {
                return null;
            }
            var list = new List<GGErrorDetail>(errors.Items.Count);
            foreach (var e in errors.Items)
            {
                list.Add(GGErrorDetail.FromJson(e));
            }
            return list;
        }

        /// <summary>Disposes the owned HttpClient (no-op for caller-supplied clients).</summary>
        public void Dispose()
        {
            if (_ownsClient)
            {
                _client.Dispose();
            }
        }
    }
}
