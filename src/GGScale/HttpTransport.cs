using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>Configuration for <see cref="HttpTransport"/>.</summary>
    public sealed class HttpTransportOptions
    {
        /// <summary>User-Agent header; null uses "ggscale-csharp/&lt;sdk-version&gt;".</summary>
        public string? UserAgent { get; set; }

        /// <summary>
        /// Per-attempt timeout covering connect, send, and body read.
        /// Elapsing surfaces as <see cref="GGFailureKind.Timeout"/>.
        /// Default 30 seconds.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum response body size in bytes; larger bodies surface as
        /// <see cref="GGFailureKind.Decode"/>. Default 4 MiB.
        /// </summary>
        public long MaxResponseBytes { get; set; } = 4 * 1024 * 1024;
    }

    /// <summary>
    /// The default <see cref="ITransport"/>: JSON over HTTP via
    /// <see cref="HttpClient"/>. Engines that need a different HTTP stack
    /// (e.g. UnityWebRequest) implement ITransport themselves.
    /// </summary>
    public sealed class HttpTransport : ITransport, IDisposable
    {
        private const int RawBodyLimit = 2048;

        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly HttpTransportOptions _options;
        private readonly string _userAgent;
        private readonly IGGClock _clock;

        /// <summary>Creates a transport owning its own HttpClient with default options.</summary>
        public HttpTransport(string baseUrl)
            : this(baseUrl, null, null, null)
        {
        }

        /// <summary>Creates a transport over a caller-supplied HttpClient (not disposed).</summary>
        public HttpTransport(string baseUrl, HttpClient client)
            : this(baseUrl, client ?? throw new ArgumentNullException(nameof(client)), null, null)
        {
        }

        /// <summary>Creates a transport owning its own HttpClient with the given options.</summary>
        public HttpTransport(string baseUrl, HttpTransportOptions options)
            : this(baseUrl, null, options, null)
        {
        }

        /// <summary>Creates a transport over a caller-supplied HttpClient with the given options.</summary>
        public HttpTransport(string baseUrl, HttpClient client, HttpTransportOptions options)
            : this(baseUrl, client ?? throw new ArgumentNullException(nameof(client)), options, null)
        {
        }

        internal HttpTransport(string baseUrl, HttpClient? client, HttpTransportOptions? options, IGGClock? clock)
        {
            if (string.IsNullOrEmpty(baseUrl))
            {
                throw new ArgumentException("baseUrl is required", nameof(baseUrl));
            }
            BaseUrl = baseUrl.TrimEnd('/');
            _options = options ?? new HttpTransportOptions();
            _userAgent = _options.UserAgent ?? "ggscale-csharp/" + SdkVersion.Value;
            _clock = clock ?? SystemClock.Instance;
            if (client == null)
            {
                // The per-attempt timeout is enforced with a linked token so
                // it can be told apart from caller cancellation.
                _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                _ownsClient = true;
            }
            else
            {
                _client = client;
                _ownsClient = false;
            }
        }

        /// <summary>The server base URL (no trailing slash).</summary>
        public string BaseUrl { get; }

        /// <inheritdoc />
        public async Task<GGResponse> CallAsync(GGRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            using var httpReq = BuildRequest(request);
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.Timeout > TimeSpan.Zero)
            {
                attemptCts.CancelAfter(_options.Timeout);
            }

            HttpResponseMessage resp;
            string body;
            try
            {
                resp = await _client.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw ConnectionFailure(ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw AttemptTimeout(ex);
            }

            using (resp)
            {
                try
                {
                    body = await ReadBoundedBodyAsync(resp, attemptCts.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw ConnectionFailure(ex);
                }
                catch (IOException ex)
                {
                    throw ConnectionFailure(ex);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw AttemptTimeout(ex);
                }

                var status = (int)resp.StatusCode;
                var etag = HeaderValue(resp, "ETag");
                var requestId = HeaderValue(resp, "X-Request-Id");
                if (status >= 200 && status < 300)
                {
                    if (body.Length == 0)
                    {
                        return new GGResponse(status, JsonValue.Null, etag, requestId);
                    }
                    try
                    {
                        return new GGResponse(status, JsonValue.Parse(body), etag, requestId);
                    }
                    catch (FormatException ex)
                    {
                        throw new GGScaleException(GGFailureKind.Decode, "invalid_json", "success response body is not valid JSON", ex)
                        {
                            RequestId = requestId,
                            RawBody = Bound(body),
                        };
                    }
                }
                if (status == 304 && request.IfNoneMatch != null)
                {
                    return new GGResponse(status, JsonValue.Null, etag, requestId);
                }

                var mapped = MapError(status, body, RetryAfterHeader(resp));
                mapped.RequestId = requestId;
                throw mapped;
            }
        }

        private static GGScaleException ConnectionFailure(Exception inner) =>
            GGScaleException.HasCertificateFailure(inner)
                ? new GGScaleException(GGFailureKind.Connection, GGScaleException.CertificateErrorCode, inner.Message, inner)
                : new GGScaleException(GGFailureKind.Connection, "connection_error", inner.Message, inner);

        private GGScaleException AttemptTimeout(Exception inner) =>
            new GGScaleException(
                GGFailureKind.Timeout,
                "timeout",
                FormattableString.Invariant($"no response within the {_options.Timeout.TotalSeconds:0.###} s attempt timeout"),
                inner);

        private async Task<string> ReadBoundedBodyAsync(HttpResponseMessage resp, CancellationToken cancellationToken)
        {
            var content = resp.Content;
            if (content == null)
            {
                return string.Empty;
            }
            var declared = content.Headers.ContentLength;
            if (declared != null && declared.Value > _options.MaxResponseBytes)
            {
                throw ResponseTooLarge(declared.Value);
            }
#if NET8_0_OR_GREATER
            var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            using (stream)
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                while (true)
                {
                    var n = await stream.ReadAsync(new Memory<byte>(chunk), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }
                    if (buffer.Length + n > _options.MaxResponseBytes)
                    {
                        throw ResponseTooLarge(buffer.Length + n);
                    }
                    buffer.Write(chunk, 0, n);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private GGScaleException ResponseTooLarge(long atLeast) =>
            new GGScaleException(
                GGFailureKind.Decode,
                "response_too_large",
                FormattableString.Invariant($"response body of at least {atLeast} bytes exceeds the {_options.MaxResponseBytes}-byte limit"));

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
            httpReq.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
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
            if (!string.IsNullOrEmpty(request.IfNoneMatch))
            {
                httpReq.Headers.TryAddWithoutValidation("If-None-Match", request.IfNoneMatch);
            }
            if (!string.IsNullOrEmpty(request.RequestId))
            {
                httpReq.Headers.TryAddWithoutValidation("X-Request-Id", request.RequestId);
            }
            return httpReq;
        }

        private static string? HeaderValue(HttpResponseMessage resp, string name)
        {
            if (!resp.Headers.TryGetValues(name, out var values))
            {
                return null;
            }
            foreach (var v in values)
            {
                return v;
            }
            return null;
        }

        private TimeSpan? RetryAfterHeader(HttpResponseMessage resp)
        {
            var ra = resp.Headers.RetryAfter;
            if (ra == null)
            {
                return null;
            }
            if (ra.Delta != null)
            {
                return ra.Delta;
            }
            if (ra.Date != null)
            {
                var wait = ra.Date.Value - _clock.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
            return null;
        }

        /// <summary>
        /// Maps a non-2xx response to a <see cref="GGScaleException"/> using
        /// the same tolerant rules as the Go SDK. It reads both the canonical
        /// Huma problem-details shape (type/title/detail/instance/errors,
        /// plus a stable code extension where emitted) and the legacy
        /// error/message envelope, falling back to the raw text body; a
        /// Retry-After header wins. The server puts machine-readable codes
        /// such as "ticket_already_active" in <c>detail</c>.
        /// </summary>
        internal static GGScaleException MapError(int status, string body, TimeSpan? headerRetryAfter)
        {
            var code = string.Empty;
            var message = string.Empty;
            var problemType = string.Empty;
            var title = string.Empty;
            var instance = string.Empty;
            TimeSpan? retryAfter = null;
            long conflictVersion = 0;
            IReadOnlyList<GGErrorDetail>? details = null;
            JsonValue? parsed = null;

            if (body.Length > 0)
            {
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
                    problemType = parsed.OptString("type") ?? string.Empty;
                    title = parsed.OptString("title") ?? string.Empty;
                    instance = parsed.OptString("instance") ?? string.Empty;
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
            return new GGScaleException(status, code, message, retryAfter, conflictVersion, details)
            {
                ProblemType = problemType,
                Title = title,
                Instance = instance,
                RawBody = parsed == null && body.Length > 0 ? Bound(body) : null,
            };
        }

        private static string Bound(string body) =>
            body.Length <= RawBodyLimit ? body : body.Substring(0, RawBodyLimit);

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
