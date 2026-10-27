using System;
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
        internal RelayCredentials(string username, string password, long ttl, string realm, IReadOnlyList<string> urls, IReadOnlyList<string> stunUrls, string region)
        {
            Username = username;
            Password = password;
            Ttl = ttl;
            Realm = realm;
            Urls = urls;
            StunUrls = stunUrls;
            Region = region;
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

        /// <summary>
        /// The region the relay URLs were chosen from, or empty when they span
        /// regions (no region requested, or none eligible in it).
        /// </summary>
        public string Region { get; }

        internal static RelayCredentials FromJson(JsonValue v)
        {
            return new RelayCredentials(
                v.OptString("username") ?? string.Empty,
                v.OptString("password") ?? string.Empty,
                v.OptLong("ttl"),
                v.OptString("realm") ?? string.Empty,
                StringList(v, "urls"),
                StringList(v, "stun_urls"),
                v.OptString("region") ?? string.Empty);
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

    /// <summary>Optional parameters for a relay credential request.</summary>
    public sealed class RelayCredentialsRequest
    {
        /// <summary>
        /// Scopes the request to a match: the server verifies the caller is in
        /// that match's roster (IsForbidden otherwise).
        /// </summary>
        public string? MatchId { get; set; }

        /// <summary>
        /// Preferred relay region, for example "us-east", matched
        /// case-insensitively. When it has no eligible relay, the server
        /// returns relays from every region and sets
        /// <see cref="RelayCredentials.Region"/> to empty.
        /// </summary>
        public string? Region { get; set; }
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
            GetCredentialsAsync(new RelayCredentialsRequest(), cancellationToken);

        /// <summary>
        /// Returns a fresh TURN credential pair. When <paramref name="matchId"/>
        /// is non-null the server verifies the caller is in that match's roster
        /// before issuing (IsForbidden otherwise), so peer-to-peer clients can
        /// prove match membership. Requires a player session and the p2p_relay
        /// key scope.
        /// </summary>
        public Task<RelayCredentials> GetCredentialsAsync(string? matchId, CancellationToken cancellationToken = default) =>
            GetCredentialsAsync(new RelayCredentialsRequest { MatchId = matchId }, cancellationToken);

        /// <summary>
        /// Returns a fresh TURN credential pair for <paramref name="request"/>
        /// (match scope and preferred region). When no relay is eligible the
        /// server answers 503 with Retry-After; the request is retried within
        /// the client's retry policy, because minting credentials changes no
        /// server state. Requires a player session and the p2p_relay key scope.
        /// </summary>
        public async Task<RelayCredentials> GetCredentialsAsync(RelayCredentialsRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            var req = new GGRequest
            {
                Method = "POST",
                Path = "/v1/relay/credentials",
                Operation = "POST /v1/relay/credentials",
                Idempotent = true,
            };
            if (!string.IsNullOrEmpty(request.MatchId))
            {
                req.AddQuery("match_id", request.MatchId!);
            }
            if (!string.IsNullOrEmpty(request.Region))
            {
                req.AddQuery("region", request.Region!);
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            return RelayCredentials.FromJson(resp);
        }

        private static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Fetches credentials now and again at 80% of each TTL, passing every
        /// new set to <paramref name="onCredentials"/> until
        /// <paramref name="cancellationToken"/> is cancelled. Update the ICE
        /// servers of your peer connections there: the relay refuses an
        /// allocation refresh signed with an expired credential, so a session
        /// that keeps its first credentials loses its relay within minutes.
        /// Renewed credentials keep controlling the same allocation. Failures
        /// that can pass (connection errors, timeouts, 408, 429, 502, 503, 504)
        /// are retried with backoff, honoring Retry-After; any other failure is
        /// thrown. Cancellation throws <see cref="OperationCanceledException"/>.
        /// </summary>
        public async Task KeepFreshAsync(Action<RelayCredentials> onCredentials, RelayCredentialsRequest? request = null, CancellationToken cancellationToken = default)
        {
            if (onCredentials == null)
            {
                throw new ArgumentNullException(nameof(onCredentials));
            }
            var clock = _client.ClientClock;
            var backoff = RetryMin;
            while (true)
            {
                TimeSpan wait;
                try
                {
                    var creds = await GetCredentialsAsync(request ?? new RelayCredentialsRequest(), cancellationToken).ConfigureAwait(false);
                    onCredentials(creds);
                    cancellationToken.ThrowIfCancellationRequested();
                    backoff = RetryMin;
                    var refresh = TimeSpan.FromSeconds(creds.Ttl * 0.8);
                    wait = refresh > RetryMin ? refresh : RetryMin;
                }
                catch (GGScaleException ex) when (ex.IsRetryable && !cancellationToken.IsCancellationRequested)
                {
                    wait = ex.RetryAfter ?? backoff;
                    var doubled = TimeSpan.FromTicks(backoff.Ticks * 2);
                    backoff = doubled < RetryMax ? doubled : RetryMax;
                }
                await clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
