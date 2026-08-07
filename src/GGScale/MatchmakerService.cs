using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>Matchmaking result modes (wire strings).</summary>
    public static class MatchMode
    {
        /// <summary>Bare roster; peers connect to each other (BYO-signaling P2P).</summary>
        public const string MatchOnly = "match_only";

        /// <summary>A joinable game session for the roster (host/listen-server P2P).</summary>
        public const string GameSession = "game_session";

        /// <summary>A dedicated server allocation (beta).</summary>
        public const string FleetAllocation = "fleet_allocation";
    }

    /// <summary>One matched player, including the attributes they queued with.</summary>
    public sealed class RosterEntry
    {
        internal RosterEntry(long playerId, string region, IReadOnlyDictionary<string, string> stringProperties, IReadOnlyDictionary<string, double> numericProperties, JsonValue attributes)
        {
            PlayerId = playerId;
            Region = region;
            StringProperties = stringProperties;
            NumericProperties = numericProperties;
            Attributes = attributes;
        }

        /// <summary>The matched player's id.</summary>
        public long PlayerId { get; }

        /// <summary>The player's region; empty when unset.</summary>
        public string Region { get; }

        /// <summary>The player's string match properties.</summary>
        public IReadOnlyDictionary<string, string> StringProperties { get; }

        /// <summary>The player's numeric match properties.</summary>
        public IReadOnlyDictionary<string, double> NumericProperties { get; }

        /// <summary>The player's opaque attributes, visible to every matched peer.</summary>
        public JsonValue Attributes { get; }

        internal static RosterEntry FromJson(JsonValue v) =>
            new RosterEntry(
                v.OptLong("player_id"),
                v.OptString("region") ?? string.Empty,
                ReadStringMap(v.Opt("string_properties")),
                ReadNumberMap(v.Opt("numeric_properties")),
                v.Opt("attributes") ?? JsonValue.Null);

        internal static IReadOnlyList<RosterEntry> ParseList(JsonValue? arr)
        {
            var list = new List<RosterEntry>();
            if (arr != null && arr.Kind == JsonKind.Array)
            {
                foreach (var e in arr.Items)
                {
                    list.Add(FromJson(e));
                }
            }
            return list;
        }

        private static Dictionary<string, string> ReadStringMap(JsonValue? v)
        {
            var map = new Dictionary<string, string>();
            if (v != null && v.Kind == JsonKind.Object)
            {
                foreach (var kv in v.Members)
                {
                    map[kv.Key] = kv.Value.AsString();
                }
            }
            return map;
        }

        private static Dictionary<string, double> ReadNumberMap(JsonValue? v)
        {
            var map = new Dictionary<string, double>();
            if (v != null && v.Kind == JsonKind.Object)
            {
                foreach (var kv in v.Members)
                {
                    map[kv.Key] = kv.Value.AsDouble();
                }
            }
            return map;
        }
    }

    /// <summary>One in-flight (or settled) matchmaking request.</summary>
    public sealed class Ticket
    {
        internal Ticket(long id, string status, string mode, string region, bool allowCrossRegion, string gameMode, int minCount, int maxCount, int countMultiple, JsonValue attributes, string matchId, string matchAddress, string protocolHint, string sessionId, string joinCode, long hostPlayerId, string failureReason, IReadOnlyList<RosterEntry> users, DateTimeOffset createdAt, DateTimeOffset? matchedAt, DateTimeOffset? expiresAt)
        {
            Id = id;
            Status = status;
            Mode = mode;
            Region = region;
            AllowCrossRegion = allowCrossRegion;
            GameMode = gameMode;
            MinCount = minCount;
            MaxCount = maxCount;
            CountMultiple = countMultiple;
            Attributes = attributes;
            MatchId = matchId;
            MatchAddress = matchAddress;
            ProtocolHint = protocolHint;
            SessionId = sessionId;
            JoinCode = joinCode;
            HostPlayerId = hostPlayerId;
            FailureReason = failureReason;
            Users = users;
            CreatedAt = createdAt;
            MatchedAt = matchedAt;
            ExpiresAt = expiresAt;
        }

        /// <summary>Ticket id.</summary>
        public long Id { get; }

        /// <summary>queued, matched, cancelled, or failed.</summary>
        public string Status { get; }

        /// <summary>Result mode: match_only, game_session, or fleet_allocation.</summary>
        public string Mode { get; }

        /// <summary>Requested region.</summary>
        public string Region { get; }

        /// <summary>Whether cross-region matching is permitted.</summary>
        public bool AllowCrossRegion { get; }

        /// <summary>Requested game mode.</summary>
        public string GameMode { get; }

        /// <summary>Minimum roster size.</summary>
        public int MinCount { get; }

        /// <summary>Maximum roster size.</summary>
        public int MaxCount { get; }

        /// <summary>Roster-size multiple constraint.</summary>
        public int CountMultiple { get; }

        /// <summary>Opaque matchmaking attributes.</summary>
        public JsonValue Attributes { get; }

        /// <summary>The match id once matched; empty before.</summary>
        public string MatchId { get; }

        /// <summary>Game-server address for fleet_allocation; empty otherwise.</summary>
        public string MatchAddress { get; }

        /// <summary>Optional transport hint from the allocator.</summary>
        public string ProtocolHint { get; }

        /// <summary>Session id for game_session matches; empty otherwise.</summary>
        public string SessionId { get; }

        /// <summary>Session join code for game_session matches; empty otherwise.</summary>
        public string JoinCode { get; }

        /// <summary>The host player for match_only/game_session; 0 for fleet_allocation.</summary>
        public long HostPlayerId { get; }

        /// <summary>Machine-readable reason for a failed ticket (open enum); empty otherwise.</summary>
        public string FailureReason { get; }

        /// <summary>The match roster once matched.</summary>
        public IReadOnlyList<RosterEntry> Users { get; }

        /// <summary>Ticket creation time.</summary>
        public DateTimeOffset CreatedAt { get; }

        /// <summary>When the ticket matched; null before.</summary>
        public DateTimeOffset? MatchedAt { get; }

        /// <summary>When the queued ticket expires; null when no TTL.</summary>
        public DateTimeOffset? ExpiresAt { get; }

        internal static Ticket FromJson(JsonValue v) =>
            new Ticket(
                v.OptLong("id"),
                v.OptString("status") ?? string.Empty,
                v.OptString("mode") ?? string.Empty,
                v.OptString("region") ?? string.Empty,
                v.OptBool("allow_cross_region"),
                v.OptString("game_mode") ?? string.Empty,
                (int)v.OptLong("min_count"),
                (int)v.OptLong("max_count"),
                (int)v.OptLong("count_multiple"),
                v.Opt("attributes") ?? JsonValue.Null,
                v.OptString("match_id") ?? string.Empty,
                v.OptString("match_address") ?? string.Empty,
                v.OptString("protocol_hint") ?? string.Empty,
                v.OptString("session_id") ?? string.Empty,
                v.OptString("join_code") ?? string.Empty,
                v.OptLong("host_player_id"),
                v.OptString("failure_reason") ?? string.Empty,
                RosterEntry.ParseList(v.Opt("users")),
                v.OptTime("created_at") ?? DateTimeOffset.MinValue,
                v.OptTime("matched_at"),
                v.OptTime("expires_at"));
    }

    /// <summary>Input to CreateTicketAsync / WaitForMatchAsync.</summary>
    public sealed class MatchRequest
    {
        /// <summary>Result mode; leave empty to let the server infer it.</summary>
        public string? Mode { get; set; }

        /// <summary>Target fleet name (fleet_allocation only).</summary>
        public string? Fleet { get; set; }

        /// <summary>Preferred region.</summary>
        public string? Region { get; set; }

        /// <summary>Whether to allow cross-region matching (non-fleet modes).</summary>
        public bool? AllowCrossRegion { get; set; }

        /// <summary>Game mode to match on.</summary>
        public string? GameMode { get; set; }

        /// <summary>Minimum roster size.</summary>
        public int MinCount { get; set; }

        /// <summary>Maximum roster size.</summary>
        public int MaxCount { get; set; }

        /// <summary>Roster-size multiple constraint.</summary>
        public int CountMultiple { get; set; }

        /// <summary>Criteria query expression.</summary>
        public string? Query { get; set; }

        /// <summary>String match properties.</summary>
        public IReadOnlyDictionary<string, string>? StringProperties { get; set; }

        /// <summary>Numeric match properties.</summary>
        public IReadOnlyDictionary<string, double>? NumericProperties { get; set; }

        /// <summary>Opaque attributes echoed to matched peers (raw JSON object, 4 KiB cap).</summary>
        public JsonValue? Attributes { get; set; }

        internal JsonValue ToJson()
        {
            var body = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(Mode))
            {
                body.Set("mode", JsonValue.Of(Mode!));
            }
            if (!string.IsNullOrEmpty(Fleet))
            {
                body.Set("fleet", JsonValue.Of(Fleet!));
            }
            if (!string.IsNullOrEmpty(Region))
            {
                body.Set("region", JsonValue.Of(Region!));
            }
            if (AllowCrossRegion.HasValue)
            {
                body.Set("allow_cross_region", JsonValue.Of(AllowCrossRegion.Value));
            }
            if (!string.IsNullOrEmpty(GameMode))
            {
                body.Set("game_mode", JsonValue.Of(GameMode!));
            }
            if (MinCount > 0)
            {
                body.Set("min_count", JsonValue.Of((long)MinCount));
            }
            if (MaxCount > 0)
            {
                body.Set("max_count", JsonValue.Of((long)MaxCount));
            }
            if (CountMultiple > 0)
            {
                body.Set("count_multiple", JsonValue.Of((long)CountMultiple));
            }
            if (!string.IsNullOrEmpty(Query))
            {
                body.Set("query", JsonValue.Of(Query!));
            }
            if (StringProperties != null && StringProperties.Count > 0)
            {
                var o = JsonValue.NewObject();
                foreach (var kv in StringProperties)
                {
                    o.Set(kv.Key, JsonValue.Of(kv.Value));
                }
                body.Set("string_properties", o);
            }
            if (NumericProperties != null && NumericProperties.Count > 0)
            {
                var o = JsonValue.NewObject();
                foreach (var kv in NumericProperties)
                {
                    o.Set(kv.Key, JsonValue.Of(kv.Value));
                }
                body.Set("numeric_properties", o);
            }
            if (Attributes != null)
            {
                body.Set("attributes", Attributes);
            }
            return body;
        }
    }

    /// <summary>
    /// The unified match outcome returned by WaitForMatchAsync across every
    /// mode, parsed from the matchmaker_matched event or recovered from a
    /// polled ticket.
    /// </summary>
    public sealed class MatchResult
    {
        internal MatchResult(long ticketId, string matchId, string mode, string address, string protocolHint, string sessionId, string joinCode, long hostPlayerId, IReadOnlyList<RosterEntry> users)
        {
            TicketId = ticketId;
            MatchId = matchId;
            Mode = mode;
            Address = address;
            ProtocolHint = protocolHint;
            SessionId = sessionId;
            JoinCode = joinCode;
            HostPlayerId = hostPlayerId;
            Users = users;
        }

        /// <summary>The ticket that matched.</summary>
        public long TicketId { get; }

        /// <summary>The match id.</summary>
        public string MatchId { get; }

        /// <summary>Result mode.</summary>
        public string Mode { get; }

        /// <summary>Game-server address (fleet_allocation).</summary>
        public string Address { get; }

        /// <summary>Transport hint (fleet_allocation).</summary>
        public string ProtocolHint { get; }

        /// <summary>Session id (game_session).</summary>
        public string SessionId { get; }

        /// <summary>Session join code (game_session).</summary>
        public string JoinCode { get; }

        /// <summary>The host player for match_only/game_session; 0 for fleet_allocation.</summary>
        public long HostPlayerId { get; }

        /// <summary>The full roster, each entry carrying the peer's attributes.</summary>
        public IReadOnlyList<RosterEntry> Users { get; }

        internal static MatchResult FromTicket(Ticket t) =>
            new MatchResult(t.Id, t.MatchId, t.Mode, t.MatchAddress, t.ProtocolHint, t.SessionId, t.JoinCode, t.HostPlayerId, t.Users);

        internal static MatchResult FromPayload(JsonValue p) =>
            new MatchResult(
                p.OptLong("ticket_id"),
                p.OptString("match_id") ?? string.Empty,
                p.OptString("mode") ?? string.Empty,
                p.OptString("address") ?? string.Empty,
                p.OptString("protocol_hint") ?? string.Empty,
                p.OptString("session_id") ?? string.Empty,
                p.OptString("join_code") ?? string.Empty,
                p.OptLong("host_player_id"),
                RosterEntry.ParseList(p.Opt("users")));
    }

    /// <summary>Thrown by WaitForMatchAsync when the ticket ends in a failed
    /// (or cancelled) state rather than matching.</summary>
    public sealed class MatchFailedException : Exception
    {
        /// <summary>Creates the exception with the server's failure reason.</summary>
        public MatchFailedException(string reason)
            : base(string.IsNullOrEmpty(reason) ? "ggscale: matchmaking failed" : "ggscale: matchmaking failed: " + reason)
        {
            Reason = reason ?? string.Empty;
        }

        /// <summary>Machine-readable reason ("expired", "attempts_exhausted", "cancelled").</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// Everything a peer needs to connect after a peer-to-peer match: the
    /// unified result (host + roster), TURN relay credentials scoped to the
    /// match (null when the relay is disabled), and — for game_session — the
    /// joined session with the current peer endpoints. The SDK gathers the
    /// coordination data; opening the actual peer connections is the game's
    /// responsibility.
    /// </summary>
    public sealed class P2PMatch
    {
        internal P2PMatch(MatchResult result, RelayCredentials? relay, GameSession? session, bool isHost)
        {
            Result = result;
            Relay = relay;
            Session = session;
            IsHost = isHost;
        }

        /// <summary>The unified match result.</summary>
        public MatchResult Result { get; }

        /// <summary>TURN relay credentials for NAT-traversal fallback; null when unavailable.</summary>
        public RelayCredentials? Relay { get; }

        /// <summary>The joined game session (game_session mode); null for match_only.</summary>
        public GameSession? Session { get; }

        /// <summary>Whether the local player is the designated host.</summary>
        public bool IsHost { get; }
    }

    /// <summary>
    /// The /v1/matchmaker/tickets endpoints. Reach it via
    /// <see cref="GGScaleClient.Matchmaker"/>.
    /// </summary>
    public sealed class MatchmakerService
    {
        /// <summary>The realtime envelope type pushed when a ticket is matched.</summary>
        public const string EventMatchmakerMatched = "matchmaker_matched";

        private readonly GGScaleClient _client;

        internal MatchmakerService(GGScaleClient client) => _client = client;

        /// <summary>WaitForMatchAsync recovery poll cadence. Set only in tests.</summary>
        internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Enqueues a matchmaking ticket (starts "queued"). Use
        /// WaitForMatchAsync for the high-level helper that waits for the
        /// match. A player may hold only one active ticket per project; a
        /// second create throws a <see cref="GGScaleException"/> with
        /// IsTicketAlreadyActive set (and ActiveTicketId naming the ticket to
        /// cancel).
        /// </summary>
        public async Task<Ticket> CreateTicketAsync(MatchRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/matchmaker/tickets",
                Operation = "POST /v1/matchmaker/tickets",
                Body = request.ToJson(),
            }, cancellationToken).ConfigureAwait(false);
            return Ticket.FromJson(resp);
        }

        /// <summary>Returns a ticket by id (IsNotFound for unknown/foreign tickets).</summary>
        public async Task<Ticket> GetTicketAsync(long id, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = TicketPath(id),
                Operation = "GET /v1/matchmaker/tickets/{id}",
            }, cancellationToken).ConfigureAwait(false);
            return Ticket.FromJson(resp);
        }

        /// <summary>Cancels a queued ticket (IsConflict once it reached a terminal status).</summary>
        public Task CancelTicketAsync(long id, CancellationToken cancellationToken = default)
        {
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "DELETE",
                Path = TicketPath(id),
                Operation = "DELETE /v1/matchmaker/tickets/{id}",
            }, cancellationToken);
        }

        /// <summary>
        /// Creates a ticket and waits until it is matched, returning the
        /// unified result for any mode. It combines the realtime push with
        /// periodic authenticated ticket polling, so a dropped WebSocket still
        /// returns the persisted match before its TTL. The socket is dialed
        /// BEFORE the ticket is created (a late subscriber would miss the
        /// push). A failed ticket throws <see cref="MatchFailedException"/>;
        /// on cancellation the ticket is best-effort cancelled.
        /// </summary>
        public async Task<MatchResult> WaitForMatchAsync(MatchRequest request, ISocketAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // Dial realtime BEFORE creating the ticket; best-effort so a relay
            // outage or missing session falls back to polling-only.
            RealtimeClient? realtime = null;
            try
            {
                realtime = await _client.DialRealtimeAsync(adapter, cancellationToken).ConfigureAwait(false);
            }
            catch (GGScaleException)
            {
                realtime = null;
            }
            catch (InvalidOperationException)
            {
                realtime = null;
            }

            try
            {
                var ticket = await CreateTicketAsync(request, cancellationToken).ConfigureAwait(false);
                if (TryTerminal(ticket, out var immediate, out var immediateFailure))
                {
                    return immediateFailure != null ? throw immediateFailure : immediate!;
                }

                Task<RealtimeMessage?>? readTask = realtime?.ReadMessageAsync(cancellationToken);
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (readTask != null)
                        {
                            var pollDelay = Task.Delay(PollInterval, cancellationToken);
                            var completed = await Task.WhenAny(readTask, pollDelay).ConfigureAwait(false);
                            if (completed == readTask)
                            {
                                RealtimeMessage? msg = await readTask.ConfigureAwait(false);
                                if (msg == null)
                                {
                                    readTask = null; // WS closed; keep polling
                                    continue;
                                }
                                if (msg.Type == EventMatchmakerMatched)
                                {
                                    var res = MatchResult.FromPayload(msg.Payload);
                                    if (res.TicketId == 0 || res.TicketId == ticket.Id)
                                    {
                                        return res;
                                    }
                                }
                                readTask = realtime!.ReadMessageAsync(cancellationToken);
                                continue;
                            }
                            await pollDelay.ConfigureAwait(false); // observe cancellation
                        }
                        else
                        {
                            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                        }

                        var polled = await PollTicketAsync(ticket.Id, cancellationToken).ConfigureAwait(false);
                        if (polled != null && TryTerminal(polled, out var res2, out var failure2))
                        {
                            return failure2 != null ? throw failure2 : res2!;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    await CancelBestEffortAsync(ticket.Id).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                if (realtime != null)
                {
                    await realtime.CloseAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Legacy alias for <see cref="WaitForMatchAsync"/>, returning the
        /// same unified result.
        /// </summary>
        public Task<MatchResult> RequestMatchAsync(MatchRequest request, ISocketAdapter? adapter = null, CancellationToken cancellationToken = default) =>
            WaitForMatchAsync(request, adapter, cancellationToken);

        /// <summary>
        /// Waits for a peer-to-peer match (match_only or game_session),
        /// fetches TURN relay credentials scoped to the match, and — for
        /// game_session — joins the session announcing <paramref name="selfAddr"/>
        /// so peers can discover this player's endpoint. <paramref name="selfAddr"/>
        /// is the local player's public address (typically learned via STUN).
        /// Throws for fleet_allocation matches (use <see cref="MatchResult.Address"/>).
        /// </summary>
        public async Task<P2PMatch> ConnectP2PAsync(MatchRequest request, GameSessionAddr selfAddr, ISocketAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            if (selfAddr == null)
            {
                throw new ArgumentNullException(nameof(selfAddr));
            }
            var result = await WaitForMatchAsync(request, adapter, cancellationToken).ConfigureAwait(false);
            if (result.Mode == MatchMode.FleetAllocation)
            {
                throw new InvalidOperationException("ggscale: ConnectP2PAsync does not apply to fleet_allocation matches; use MatchResult.Address");
            }

            var session = _client.Session;
            var isHost = session != null && session.PlayerId == result.HostPlayerId;

            RelayCredentials? relay = null;
            try
            {
                relay = await _client.Relay.GetCredentialsAsync(result.MatchId, cancellationToken).ConfigureAwait(false);
            }
            catch (GGScaleException)
            {
                relay = null; // relay disabled for the project, or not permitted
            }

            GameSession? joined = null;
            if (result.Mode == MatchMode.GameSession && result.SessionId.Length > 0)
            {
                joined = await _client.GameSessions.JoinAsync(result.SessionId, selfAddr, cancellationToken).ConfigureAwait(false);
            }
            return new P2PMatch(result, relay, joined, isHost);
        }

        private static bool TryTerminal(Ticket t, out MatchResult? result, out MatchFailedException? failure)
        {
            result = null;
            failure = null;
            switch (t.Status)
            {
                case "matched":
                    result = MatchResult.FromTicket(t);
                    return true;
                case "failed":
                    failure = new MatchFailedException(t.FailureReason);
                    return true;
                case "cancelled":
                    failure = new MatchFailedException("cancelled");
                    return true;
                default:
                    return false;
            }
        }

        private async Task<Ticket?> PollTicketAsync(long id, CancellationToken cancellationToken)
        {
            try
            {
                return await GetTicketAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (GGScaleException)
            {
                return null; // transient; retry on the next tick
            }
        }

        private async Task CancelBestEffortAsync(long ticketId)
        {
            try
            {
                await CancelTicketAsync(ticketId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (GGScaleException)
            {
                // The ticket may already be matched or gone; nothing to do.
            }
        }

        private static string TicketPath(long id) =>
            "/v1/matchmaker/tickets/" + id.ToString(CultureInfo.InvariantCulture);
    }
}
