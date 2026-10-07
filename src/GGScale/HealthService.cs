using System.Threading;
using System.Threading.Tasks;

namespace GGScale
{
    /// <summary>Server liveness and build identity.</summary>
    public sealed class HealthInfo
    {
        internal HealthInfo(string status, string version, string commit)
        {
            Status = status;
            Version = version;
            Commit = commit;
        }

        /// <summary>"ok" when the server is live.</summary>
        public string Status { get; }

        /// <summary>The server release, for example "v0.9.71".</summary>
        public string Version { get; }

        /// <summary>The server build commit.</summary>
        public string Commit { get; }
    }

    /// <summary>
    /// The unauthenticated liveness endpoint. Reach it via
    /// <see cref="GGScaleClient.Health"/>.
    /// </summary>
    public sealed class HealthService
    {
        private readonly ITransport _transport;

        internal HealthService(ITransport transport) => _transport = transport;

        /// <summary>Reads the server health. Needs no API key and no session.</summary>
        public async Task<HealthInfo> GetAsync(CancellationToken cancellationToken = default)
        {
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/healthz",
                Operation = "GET /v1/healthz",
            }, cancellationToken).ConfigureAwait(false);
            var v = resp.Value;
            return new HealthInfo(
                v.OptString("status") ?? string.Empty,
                v.OptString("version") ?? string.Empty,
                v.OptString("commit") ?? string.Empty);
        }
    }
}
