using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>A peer's public endpoint exchanged through the session roster.</summary>
    public sealed class GameSessionAddr
    {
        /// <summary>Creates an endpoint (port 1–65535).</summary>
        public GameSessionAddr(string ip, int port)
        {
            Ip = ip;
            Port = port;
        }

        /// <summary>IP address string.</summary>
        public string Ip { get; }

        /// <summary>UDP/TCP port.</summary>
        public int Port { get; }

        internal JsonValue ToJson() =>
            JsonValue.NewObject().Set("ip", JsonValue.Of(Ip)).Set("port", JsonValue.Of((long)Port));

        internal static GameSessionAddr FromJson(JsonValue v) =>
            new GameSessionAddr(v.OptString("ip") ?? string.Empty, (int)v.OptLong("port"));
    }

    /// <summary>One member of a session's roster.</summary>
    public sealed class GameSessionPeer
    {
        internal GameSessionPeer(long playerId, string xuid, GameSessionAddr addr, JsonValue relay)
        {
            PlayerId = playerId;
            Xuid = xuid;
            Addr = addr;
            Relay = relay;
        }

        /// <summary>The peer's player id.</summary>
        public long PlayerId { get; }

        /// <summary>The peer's XUID; empty when unset.</summary>
        public string Xuid { get; }

        /// <summary>The peer's published endpoint.</summary>
        public GameSessionAddr Addr { get; }

        /// <summary>Opaque relay hint; JsonValue.Null when absent.</summary>
        public JsonValue Relay { get; }

        internal static GameSessionPeer FromJson(JsonValue v) =>
            new GameSessionPeer(
                v.OptLong("player_id"),
                v.OptString("xuid") ?? string.Empty,
                GameSessionAddr.FromJson(v.Opt("addr") ?? JsonValue.NewObject()),
                v.Opt("relay") ?? JsonValue.Null);
    }

    /// <summary>A player-hosted game session with its peer roster.</summary>
    public sealed class GameSession
    {
        internal GameSession(string sessionId, string joinCode, string state, IReadOnlyList<GameSessionPeer> peers)
        {
            SessionId = sessionId;
            JoinCode = joinCode;
            State = state;
            Peers = peers;
        }

        /// <summary>Server-issued session id.</summary>
        public string SessionId { get; }

        /// <summary>Short shareable code others resolve via ResolveAsync.</summary>
        public string JoinCode { get; }

        /// <summary>Session state: "open" or "ended".</summary>
        public string State { get; }

        /// <summary>The current peer roster.</summary>
        public IReadOnlyList<GameSessionPeer> Peers { get; }

        internal static GameSession FromJson(JsonValue v) =>
            new GameSession(
                v.OptString("session_id") ?? string.Empty,
                v.OptString("join_code") ?? string.Empty,
                v.OptString("state") ?? string.Empty,
                ParsePeers(v));

        internal static IReadOnlyList<GameSessionPeer> ParsePeers(JsonValue v)
        {
            var peers = new List<GameSessionPeer>();
            var arr = v.Opt("peers");
            if (arr != null)
            {
                foreach (var p in arr.Items)
                {
                    peers.Add(GameSessionPeer.FromJson(p));
                }
            }
            return peers;
        }
    }

    /// <summary>Input to <see cref="GameSessionsService.CreateAsync"/>.</summary>
    public sealed class GameSessionCreate
    {
        /// <summary>Optional game/title identifier.</summary>
        public string? TitleId { get; set; }

        /// <summary>The host's reachable endpoint. Required.</summary>
        public GameSessionAddr? PublicAddr { get; set; }

        /// <summary>Opaque session properties (raw JSON object).</summary>
        public JsonValue? Props { get; set; }

        /// <summary>Player cap (server default 2, max 64).</summary>
        public int MaxPlayers { get; set; }

        /// <summary>Private sessions are visible only to host, members, and invitees.</summary>
        public bool Private { get; set; }

        internal JsonValue ToJson()
        {
            var body = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(TitleId))
            {
                body.Set("title_id", JsonValue.Of(TitleId!));
            }
            if (PublicAddr == null)
            {
                throw new InvalidOperationException("ggscale: GameSessionCreate.PublicAddr is required");
            }
            body.Set("public_addr", PublicAddr.ToJson());
            if (Props != null)
            {
                body.Set("props", Props);
            }
            if (MaxPlayers > 0)
            {
                body.Set("max_players", JsonValue.Of((long)MaxPlayers));
            }
            if (Private)
            {
                body.Set("private", JsonValue.True);
            }
            return body;
        }
    }

    /// <summary>
    /// The /v1/game-session endpoints for player-hosted (listen-server)
    /// games: create a session, share its join code, join, and heartbeat
    /// to keep the roster fresh. Reach it via
    /// <see cref="GGScaleClient.GameSessions"/>.
    /// </summary>
    public sealed class GameSessionsService
    {
        private readonly GGScaleClient _client;

        internal GameSessionsService(GGScaleClient client) => _client = client;

        /// <summary>
        /// Opens a session hosted by the caller (who becomes the first
        /// peer). IsRateLimited when the project's open-session cap is hit.
        /// </summary>
        public async Task<GameSession> CreateAsync(GameSessionCreate create, CancellationToken cancellationToken = default)
        {
            if (create == null)
            {
                throw new ArgumentNullException(nameof(create));
            }
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/game-session",
                Body = create.ToJson(),
            }, cancellationToken).ConfigureAwait(false);
            return GameSession.FromJson(resp);
        }

        /// <summary>
        /// Returns a session and its roster. Only the host, members, and
        /// invitees can see it; everyone else gets IsNotFound.
        /// </summary>
        public async Task<GameSession> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = SessionPath(sessionId),
            }, cancellationToken).ConfigureAwait(false);
            return GameSession.FromJson(resp);
        }

        /// <summary>
        /// Turns a shareable join code into a session id for JoinAsync.
        /// Private sessions resolve only for host/members/invitees.
        /// </summary>
        public async Task<string> ResolveAsync(string joinCode, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest { Method = "GET", Path = "/v1/game-session" };
            req.AddQuery("joinCode", joinCode);
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            return resp.OptString("session_id") ?? string.Empty;
        }

        /// <summary>
        /// Adds the caller to the session, publishing addr to the other
        /// peers, and returns the refreshed session. A full session reports
        /// IsConflict; an ended or expired one surfaces Status 410.
        /// </summary>
        public async Task<GameSession> JoinAsync(string sessionId, GameSessionAddr addr, CancellationToken cancellationToken = default)
        {
            if (addr == null)
            {
                throw new ArgumentNullException(nameof(addr));
            }
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = SessionPath(sessionId) + "/join",
                Body = JsonValue.NewObject().Set("public_addr", addr.ToJson()),
            }, cancellationToken).ConfigureAwait(false);
            return GameSession.FromJson(resp);
        }

        /// <summary>
        /// Marks the caller live and returns the roster with stale peers
        /// pruned. qos optionally updates the caller's connection-quality
        /// blob; null leaves the stored value untouched. Call roughly every
        /// 30 seconds while in the session.
        /// </summary>
        public async Task<IReadOnlyList<GameSessionPeer>> HeartbeatAsync(string sessionId, JsonValue? qos = null, CancellationToken cancellationToken = default)
        {
            var body = JsonValue.NewObject();
            if (qos != null)
            {
                body.Set("qos", qos);
            }
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = SessionPath(sessionId) + "/heartbeat",
                Body = body,
            }, cancellationToken).ConfigureAwait(false);
            return GameSession.ParsePeers(resp);
        }

        /// <summary>Leaves the session. When the host leaves, the session ends for everyone.</summary>
        public Task LeaveAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "DELETE",
                Path = SessionPath(sessionId),
            }, cancellationToken);
        }

        private static string SessionPath(string sessionId) =>
            "/v1/game-session/" + Uri.EscapeDataString(sessionId);
    }
}
