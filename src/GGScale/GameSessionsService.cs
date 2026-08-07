using System;
using System.Collections.Generic;
using System.Globalization;
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
        internal GameSessionPeer(long playerId, string xuid, string displayName, GameSessionAddr addr)
        {
            PlayerId = playerId;
            Xuid = xuid;
            DisplayName = displayName;
            Addr = addr;
        }

        /// <summary>The peer's player id.</summary>
        public long PlayerId { get; }

        /// <summary>The peer's XUID; empty when unset.</summary>
        public string Xuid { get; }

        /// <summary>The peer's display name; empty when unset.</summary>
        public string DisplayName { get; }

        /// <summary>The peer's published endpoint.</summary>
        public GameSessionAddr Addr { get; }

        internal static GameSessionPeer FromJson(JsonValue v) =>
            new GameSessionPeer(
                v.OptLong("player_id"),
                v.OptString("xuid") ?? string.Empty,
                v.OptString("display_name") ?? string.Empty,
                GameSessionAddr.FromJson(v.Opt("addr") ?? JsonValue.NewObject()));
    }

    /// <summary>A player-hosted game session with its peer roster.</summary>
    public sealed class GameSession
    {
        internal GameSession(string sessionId, string joinCode, string state, DateTimeOffset expiresAt, IReadOnlyList<GameSessionPeer> peers)
        {
            SessionId = sessionId;
            JoinCode = joinCode;
            State = state;
            ExpiresAt = expiresAt;
            Peers = peers;
        }

        /// <summary>Server-issued session id.</summary>
        public string SessionId { get; }

        /// <summary>Short shareable code others resolve via ResolveAsync.</summary>
        public string JoinCode { get; }

        /// <summary>Session state: "open" or "ended".</summary>
        public string State { get; }

        /// <summary>When the session expires without further heartbeats.</summary>
        public DateTimeOffset ExpiresAt { get; }

        /// <summary>The current peer roster.</summary>
        public IReadOnlyList<GameSessionPeer> Peers { get; }

        internal static GameSession FromJson(JsonValue v) =>
            new GameSession(
                v.OptString("session_id") ?? string.Empty,
                v.OptString("join_code") ?? string.Empty,
                v.OptString("state") ?? string.Empty,
                v.OptTime("expires_at") ?? DateTimeOffset.MinValue,
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

    /// <summary>
    /// The WebRTC-style signal kinds exchanged through session signaling.
    /// The set is open: treat unknown wire values as pass-through strings.
    /// </summary>
    public static class GameSessionSignalKind
    {
        /// <summary>An SDP offer.</summary>
        public const string Offer = "offer";

        /// <summary>An SDP answer.</summary>
        public const string Answer = "answer";

        /// <summary>An ICE-restart offer.</summary>
        public const string RestartOffer = "restart_offer";

        /// <summary>An ICE-restart answer.</summary>
        public const string RestartAnswer = "restart_answer";
    }

    /// <summary>One session in the public session browser.</summary>
    public sealed class PublicGameSessionEntry
    {
        internal PublicGameSessionEntry(string sessionId, string titleId, JsonValue props, int playerCount, int maxPlayers, long hostPlayerId, string hostDisplayName, DateTimeOffset createdAt)
        {
            SessionId = sessionId;
            TitleId = titleId;
            Props = props;
            PlayerCount = playerCount;
            MaxPlayers = maxPlayers;
            HostPlayerId = hostPlayerId;
            HostDisplayName = hostDisplayName;
            CreatedAt = createdAt;
        }

        /// <summary>The session id (joinable via JoinAsync).</summary>
        public string SessionId { get; }

        /// <summary>Game/title identifier; empty when unset.</summary>
        public string TitleId { get; }

        /// <summary>Opaque session properties; JsonValue.Null when absent.</summary>
        public JsonValue Props { get; }

        /// <summary>Current player count.</summary>
        public int PlayerCount { get; }

        /// <summary>Player capacity.</summary>
        public int MaxPlayers { get; }

        /// <summary>The hosting player's id.</summary>
        public long HostPlayerId { get; }

        /// <summary>The host's display name; empty when unset.</summary>
        public string HostDisplayName { get; }

        /// <summary>Session creation time.</summary>
        public DateTimeOffset CreatedAt { get; }

        internal static PublicGameSessionEntry FromJson(JsonValue v) =>
            new PublicGameSessionEntry(
                v.OptString("session_id") ?? string.Empty,
                v.OptString("title_id") ?? string.Empty,
                v.Opt("props") ?? JsonValue.Null,
                (int)v.OptLong("player_count"),
                (int)v.OptLong("max_players"),
                v.OptLong("host_player_id"),
                v.OptString("host_display_name") ?? string.Empty,
                v.OptTime("created_at") ?? DateTimeOffset.MinValue);
    }

    /// <summary>One page of the session browser; NextCursor is empty on the last page.</summary>
    public sealed class GameSessionPage
    {
        internal GameSessionPage(IReadOnlyList<PublicGameSessionEntry> items, string nextCursor)
        {
            Items = items;
            NextCursor = nextCursor;
        }

        /// <summary>The page's sessions, newest first.</summary>
        public IReadOnlyList<PublicGameSessionEntry> Items { get; }

        /// <summary>Cursor for the next page; empty when done.</summary>
        public string NextCursor { get; }
    }

    /// <summary>Options for <see cref="GameSessionsService.ListAsync"/>.</summary>
    public sealed class GameSessionListOptions
    {
        /// <summary>Filter sessions by title id.</summary>
        public string? TitleId { get; set; }

        /// <summary>Page size; server default 50, cap 100.</summary>
        public int Limit { get; set; }

        /// <summary>NextCursor from a prior page.</summary>
        public string? Cursor { get; set; }
    }

    /// <summary>One P2P signal addressed to the caller.</summary>
    public sealed class GameSessionSignal
    {
        internal GameSessionSignal(long id, long fromPlayerId, long toPlayerId, string negotiationId, string kind, string payload, DateTimeOffset createdAt)
        {
            Id = id;
            FromPlayerId = fromPlayerId;
            ToPlayerId = toPlayerId;
            NegotiationId = negotiationId;
            Kind = kind;
            Payload = payload;
            CreatedAt = createdAt;
        }

        /// <summary>Monotonic signal id; pass the highest seen as after_id when polling.</summary>
        public long Id { get; }

        /// <summary>The sending peer.</summary>
        public long FromPlayerId { get; }

        /// <summary>The addressed peer (the caller).</summary>
        public long ToPlayerId { get; }

        /// <summary>Caller-chosen id correlating one offer/answer negotiation.</summary>
        public string NegotiationId { get; }

        /// <summary>Signal kind (see <see cref="GameSessionSignalKind"/>; open set).</summary>
        public string Kind { get; }

        /// <summary>Opaque payload (e.g. base64 SDP), at most 64 KiB.</summary>
        public string Payload { get; }

        /// <summary>Server receive time.</summary>
        public DateTimeOffset CreatedAt { get; }

        internal static GameSessionSignal FromJson(JsonValue v) =>
            new GameSessionSignal(
                v.OptLong("id"),
                v.OptLong("from_player_id"),
                v.OptLong("to_player_id"),
                v.OptString("negotiation_id") ?? string.Empty,
                v.OptString("kind") ?? string.Empty,
                v.OptString("payload") ?? string.Empty,
                v.OptTime("created_at") ?? DateTimeOffset.MinValue);
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
                Operation = "POST /v1/game-session",
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
                Operation = "GET /v1/game-session/{id}",
            }, cancellationToken).ConfigureAwait(false);
            return GameSession.FromJson(resp);
        }

        /// <summary>
        /// Turns a shareable join code into a session id for JoinAsync.
        /// Private sessions resolve only for host/members/invitees.
        /// </summary>
        public async Task<string> ResolveAsync(string joinCode, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest { Method = "GET", Path = "/v1/game-session", Operation = "GET /v1/game-session" };
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
                Operation = "POST /v1/game-session/{id}/join",
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
                Operation = "POST /v1/game-session/{id}/heartbeat",
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
                Operation = "DELETE /v1/game-session/{id}",
            }, cancellationToken);
        }

        /// <summary>
        /// Pages through the public session browser: open, public, non-full
        /// sessions with a recent heartbeat, newest first.
        /// </summary>
        public async Task<GameSessionPage> ListAsync(GameSessionListOptions? options = null, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "GET",
                Path = "/v1/game-sessions",
                Operation = "GET /v1/game-sessions",
            };
            if (!string.IsNullOrEmpty(options?.TitleId))
            {
                req.AddQuery("title_id", options!.TitleId!);
            }
            if (options?.Limit > 0)
            {
                req.AddQuery("limit", options.Limit.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(options?.Cursor))
            {
                req.AddQuery("cursor", options!.Cursor!);
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            var items = new List<PublicGameSessionEntry>();
            var arr = resp.Opt("items");
            if (arr != null)
            {
                foreach (var item in arr.Items)
                {
                    items.Add(PublicGameSessionEntry.FromJson(item));
                }
            }
            return new GameSessionPage(items, resp.OptString("next_cursor") ?? string.Empty);
        }

        /// <summary>
        /// Sends a P2P signal to another member of the session and returns
        /// the new signal id. Both players must be live members of the same
        /// open session; anything else reports IsNotFound. IsRateLimited at
        /// 30 signals per minute per sender per session.
        /// </summary>
        public async Task<long> SendSignalAsync(string sessionId, long toPlayerId, string negotiationId, string kind, string payload, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = SessionPath(sessionId) + "/signals",
                Operation = "POST /v1/game-session/{id}/signals",
                Body = JsonValue.NewObject()
                    .Set("to_player_id", JsonValue.Of(toPlayerId))
                    .Set("negotiation_id", JsonValue.Of(negotiationId))
                    .Set("kind", JsonValue.Of(kind))
                    .Set("payload", JsonValue.Of(payload)),
            }, cancellationToken).ConfigureAwait(false);
            return resp.OptLong("id");
        }

        /// <summary>
        /// Returns signals addressed to the caller in id order. Pass the
        /// highest id already seen as <paramref name="afterId"/> to fetch
        /// only newer ones.
        /// </summary>
        public async Task<IReadOnlyList<GameSessionSignal>> PollSignalsAsync(string sessionId, long afterId = 0, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "GET",
                Path = SessionPath(sessionId) + "/signals",
                Operation = "GET /v1/game-session/{id}/signals",
            };
            if (afterId > 0)
            {
                req.AddQuery("after_id", afterId.ToString(CultureInfo.InvariantCulture));
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            var signals = new List<GameSessionSignal>();
            var arr = resp.Opt("signals");
            if (arr != null)
            {
                foreach (var s in arr.Items)
                {
                    signals.Add(GameSessionSignal.FromJson(s));
                }
            }
            return signals;
        }

        private static string SessionPath(string sessionId) =>
            "/v1/game-session/" + Uri.EscapeDataString(sessionId);
    }
}
