using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>A one-time ticket for /v1/ws?ticket=&lt;ticket&gt;.</summary>
    public sealed class RealtimeTicket
    {
        internal RealtimeTicket(string ticket, int expiresInSeconds)
        {
            Ticket = ticket;
            ExpiresInSeconds = expiresInSeconds;
        }

        /// <summary>The ticket. It works once.</summary>
        public string Ticket { get; }

        /// <summary>The ticket expires after this many seconds.</summary>
        public int ExpiresInSeconds { get; }
    }

    /// <summary>
    /// The realtime REST operations. Reach it via
    /// <see cref="GGScaleClient.Realtime"/>.
    /// </summary>
    public sealed class RealtimeService
    {
        private readonly GGScaleClient _client;

        internal RealtimeService(GGScaleClient client) => _client = client;

        /// <summary>
        /// Gets a one-time WebSocket ticket for the current player. The
        /// ticket works once, within ExpiresInSeconds. A socket adapter that
        /// implements <see cref="ITicketSocketAdapter"/> gets a new ticket
        /// for each connect through this call; the default adapter sends
        /// headers and does not need it. Requires a player session.
        /// </summary>
        public async Task<RealtimeTicket> CreateTicketAsync(CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/ws/ticket",
                Operation = "POST /v1/ws/ticket",
            }, cancellationToken).ConfigureAwait(false);
            return new RealtimeTicket(resp.OptString("ticket") ?? string.Empty, (int)resp.OptLong("expires_in_seconds"));
        }
    }

    /// <summary>
    /// Realtime event types the server sends over /v1/ws
    /// (<see cref="RealtimeMessage.Type"/>). Events are best effort: a
    /// client that misses one recovers the state with the matching GET.
    /// </summary>
    public static class RealtimeEvents
    {
        /// <summary>A ticket of this player matched. Read by WaitForMatchAsync.</summary>
        public const string MatchmakerMatched = MatchmakerService.EventMatchmakerMatched;

        /// <summary>A friend changed status. Payload: <see cref="PresenceEvent"/>.</summary>
        public const string Presence = "presence";

        /// <summary>A friend invited you to a game session. Payload: <see cref="GameInviteEvent"/>.</summary>
        public const string GameInvite = "game_invite";

        /// <summary>
        /// A party you are in, or were removed from, changed. Payload:
        /// <see cref="PartyChangedEvent"/>.
        /// </summary>
        public const string PartyChanged = "party_changed";

        /// <summary>
        /// A friend invited you to a party. Payload: <see cref="PartyInviteEvent"/>.
        /// A re-invite of a pending invite sends no new event.
        /// </summary>
        public const string PartyInvite = "party_invite";
    }

    /// <summary>The payload of <see cref="RealtimeEvents.Presence"/>.</summary>
    public sealed class PresenceEvent
    {
        private PresenceEvent(long playerId, string status, string? sessionId)
        {
            PlayerId = playerId;
            Status = status;
            SessionId = sessionId;
        }

        /// <summary>The friend whose status changed.</summary>
        public long PlayerId { get; }

        /// <summary>The new status, for example "online" or "in_game".</summary>
        public string Status { get; }

        /// <summary>The game session the friend is in; null when none.</summary>
        public string? SessionId { get; }

        /// <summary>Reads the payload of a presence message.</summary>
        public static PresenceEvent FromPayload(JsonValue payload) =>
            new PresenceEvent(payload.OptLong("player_id"), payload.OptString("status") ?? string.Empty, payload.OptString("session_id"));
    }

    /// <summary>The payload of <see cref="RealtimeEvents.GameInvite"/>.</summary>
    public sealed class GameInviteEvent
    {
        private GameInviteEvent(long inviteId, string sessionId, string joinCode)
        {
            InviteId = inviteId;
            SessionId = sessionId;
            JoinCode = joinCode;
        }

        /// <summary>The invite id.</summary>
        public long InviteId { get; }

        /// <summary>The game session of the invite.</summary>
        public string SessionId { get; }

        /// <summary>The join code of the game session.</summary>
        public string JoinCode { get; }

        /// <summary>Reads the payload of a game_invite message.</summary>
        public static GameInviteEvent FromPayload(JsonValue payload) =>
            new GameInviteEvent(payload.OptLong("invite_id"), payload.OptString("session_id") ?? string.Empty, payload.OptString("join_code") ?? string.Empty);
    }

    /// <summary>
    /// The payload of <see cref="RealtimeEvents.PartyChanged"/>. It does not
    /// carry the party: call <see cref="PartiesService.GetAsync"/> when
    /// Version is newer than yours. A 404 means you are no longer a member.
    /// </summary>
    public sealed class PartyChangedEvent
    {
        private PartyChangedEvent(long partyId, long version, string state)
        {
            PartyId = partyId;
            Version = version;
            State = state;
        }

        /// <summary>The party that changed.</summary>
        public long PartyId { get; }

        /// <summary>The new party version.</summary>
        public long Version { get; }

        /// <summary>The new party state (see <see cref="PartyState"/>).</summary>
        public string State { get; }

        /// <summary>Reads the payload of a party_changed message.</summary>
        public static PartyChangedEvent FromPayload(JsonValue payload) =>
            new PartyChangedEvent(payload.OptLong("party_id"), payload.OptLong("version"), payload.OptString("state") ?? string.Empty);
    }

    /// <summary>The payload of <see cref="RealtimeEvents.PartyInvite"/>.</summary>
    public sealed class PartyInviteEvent
    {
        private PartyInviteEvent(long inviteId, long partyId, long fromPlayerId)
        {
            InviteId = inviteId;
            PartyId = partyId;
            FromPlayerId = fromPlayerId;
        }

        /// <summary>The invite id.</summary>
        public long InviteId { get; }

        /// <summary>The party of the invite.</summary>
        public long PartyId { get; }

        /// <summary>The leader who sent the invite.</summary>
        public long FromPlayerId { get; }

        /// <summary>Reads the payload of a party_invite message.</summary>
        public static PartyInviteEvent FromPayload(JsonValue payload) =>
            new PartyInviteEvent(payload.OptLong("invite_id"), payload.OptLong("party_id"), payload.OptLong("from_player_id"));
    }
}
