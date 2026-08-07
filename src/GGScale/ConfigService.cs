using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>The result of a remote-config read.</summary>
    public sealed class ConfigResult
    {
        internal ConfigResult(JsonValue value, string etag, bool notModified)
        {
            Value = value;
            ETag = etag;
            NotModified = notModified;
        }

        /// <summary>
        /// The project-defined config JSON object. JsonValue.Null when
        /// <see cref="NotModified"/> is true — keep using the copy you
        /// cached with the ETag.
        /// </summary>
        public JsonValue Value { get; }

        /// <summary>The config validator to pass back on the next read.</summary>
        public string ETag { get; }

        /// <summary>True when the server answered 304 (config unchanged).</summary>
        public bool NotModified { get; }
    }

    /// <summary>
    /// The /v1/config endpoint: project remote configuration. Readable
    /// with the tenant API key alone, before any player logs in. Reach it
    /// via <see cref="GGScaleClient.Config"/>.
    /// </summary>
    public sealed class ConfigService
    {
        private readonly ITransport _transport;
        private readonly string _apiKey;

        internal ConfigService(ITransport transport, string apiKey)
        {
            _transport = transport;
            _apiKey = apiKey;
        }

        /// <summary>
        /// Reads the remote config. Pass the ETag from a previous result to
        /// let the server answer 304 when nothing changed; the result then
        /// has NotModified true and a Null value. The server marks the
        /// response no-cache — always revalidate, never assume freshness.
        /// </summary>
        public async Task<ConfigResult> GetAsync(string? etag = null, CancellationToken cancellationToken = default)
        {
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/config",
                Operation = "GET /v1/config",
                ApiKey = _apiKey,
                IfNoneMatch = string.IsNullOrEmpty(etag) ? null : etag,
            }, cancellationToken).ConfigureAwait(false);
            if (resp.NotModified)
            {
                return new ConfigResult(JsonValue.Null, resp.ETag ?? etag ?? string.Empty, true);
            }
            return new ConfigResult(resp.Value, resp.ETag ?? string.Empty, false);
        }
    }
}
