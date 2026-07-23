using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>Returned by VerifySessionAsync on a valid player token.</summary>
    public sealed class PlayerVerifyResult
    {
        internal PlayerVerifyResult(long playerId, string externalId, string email)
        {
            PlayerId = playerId;
            ExternalId = externalId;
            Email = email;
        }

        /// <summary>The verified player's id.</summary>
        public long PlayerId { get; }

        /// <summary>Per-game stable identifier (Steam id, anonymous UUID, …).</summary>
        public string ExternalId { get; }

        /// <summary>The player's email; empty for anonymous players.</summary>
        public string Email { get; }
    }

    /// <summary>
    /// The /v1/server endpoints for server-tier workloads (game servers,
    /// matchmakers) authenticating with a secret API key — no player
    /// session. Publishable keys are rejected with IsForbidden. Reach it
    /// via <see cref="GGScaleClient.Server"/>.
    /// </summary>
    public sealed class ServerService
    {
        private readonly ITransport _transport;
        private readonly string _apiKey;

        internal ServerService(ITransport transport, string apiKey)
        {
            _transport = transport;
            _apiKey = apiKey;
        }

        /// <summary>
        /// Validates a player's session token on behalf of a game server.
        /// Every server-side failure mode (expired token, tampered
        /// signature, disabled player, wrong tenant/project) collapses to
        /// the same opaque 401 — treat IsUnauthorized as "session not
        /// valid" without distinguishing further.
        /// </summary>
        public async Task<PlayerVerifyResult> VerifySessionAsync(string sessionToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionToken))
            {
                throw new ArgumentException("session token is required", nameof(sessionToken));
            }
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/server/player-sessions/verify",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("session_token", JsonValue.Of(sessionToken)),
            }, cancellationToken).ConfigureAwait(false);
            return new PlayerVerifyResult(
                resp.OptLong("player_id"),
                resp.OptString("external_id") ?? string.Empty,
                resp.OptString("email") ?? string.Empty);
        }

        /// <summary>
        /// Reads the remote addresses a player published for direct
        /// connectivity. IsNotFound when the player is unknown or has no
        /// linked account.
        /// </summary>
        public async Task<IReadOnlyList<RemoteAddr>> PlayerRemoteAddrsAsync(long playerId, CancellationToken cancellationToken = default)
        {
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/server/players/" + playerId.ToString(CultureInfo.InvariantCulture) + "/remote-addrs",
                ApiKey = _apiKey,
            }, cancellationToken).ConfigureAwait(false);
            return RemoteAddr.ListFromJson(resp);
        }
    }
}
