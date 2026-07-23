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
    /// 2xx JSON response into a <see cref="JsonValue"/> (JsonValue.Null for
    /// empty bodies) and throw <see cref="GGScaleException"/> for any
    /// non-2xx response. Implementations must be safe for concurrent use.
    /// </summary>
    public interface ITransport
    {
        /// <summary>Performs the request and returns the parsed response body.</summary>
        Task<JsonValue> CallAsync(GGRequest request, CancellationToken cancellationToken);
    }
}
