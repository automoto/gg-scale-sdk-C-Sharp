using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// One API call. Service methods build a GGRequest and hand it to an
    /// <see cref="ITransport"/>; tests capture and inspect it directly
    /// without going through HTTP.
    /// </summary>
    public sealed class GGRequest
    {
        private readonly List<KeyValuePair<string, string>> _query = new List<KeyValuePair<string, string>>();

        /// <summary>HTTP method: GET, POST, PUT, PATCH, DELETE.</summary>
        public string Method { get; set; } = "GET";

        /// <summary>Path under the base URL, e.g. "/v1/profile".</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Query parameters in append order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Query => _query;

        /// <summary>Optional JSON request body.</summary>
        public JsonValue? Body { get; set; }

        /// <summary>Tenant API key, sent as "Authorization: Bearer".</summary>
        public string? ApiKey { get; set; }

        /// <summary>Player session token, sent as "X-Session-Token".</summary>
        public string? SessionToken { get; set; }

        /// <summary>Optional If-Match value (storage OCC).</summary>
        public string? IfMatch { get; set; }

        /// <summary>Optional If-None-Match validator for conditional GETs (/v1/config).</summary>
        public string? IfNoneMatch { get; set; }

        /// <summary>
        /// The route template for telemetry, e.g. "GET /v1/storage/objects/{key}".
        /// Never a raw URL — path parameters stay as placeholders.
        /// </summary>
        public string Operation { get; set; } = string.Empty;

        /// <summary>
        /// Marks a POST/PATCH request as safe to replay so the retry layer
        /// may retry it. GET/HEAD/PUT/DELETE are replayable by default.
        /// </summary>
        public bool Idempotent { get; set; }

        /// <summary>
        /// The client-generated X-Request-Id, assigned once per logical call
        /// and kept stable across retry attempts. Transports send it as the
        /// X-Request-Id header.
        /// </summary>
        public string? RequestId { get; internal set; }

        /// <summary>
        /// When this logical call started; stamped once by the retry layer
        /// so duration and the overall deadline span the 401
        /// refresh-and-retry re-invocation.
        /// </summary>
        internal DateTimeOffset? TelemetryStart { get; set; }

        /// <summary>Attempts completed so far across re-invocations.</summary>
        internal int TelemetryAttempts { get; set; }

        /// <summary>
        /// Set while the client will refresh-and-retry a 401, so the retry
        /// layer defers the completion record instead of reporting a
        /// failure that is about to be retried.
        /// </summary>
        internal bool RetryOn401Pending { get; set; }

        /// <summary>Appends a query parameter.</summary>
        public void AddQuery(string name, string value)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            _query.Add(new KeyValuePair<string, string>(name, value));
        }

        /// <summary>The first query value for <paramref name="name"/>, or null.</summary>
        public string? QueryValue(string name)
        {
            foreach (var kv in _query)
            {
                if (kv.Key == name)
                {
                    return kv.Value;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Sends a single request to the ggscale API. Implementations parse a
    /// success response into a <see cref="GGResponse"/> (JsonValue.Null for
    /// empty and 304 bodies) and throw <see cref="GGScaleException"/> for
    /// any failure, classifying transport-level failures via
    /// <see cref="GGFailureKind"/>. Implementations must be safe for
    /// concurrent use.
    /// </summary>
    public interface ITransport
    {
        /// <summary>Performs the request and returns the response envelope.</summary>
        Task<GGResponse> CallAsync(GGRequest request, CancellationToken cancellationToken);
    }
}
