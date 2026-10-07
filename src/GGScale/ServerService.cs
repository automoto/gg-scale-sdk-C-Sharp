using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    /// The operations for server-tier workloads (game servers,
    /// matchmakers) that need a secret API key and no player session: the
    /// /v1/server endpoints and the fleet heartbeat. Publishable keys are
    /// rejected with IsForbidden. Reach it via
    /// <see cref="GGScaleClient.Server"/>.
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
                Operation = "POST /v1/server/player-sessions/verify",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("session_token", JsonValue.Of(sessionToken)),
            }, cancellationToken).ConfigureAwait(false);
            return new PlayerVerifyResult(
                resp.Value.OptLong("player_id"),
                resp.Value.OptString("external_id") ?? string.Empty,
                resp.Value.OptString("email") ?? string.Empty);
        }

        /// <summary>
        /// Announces this game-server's liveness and player count. Call it
        /// every 5 seconds; the server registry drops a server after about
        /// 15 seconds without a heartbeat. Requires a secret API key.
        /// </summary>
        public Task FleetHeartbeatAsync(FleetHeartbeat heartbeat, CancellationToken cancellationToken = default)
        {
            if (heartbeat == null)
            {
                throw new ArgumentNullException(nameof(heartbeat));
            }
            if (string.IsNullOrEmpty(heartbeat.AgonesName) || string.IsNullOrEmpty(heartbeat.Fleet) || string.IsNullOrEmpty(heartbeat.Address))
            {
                throw new ArgumentException("heartbeat requires AgonesName, Fleet, and Address", nameof(heartbeat));
            }
            if (heartbeat.MaxPlayers <= 0)
            {
                throw new ArgumentException("heartbeat MaxPlayers must be > 0", nameof(heartbeat));
            }
            return _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/fleets/heartbeat",
                Operation = "POST /v1/fleets/heartbeat",
                ApiKey = _apiKey,
                Body = heartbeat.ToJson(),
            }, cancellationToken);
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
                Path = PlayerPath(playerId) + "/remote-addrs",
                Operation = "GET /v1/server/players/{player_id}/remote-addrs",
                ApiKey = _apiKey,
            }, cancellationToken).ConfigureAwait(false);
            return RemoteAddr.ListFromJson(resp.Value);
        }

        /// <summary>
        /// Posts a score for the player named by id — the server-tier
        /// alternative to client submissions. IsNotFound for an unknown
        /// player or board; IsForbidden for a disabled or banned player.
        /// </summary>
        public Task SubmitScoreAsync(long leaderboardId, long playerId, long score, JsonValue? metadata = null, CancellationToken cancellationToken = default)
        {
            var body = JsonValue.NewObject()
                .Set("player_id", JsonValue.Of(playerId))
                .Set("score", JsonValue.Of(score));
            if (metadata != null)
            {
                body.Set("metadata", metadata);
            }
            return _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/server/leaderboards/" + leaderboardId.ToString(CultureInfo.InvariantCulture) + "/scores",
                Operation = "POST /v1/server/leaderboards/{id}/scores",
                ApiKey = _apiKey,
                Body = body,
            }, cancellationToken);
        }

        /// <summary>Reads one of a player's storage objects; IsNotFound when absent.</summary>
        public async Task<StorageObject> GetPlayerStorageAsync(long playerId, string key, CancellationToken cancellationToken = default)
        {
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "GET",
                Path = PlayerStoragePath(playerId, key),
                Operation = "GET /v1/server/players/{player_id}/storage/objects/{key}",
                ApiKey = _apiKey,
            }, cancellationToken).ConfigureAwait(false);
            return StorageObject.FromJson(resp.Value);
        }

        /// <summary>
        /// Writes one of a player's storage objects and returns it with
        /// its new version. Pass <paramref name="ifMatchVersion"/> for
        /// optimistic concurrency: a mismatch throws with IsConflict true.
        /// </summary>
        public async Task<StorageObject> PutPlayerStorageAsync(long playerId, string key, JsonValue value, long? ifMatchVersion = null, CancellationToken cancellationToken = default)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var req = new GGRequest
            {
                Method = "PUT",
                Path = PlayerStoragePath(playerId, key),
                Operation = "PUT /v1/server/players/{player_id}/storage/objects/{key}",
                ApiKey = _apiKey,
                Body = value,
            };
            if (ifMatchVersion != null)
            {
                req.IfMatch = ifMatchVersion.Value.ToString(CultureInfo.InvariantCulture);
            }
            var resp = await _transport.CallAsync(req, cancellationToken).ConfigureAwait(false);
            return StorageObject.FromJson(resp.Value);
        }

        /// <summary>Pages through a player's storage objects, oldest first.</summary>
        public async Task<StoragePage> ListPlayerStorageAsync(long playerId, StorageListOptions? options = null, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "GET",
                Path = PlayerPath(playerId) + "/storage/objects",
                Operation = "GET /v1/server/players/{player_id}/storage/objects",
                ApiKey = _apiKey,
            };
            if (!string.IsNullOrEmpty(options?.KeyPrefix))
            {
                req.AddQuery("key_prefix", options!.KeyPrefix!);
            }
            if (options?.Limit > 0)
            {
                req.AddQuery("limit", options.Limit.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(options?.Cursor))
            {
                req.AddQuery("cursor", options!.Cursor!);
            }
            var resp = await _transport.CallAsync(req, cancellationToken).ConfigureAwait(false);
            return StoragePage.FromJson(resp.Value);
        }

        /// <summary>
        /// Iterates a player's storage objects across every page. It reads
        /// pages only when the enumeration needs them. The caller's options
        /// object does not change.
        /// </summary>
        public async IAsyncEnumerable<StorageObject> ListAllStorageAsync(
            long playerId,
            StorageListOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var page = new StorageListOptions
            {
                KeyPrefix = options?.KeyPrefix,
                Limit = options?.Limit ?? 0,
                Cursor = options?.Cursor,
            };
            while (true)
            {
                var result = await ListPlayerStorageAsync(playerId, page, cancellationToken).ConfigureAwait(false);
                foreach (var item in result.Items)
                {
                    yield return item;
                }
                if (result.NextCursor.Length == 0)
                {
                    yield break;
                }
                page.Cursor = result.NextCursor;
            }
        }

        private static string PlayerPath(long playerId) =>
            "/v1/server/players/" + playerId.ToString(CultureInfo.InvariantCulture);

        private static string PlayerStoragePath(long playerId, string key) =>
            PlayerPath(playerId) + "/storage/objects/" + Uri.EscapeDataString(key);
    }
}
