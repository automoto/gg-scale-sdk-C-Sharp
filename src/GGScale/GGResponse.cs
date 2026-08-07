using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// A successful transport result: the parsed body plus the response
    /// metadata the SDK needs (ETag for conditional GETs, the server's
    /// X-Request-Id for correlation).
    /// </summary>
    public sealed class GGResponse
    {
        /// <summary>Creates a response envelope.</summary>
        public GGResponse(int status, JsonValue value, string? etag = null, string? requestId = null)
        {
            Status = status;
            Value = value ?? JsonValue.Null;
            ETag = etag;
            RequestId = requestId;
        }

        /// <summary>The HTTP status code (2xx, or 304 for a conditional GET).</summary>
        public int Status { get; }

        /// <summary>The parsed JSON body; JsonValue.Null for empty and 304 bodies.</summary>
        public JsonValue Value { get; }

        /// <summary>The response ETag header, when present.</summary>
        public string? ETag { get; }

        /// <summary>The response X-Request-Id header, when present.</summary>
        public string? RequestId { get; }

        /// <summary>True when the server answered 304 Not Modified.</summary>
        public bool NotModified => Status == 304;
    }
}
