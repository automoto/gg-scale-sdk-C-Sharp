using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// TURN-REST credentials minted by the server. Feed them to a TURN
    /// client to authenticate against the relay.
    /// </summary>
    public sealed class RelayCredentials
    {
        internal RelayCredentials(string username, string password, long ttl, string realm, IReadOnlyList<string> urls, IReadOnlyList<string> stunUrls)
        {
            Username = username;
            Password = password;
            Ttl = ttl;
            Realm = realm;
            Urls = urls;
            StunUrls = stunUrls;
        }

        /// <summary>TURN username.</summary>
        public string Username { get; }

        /// <summary>TURN password.</summary>
        public string Password { get; }

        /// <summary>Credential lifetime in seconds.</summary>
        public long Ttl { get; }

        /// <summary>TURN realm.</summary>
        public string Realm { get; }

        /// <summary>Relay URLs (turn:/turns: URIs).</summary>
        public IReadOnlyList<string> Urls { get; }

        /// <summary>STUN server URLs for direct connectivity checks; empty when none.</summary>
        public IReadOnlyList<string> StunUrls { get; }

        internal static RelayCredentials FromJson(JsonValue v)
        {
            return new RelayCredentials(
                v.OptString("username") ?? string.Empty,
                v.OptString("password") ?? string.Empty,
                v.OptLong("ttl"),
                v.OptString("realm") ?? string.Empty,
                StringList(v, "urls"),
                StringList(v, "stun_urls"));
        }

        private static List<string> StringList(JsonValue v, string key)
        {
            var list = new List<string>();
            var arr = v.Opt(key);
            if (arr != null && arr.Kind == JsonKind.Array)
            {
                foreach (var item in arr.Items)
                {
                    list.Add(item.AsString());
                }
            }
            return list;
        }
    }

    /// <summary>
    /// POST /v1/relay/credentials. Reach it via
    /// <see cref="GGScaleClient.Relay"/>.
    /// </summary>
    public sealed class RelayService
    {
        private readonly GGScaleClient _client;

        internal RelayService(GGScaleClient client) => _client = client;

        /// <summary>
        /// Returns a fresh TURN credential pair scoped to the current
        /// player. Requires a player session and the p2p_relay key scope.
        /// </summary>
        public Task<RelayCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            GetCredentialsAsync(null, cancellationToken);

        /// <summary>
        /// Returns a fresh TURN credential pair. When <paramref name="matchId"/>
        /// is non-null the server verifies the caller is in that match's roster
        /// before issuing (IsForbidden otherwise), so peer-to-peer clients can
        /// prove match membership. Requires a player session and the p2p_relay
        /// key scope.
        /// </summary>
        public async Task<RelayCredentials> GetCredentialsAsync(string? matchId, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "POST",
                Path = "/v1/relay/credentials",
                Operation = "POST /v1/relay/credentials",
            };
            if (!string.IsNullOrEmpty(matchId))
            {
                req.AddQuery("match_id", matchId!);
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            return RelayCredentials.FromJson(resp);
        }
    }
}
