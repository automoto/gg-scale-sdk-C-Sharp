using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>Party states (wire strings). Treat the set as open.</summary>
    public static class PartyState
    {
        /// <summary>The party is not in the queue.</summary>
        public const string Idle = "idle";

        /// <summary>The party is in the matchmaking queue.</summary>
        public const string Queued = "queued";

        /// <summary>The party matched; see <see cref="Party.LastMatchId"/>.</summary>
        public const string Matched = "matched";
    }

    /// <summary>
    /// The queue criteria the whole party shares, as the server stores them.
    /// Create and update take a <see cref="MatchRequest"/>.
    /// </summary>
    public sealed class PartySettings
    {
        private PartySettings()
        {
        }

        /// <summary>Result mode (see <see cref="MatchMode"/>).</summary>
        public string Mode { get; private set; } = string.Empty;

        /// <summary>The fleet id for fleet_allocation; 0 otherwise.</summary>
        public long FleetId { get; private set; }

        /// <summary>Preferred region; empty when unset.</summary>
        public string Region { get; private set; } = string.Empty;

        /// <summary>Game mode; empty when unset.</summary>
        public string GameMode { get; private set; } = string.Empty;

        /// <summary>Minimum roster size.</summary>
        public int MinCount { get; private set; }

        /// <summary>Maximum roster size.</summary>
        public int MaxCount { get; private set; }

        /// <summary>Roster-size multiple constraint.</summary>
        public int CountMultiple { get; private set; }

        /// <summary>Whether cross-region matching is permitted.</summary>
        public bool AllowCrossRegion { get; private set; }

        /// <summary>Criteria query expression; empty when unset.</summary>
        public string Query { get; private set; } = string.Empty;

        internal static PartySettings FromJson(JsonValue? v) =>
            v == null
                ? new PartySettings()
                : new PartySettings
                {
                    Mode = v.OptString("mode") ?? string.Empty,
                    FleetId = v.OptLong("fleet_id"),
                    Region = v.OptString("region") ?? string.Empty,
                    GameMode = v.OptString("game_mode") ?? string.Empty,
                    MinCount = (int)v.OptLong("min_count"),
                    MaxCount = (int)v.OptLong("max_count"),
                    CountMultiple = (int)v.OptLong("count_multiple"),
                    AllowCrossRegion = v.OptBool("allow_cross_region"),
                    Query = v.OptString("query") ?? string.Empty,
                };
    }

    /// <summary>One member of a party.</summary>
    public sealed class PartyMember
    {
        private PartyMember()
        {
        }

        /// <summary>The member's latest matchmaking ticket in this party, or 0.</summary>
        public long TicketId { get; private set; }

        /// <summary>The member's player id.</summary>
        public long PlayerId { get; private set; }

        /// <summary>
        /// Equals <see cref="Party.RosterVersion"/> when the member is ready
        /// for the current roster.
        /// </summary>
        public long ReadyVersion { get; private set; }

        /// <summary>The member's string match properties.</summary>
        public IReadOnlyDictionary<string, string> StringProperties { get; private set; } = new Dictionary<string, string>();

        /// <summary>The member's numeric match properties.</summary>
        public IReadOnlyDictionary<string, double> NumericProperties { get; private set; } = new Dictionary<string, double>();

        /// <summary>
        /// The member's attributes, exactly as the member sent them, HTML
        /// included. Escape them before you show them.
        /// </summary>
        public JsonValue Attributes { get; private set; } = JsonValue.Null;

        /// <summary>When the member joined.</summary>
        public DateTimeOffset JoinedAt { get; private set; }

        /// <summary>The member's last heartbeat.</summary>
        public DateTimeOffset LastSeenAt { get; private set; }

        /// <summary>The server removes the member when no heartbeat comes before this time.</summary>
        public DateTimeOffset DisconnectDeadline { get; private set; }

        /// <summary>True when the member is ready for the party's current roster.</summary>
        public bool IsReady(Party party)
        {
            if (party == null)
            {
                throw new ArgumentNullException(nameof(party));
            }
            return ReadyVersion == party.RosterVersion;
        }

        internal static PartyMember FromJson(JsonValue v) =>
            new PartyMember
            {
                TicketId = v.OptLong("ticket_id"),
                PlayerId = v.OptLong("player_id"),
                ReadyVersion = v.OptLong("ready_version"),
                StringProperties = RosterEntry.ReadStringMap(v.Opt("string_properties")),
                NumericProperties = RosterEntry.ReadNumberMap(v.Opt("numeric_properties")),
                Attributes = v.Opt("attributes") ?? JsonValue.Null,
                JoinedAt = v.OptTime("joined_at") ?? DateTimeOffset.MinValue,
                LastSeenAt = v.OptTime("last_seen_at") ?? DateTimeOffset.MinValue,
                DisconnectDeadline = v.OptTime("disconnect_deadline") ?? DateTimeOffset.MinValue,
            };
    }

    /// <summary>The current state of a party.</summary>
    public sealed class Party
    {
        private Party()
        {
        }

        /// <summary>Party id.</summary>
        public long Id { get; private set; }

        /// <summary>The project of the party.</summary>
        public long ProjectId { get; private set; }

        /// <summary>The leader's player id.</summary>
        public long LeaderId { get; private set; }

        /// <summary>The party state (see <see cref="PartyState"/>).</summary>
        public string State { get; private set; } = string.Empty;

        /// <summary>The party version. Writes send it as expected_version.</summary>
        public long Version { get; private set; }

        /// <summary>Goes up when the roster or the settings change; readiness resets.</summary>
        public long RosterVersion { get; private set; }

        /// <summary>The shared queue criteria.</summary>
        public PartySettings Settings { get; private set; } = PartySettings.FromJson(null);

        /// <summary>The member limit.</summary>
        public int MaxMembers { get; private set; }

        /// <summary>The current queue entry, or 0 when the party is not queued.</summary>
        public long CurrentQueueEntryId { get; private set; }

        /// <summary>The last match of the party; empty before the first match.</summary>
        public string LastMatchId { get; private set; } = string.Empty;

        /// <summary>The members.</summary>
        public IReadOnlyList<PartyMember> Members { get; private set; } = Array.Empty<PartyMember>();

        /// <summary>The member with <paramref name="playerId"/>, or null.</summary>
        public PartyMember? Member(long playerId)
        {
            foreach (var m in Members)
            {
                if (m.PlayerId == playerId)
                {
                    return m;
                }
            }
            return null;
        }

        internal static Party FromJson(JsonValue v)
        {
            var members = new List<PartyMember>();
            var arr = v.Opt("members");
            if (arr != null && arr.Kind == JsonKind.Array)
            {
                foreach (var m in arr.Items)
                {
                    members.Add(PartyMember.FromJson(m));
                }
            }
            return new Party
            {
                Id = v.OptLong("id"),
                ProjectId = v.OptLong("project_id"),
                LeaderId = v.OptLong("leader_id"),
                State = v.OptString("state") ?? string.Empty,
                Version = v.OptLong("version"),
                RosterVersion = v.OptLong("roster_version"),
                Settings = PartySettings.FromJson(v.Opt("settings")),
                MaxMembers = (int)v.OptLong("max_members"),
                CurrentQueueEntryId = v.OptLong("current_queue_entry_id"),
                LastMatchId = v.OptString("last_match_id") ?? string.Empty,
                Members = members,
            };
        }
    }

    /// <summary>An invite code that other players redeem with JoinByCodeAsync.</summary>
    public sealed class PartyCode
    {
        private PartyCode()
        {
        }

        /// <summary>Code id, for RevokeCodeAsync.</summary>
        public long Id { get; private set; }

        /// <summary>The code to share.</summary>
        public string Code { get; private set; } = string.Empty;

        /// <summary>When the code expires.</summary>
        public DateTimeOffset ExpiresAt { get; private set; }

        /// <summary>The party version after the code was made.</summary>
        public long PartyVersion { get; private set; }

        internal static PartyCode FromJson(JsonValue v) =>
            new PartyCode
            {
                Id = v.OptLong("id"),
                Code = v.OptString("code") ?? string.Empty,
                ExpiresAt = v.OptTime("expires_at") ?? DateTimeOffset.MinValue,
                PartyVersion = v.OptLong("party_version"),
            };
    }

    /// <summary>A pending invite from a party leader to a friend.</summary>
    public sealed class PartyInvite
    {
        private PartyInvite()
        {
        }

        /// <summary>Invite id.</summary>
        public long Id { get; private set; }

        /// <summary>The party. AcceptInviteAsync and DeclineInviteAsync need PartyVersion.</summary>
        public long PartyId { get; private set; }

        /// <summary>The party version to send with AcceptInviteAsync or DeclineInviteAsync.</summary>
        public long PartyVersion { get; private set; }

        /// <summary>The invited player.</summary>
        public long TargetId { get; private set; }

        /// <summary>When the invite expires.</summary>
        public DateTimeOffset ExpiresAt { get; private set; }

        internal static PartyInvite FromJson(JsonValue v) =>
            new PartyInvite
            {
                Id = v.OptLong("id"),
                PartyId = v.OptLong("party_id"),
                PartyVersion = v.OptLong("party_version"),
                TargetId = v.OptLong("target_id"),
                ExpiresAt = v.OptTime("expires_at") ?? DateTimeOffset.MinValue,
            };
    }

    /// <summary>One member's matchmaking properties, sent with SetReadyAsync.</summary>
    public sealed class PartyProperties
    {
        /// <summary>String match properties.</summary>
        public IReadOnlyDictionary<string, string>? StringProperties { get; set; }

        /// <summary>Numeric match properties.</summary>
        public IReadOnlyDictionary<string, double>? NumericProperties { get; set; }

        /// <summary>
        /// Opaque attributes. The other members see them exactly as sent,
        /// HTML included.
        /// </summary>
        public JsonValue? Attributes { get; set; }

        internal JsonValue ToJson()
        {
            var body = JsonValue.NewObject();
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

    /// <summary>One update from <see cref="PartiesService.WatchAsync"/>. One member is set.</summary>
    public sealed class PartyEvent
    {
        private PartyEvent(Party? party, PartyInviteEvent? invite, MatchResult? match, bool removed)
        {
            Party = party;
            Invite = invite;
            Match = match;
            Removed = removed;
        }

        /// <summary>Set when the party has a newer version than the last one reported.</summary>
        public Party? Party { get; }

        /// <summary>Set when a friend invites the caller to a party.</summary>
        public PartyInviteEvent? Invite { get; }

        /// <summary>Set once for each match of the party.</summary>
        public MatchResult? Match { get; }

        /// <summary>True when the caller is no longer a member. The watch ends after it.</summary>
        public bool Removed { get; }

        internal static PartyEvent ForParty(Party party) => new PartyEvent(party, null, null, false);

        internal static PartyEvent ForInvite(PartyInviteEvent invite) => new PartyEvent(null, invite, null, false);

        internal static PartyEvent ForMatch(MatchResult match) => new PartyEvent(null, null, match, false);

        internal static PartyEvent ForRemoval() => new PartyEvent(null, null, null, true);
    }

    /// <summary>
    /// Thrown by <see cref="PartiesService.WaitForMatchAsync"/> when the
    /// player is no longer a member of the party (left, kicked, disbanded,
    /// or removed by the heartbeat sweep).
    /// </summary>
    public sealed class NotPartyMemberException : Exception
    {
        /// <summary>Creates the exception for <paramref name="partyId"/>.</summary>
        public NotPartyMemberException(long partyId)
            : base("ggscale: not a member of party " + partyId.ToString(CultureInfo.InvariantCulture))
        {
            PartyId = partyId;
        }

        /// <summary>The party the player left.</summary>
        public long PartyId { get; }
    }

    /// <summary>
    /// The /v1/parties and /v1/party-invites endpoints. Reach it via
    /// <see cref="GGScaleClient.Parties"/>. Most writes take the party
    /// version you last saw. A stale version throws with IsStaleVersion:
    /// read the party again and retry. Only the leader can update, disband,
    /// kick, invite, create or revoke codes, queue, cancel the queue, and
    /// rematch; other members get IsForbidden. All calls need a player
    /// session.
    /// </summary>
    public sealed class PartiesService
    {
        private readonly GGScaleClient _client;

        internal PartiesService(GGScaleClient client) => _client = client;

        /// <summary>
        /// The WatchAsync heartbeat cadence. It keeps a member well inside
        /// the server's 30-second disconnect deadline. Set only in tests.
        /// </summary>
        internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Makes a party with the caller as leader. <paramref name="settings"/>
        /// are the shared queue criteria; each member sends their own
        /// properties with <see cref="SetReadyAsync"/>.
        /// </summary>
        public Task<Party> CreateAsync(MatchRequest settings, CancellationToken cancellationToken = default)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            return PartyAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/parties",
                Operation = "POST /v1/parties",
                Body = JsonValue.NewObject().Set("settings", settings.ToJson()),
            }, cancellationToken);
        }

        /// <summary>
        /// Returns the caller's party. Use it to find the party again after
        /// a restart. IsNotFound when the caller is in no party.
        /// </summary>
        public Task<Party> CurrentAsync(CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/parties/current",
                Operation = "GET /v1/parties/current",
            }, cancellationToken);

        /// <summary>
        /// Returns a party. IsNotFound when the party does not exist or the
        /// caller is not a member, for example after a kick.
        /// </summary>
        public Task<Party> GetAsync(long partyId, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "GET",
                Path = PartyPath(partyId, string.Empty),
                Operation = "GET /v1/parties/{id}",
            }, cancellationToken);

        /// <summary>Replaces the queue settings. Leader only. It resets every member's readiness.</summary>
        public Task<Party> UpdateAsync(long partyId, long expectedVersion, MatchRequest settings, CancellationToken cancellationToken = default)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            return PartyAsync(new GGRequest
            {
                Method = "PATCH",
                Path = PartyPath(partyId, string.Empty),
                Operation = "PATCH /v1/parties/{id}",
                Body = VersionBody(expectedVersion).Set("settings", settings.ToJson()),
            }, cancellationToken);
        }

        /// <summary>Removes every member. Leader only.</summary>
        public Task<Party> DisbandAsync(long partyId, long expectedVersion, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "DELETE",
                Path = PartyPath(partyId, string.Empty),
                Operation = "DELETE /v1/parties/{id}",
                Body = VersionBody(expectedVersion),
            }, cancellationToken);

        /// <summary>
        /// Keeps the caller in the party and returns the full party. Each
        /// member must call it within 30 seconds or the server removes the
        /// member. <see cref="WatchAsync"/> calls it for you.
        /// </summary>
        public Task<Party> HeartbeatAsync(long partyId, long expectedVersion, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "POST",
                Path = PartyPath(partyId, "/heartbeat"),
                Operation = "POST /v1/parties/{id}/heartbeat",
                Body = VersionBody(expectedVersion),
            }, cancellationToken);

        /// <summary>
        /// Joins the party of an invite code. IsNotFound when the code is
        /// unknown, expired, revoked, or used up. After too many wrong codes
        /// the exception has IsCodeCooldown set: wait for RetryAfter, which
        /// the server sets.
        /// </summary>
        public Task<Party> JoinByCodeAsync(string code, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/parties/join",
                Operation = "POST /v1/parties/join",
                Body = JsonValue.NewObject().Set("code", JsonValue.Of(code ?? string.Empty)),
            }, cancellationToken);

        /// <summary>Removes the caller from the party.</summary>
        public Task<Party> LeaveAsync(long partyId, long expectedVersion, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "DELETE",
                Path = PartyPath(partyId, "/members/me"),
                Operation = "DELETE /v1/parties/{id}/members/me",
                Body = VersionBody(expectedVersion),
            }, cancellationToken);

        /// <summary>
        /// Sets the caller's readiness and matchmaking properties. The other
        /// members see the attributes exactly as sent.
        /// </summary>
        public Task<Party> SetReadyAsync(long partyId, long expectedVersion, bool ready, PartyProperties? properties = null, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "PUT",
                Path = PartyPath(partyId, "/members/me/ready"),
                Operation = "PUT /v1/parties/{id}/members/me/ready",
                Body = VersionBody(expectedVersion)
                    .Set("ready", JsonValue.Of(ready))
                    .Set("properties", (properties ?? new PartyProperties()).ToJson()),
            }, cancellationToken);

        /// <summary>Removes <paramref name="playerId"/> from the party. Leader only.</summary>
        public Task<Party> KickAsync(long partyId, long expectedVersion, long playerId, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "DELETE",
                Path = PartyPath(partyId, "/members/" + playerId.ToString(CultureInfo.InvariantCulture)),
                Operation = "DELETE /v1/parties/{id}/members/{player_id}",
                Body = VersionBody(expectedVersion),
            }, cancellationToken);

        /// <summary>
        /// Makes a new invite code and revokes the older ones. Leader only.
        /// <paramref name="maxUses"/> is 1 to 7; 0 uses the server default (7).
        /// </summary>
        public async Task<PartyCode> CreateCodeAsync(long partyId, long expectedVersion, int maxUses = 0, CancellationToken cancellationToken = default)
        {
            var body = VersionBody(expectedVersion);
            if (maxUses > 0)
            {
                body.Set("max_uses", JsonValue.Of((long)maxUses));
            }
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = PartyPath(partyId, "/invite-codes"),
                Operation = "POST /v1/parties/{id}/invite-codes",
                Body = body,
            }, cancellationToken).ConfigureAwait(false);
            return PartyCode.FromJson(resp);
        }

        /// <summary>
        /// Revokes an invite code. Leader only. IsNotFound when the code is
        /// unknown or already revoked.
        /// </summary>
        public Task<Party> RevokeCodeAsync(long partyId, long expectedVersion, long codeId, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "DELETE",
                Path = PartyPath(partyId, "/invite-codes/" + codeId.ToString(CultureInfo.InvariantCulture)),
                Operation = "DELETE /v1/parties/{id}/invite-codes/{code_id}",
                Body = VersionBody(expectedVersion),
            }, cancellationToken);

        /// <summary>
        /// Invites an accepted friend. Leader only. A re-invite of a pending
        /// invite only makes its expiry later and sends no new event.
        /// </summary>
        public async Task<PartyInvite> InviteFriendAsync(long partyId, long expectedVersion, long playerId, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = PartyPath(partyId, "/invites"),
                Operation = "POST /v1/parties/{id}/invites",
                Body = VersionBody(expectedVersion).Set("player_id", JsonValue.Of(playerId)),
            }, cancellationToken).ConfigureAwait(false);
            return PartyInvite.FromJson(resp);
        }

        /// <summary>
        /// Returns the caller's pending party invites. This is the source of
        /// truth; the party_invite event is only a hint.
        /// </summary>
        public async Task<IReadOnlyList<PartyInvite>> ListInvitesAsync(CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/party-invites",
                Operation = "GET /v1/party-invites",
            }, cancellationToken).ConfigureAwait(false);
            var list = new List<PartyInvite>();
            if (resp.Kind == JsonKind.Array)
            {
                foreach (var v in resp.Items)
                {
                    list.Add(PartyInvite.FromJson(v));
                }
            }
            return list;
        }

        /// <summary>
        /// Joins the party of an invite. <paramref name="expectedVersion"/>
        /// is the party version, for example <see cref="PartyInvite.PartyVersion"/>.
        /// IsNotFound when the invite is unknown, expired, or not for the caller.
        /// </summary>
        public Task<Party> AcceptInviteAsync(long inviteId, long expectedVersion, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "POST",
                Path = InvitePath(inviteId) + "/accept",
                Operation = "POST /v1/party-invites/{id}/accept",
                Body = VersionBody(expectedVersion),
            }, cancellationToken);

        /// <summary>
        /// Declines an invite, or revokes it when the caller is the leader.
        /// <paramref name="expectedVersion"/> is the party version.
        /// IsNotFound for any other player, and when the invite is unknown
        /// or expired.
        /// </summary>
        public Task DeclineInviteAsync(long inviteId, long expectedVersion, CancellationToken cancellationToken = default) =>
            _client.CallProtectedAsync(new GGRequest
            {
                Method = "DELETE",
                Path = InvitePath(inviteId),
                Operation = "DELETE /v1/party-invites/{id}",
                Body = VersionBody(expectedVersion),
            }, cancellationToken);

        /// <summary>
        /// Puts the ready party in the matchmaking queue. Leader only.
        /// <paramref name="idempotencyKey"/> is sent as Idempotency-Key: pass
        /// the same key to retry a call safely, or null to let the SDK make
        /// one. Because of the key, the SDK retries this call like a read.
        /// Throws with IsPartyEnqueueDisabled when the server turns party
        /// queue off.
        /// </summary>
        public Task<Party> QueueAsync(long partyId, long expectedVersion, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            PartyAsync(WithKey(new GGRequest
            {
                Method = "POST",
                Path = PartyPath(partyId, "/queue"),
                Operation = "POST /v1/parties/{id}/queue",
                Body = VersionBody(expectedVersion),
            }, idempotencyKey), cancellationToken);

        /// <summary>Takes the whole party out of the queue. Leader only.</summary>
        public Task<Party> CancelQueueAsync(long partyId, long expectedVersion, CancellationToken cancellationToken = default) =>
            PartyAsync(new GGRequest
            {
                Method = "DELETE",
                Path = PartyPath(partyId, "/queue"),
                Operation = "DELETE /v1/parties/{id}/queue",
                Body = VersionBody(expectedVersion),
            }, cancellationToken);

        /// <summary>
        /// Queues the party again after <paramref name="lastMatchId"/>.
        /// Leader only. <paramref name="idempotencyKey"/> works as in
        /// <see cref="QueueAsync"/>.
        /// </summary>
        public Task<Party> RematchAsync(long partyId, long expectedVersion, string lastMatchId, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            PartyAsync(WithKey(new GGRequest
            {
                Method = "POST",
                Path = PartyPath(partyId, "/rematch"),
                Operation = "POST /v1/parties/{id}/rematch",
                Body = VersionBody(expectedVersion).Set("last_match_id", JsonValue.Of(lastMatchId ?? string.Empty)),
            }, idempotencyKey), cancellationToken);

        /// <summary>
        /// Watches party <paramref name="partyId"/>: enumerate the result
        /// with <c>await foreach</c>. It reads the party first, then sends a
        /// heartbeat every 10 seconds, and reads party_changed, party_invite
        /// and matchmaker_matched on one realtime connection. A
        /// <see cref="PartyEvent.Party"/> is reported only when its version
        /// is newer than the last one reported. Events are a hint: when one
        /// is lost, the next heartbeat catches up. When the party matches,
        /// <see cref="PartyEvent.Match"/> is reported once, from the event or
        /// from the caller's ticket. When the caller is no longer a member,
        /// <see cref="PartyEvent.Removed"/> is reported and the enumeration
        /// ends. Stop the watch with the cancellation token or by leaving
        /// the loop. The heartbeat runs only while you enumerate, so keep
        /// the loop body short.
        /// With partyId 0 the watch reports party invites only and sends no
        /// heartbeat; it throws the connect error when the realtime
        /// connection cannot open. With a party, a failed connect is logged
        /// (a ws event "party_watch_realtime_unavailable") and the watch
        /// continues on the heartbeat alone, without invites (use
        /// <see cref="ListInvitesAsync"/>). The server keeps one realtime
        /// connection per player, and the watch opens its own, which closes
        /// the player's older one: make the watch the single realtime
        /// reader of a party game.
        /// </summary>
        public async IAsyncEnumerable<PartyEvent> WatchAsync(
            long partyId,
            ISocketAdapter? adapter = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var watch = new PartyWatch(_client, partyId, _client.RequireSession().PlayerId);
            var realtime = await DialForWatchAsync(partyId, adapter, cancellationToken).ConfigureAwait(false);
            using var loop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                if (partyId != 0)
                {
                    await watch.RefreshAsync(loop.Token).ConfigureAwait(false);
                    foreach (var ev in watch.TakeEvents())
                    {
                        yield return ev;
                    }
                    if (watch.Done)
                    {
                        yield break;
                    }
                }

                var read = realtime?.ReadMessageAsync(loop.Token);
                var tick = partyId != 0 ? Task.Delay(HeartbeatInterval, loop.Token) : null;
                while (true)
                {
                    loop.Token.ThrowIfCancellationRequested();
                    if (read == null && tick == null)
                    {
                        // An invites-only watch whose connection closed for
                        // good: nothing more can come.
                        await Task.Delay(Timeout.Infinite, loop.Token).ConfigureAwait(false);
                    }
                    var done = read != null && tick != null
                        ? await Task.WhenAny(read, tick).ConfigureAwait(false)
                        : read ?? tick!;
                    if (done == read)
                    {
                        var msg = await read.ConfigureAwait(false);
                        if (msg == null)
                        {
                            read = null; // closed for good; the heartbeat keeps the party current
                            continue;
                        }
                        read = realtime!.ReadMessageAsync(loop.Token);
                        await watch.HandleAsync(msg, loop.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await done.ConfigureAwait(false); // observe cancellation
                        tick = Task.Delay(HeartbeatInterval, loop.Token);
                        await watch.HeartbeatAsync(loop.Token).ConfigureAwait(false);
                    }
                    foreach (var ev in watch.TakeEvents())
                    {
                        yield return ev;
                    }
                    if (watch.Done)
                    {
                        yield break;
                    }
                }
            }
            finally
            {
                loop.Cancel();
                if (realtime != null)
                {
                    await realtime.CloseAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Waits until party <paramref name="partyId"/> is matched and
        /// returns the match. Call it when the party is queued, for example
        /// after a party_changed event with state "queued". When the party
        /// is already matched, it returns that match. When the queue entry
        /// fails or is cancelled, it throws <see cref="MatchFailedException"/>.
        /// When the caller is no longer a member, it throws
        /// <see cref="NotPartyMemberException"/>. It uses
        /// <see cref="WatchAsync"/>, so it also sends the heartbeat and
        /// opens the player's one realtime connection.
        /// </summary>
        public async Task<MatchResult> WaitForMatchAsync(long partyId, ISocketAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            var me = _client.RequireSession().PlayerId;
            long queuedTicket = 0;
            await foreach (var ev in WatchAsync(partyId, adapter, cancellationToken).ConfigureAwait(false))
            {
                if (ev.Match != null)
                {
                    return ev.Match;
                }
                if (ev.Removed)
                {
                    throw new NotPartyMemberException(partyId);
                }
                if (ev.Party == null)
                {
                    continue;
                }
                var mine = ev.Party.Member(me);
                if (ev.Party.State == PartyState.Queued && mine != null)
                {
                    queuedTicket = mine.TicketId;
                    continue;
                }
                if (ev.Party.State != PartyState.Idle || queuedTicket == 0)
                {
                    continue;
                }
                // The entry left the queue without a match.
                var ticketId = queuedTicket;
                queuedTicket = 0;
                Ticket ticket;
                try
                {
                    ticket = await _client.Matchmaker.GetTicketAsync(ticketId, cancellationToken).ConfigureAwait(false);
                }
                catch (GGScaleException)
                {
                    continue;
                }
                if (MatchmakerService.TryTerminal(ticket, out _, out var failure) && failure != null)
                {
                    throw failure;
                }
            }
            throw new NotPartyMemberException(partyId);
        }

        private async Task<RealtimeClient?> DialForWatchAsync(long partyId, ISocketAdapter? adapter, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.DialRealtimeAsync(adapter, cancellationToken).ConfigureAwait(false);
            }
            catch (GGScaleException ex) when (partyId != 0)
            {
                // The heartbeat keeps the party current; invites need ListInvitesAsync.
                _client.EmitWsEvent(new GGWsEventRecord("party_watch_realtime_unavailable", handshakeStatus: ex.Status));
                return null;
            }
            catch (InvalidOperationException) when (partyId != 0)
            {
                // No BaseUrl: the client cannot open a WebSocket.
                _client.EmitWsEvent(new GGWsEventRecord("party_watch_realtime_unavailable"));
                return null;
            }
        }

        private async Task<Party> PartyAsync(GGRequest request, CancellationToken cancellationToken)
        {
            var resp = await _client.CallProtectedAsync(request, cancellationToken).ConfigureAwait(false);
            return Party.FromJson(resp);
        }

        private static GGRequest WithKey(GGRequest request, string? key)
        {
            request.Headers["Idempotency-Key"] = string.IsNullOrEmpty(key) ? Guid.NewGuid().ToString("N") : key!;
            return request;
        }

        private static JsonValue VersionBody(long expectedVersion) =>
            JsonValue.NewObject().Set("expected_version", JsonValue.Of(expectedVersion));

        private static string PartyPath(long partyId, string suffix) =>
            "/v1/parties/" + partyId.ToString(CultureInfo.InvariantCulture) + suffix;

        private static string InvitePath(long inviteId) =>
            "/v1/party-invites/" + inviteId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The state of one WatchAsync: the last reported version and match.</summary>
    internal sealed class PartyWatch
    {
        private readonly GGScaleClient _client;
        private readonly long _partyId;
        private readonly long _me;
        private readonly List<PartyEvent> _events = new List<PartyEvent>();
        private long _version;
        private string _lastMatch = string.Empty;

        internal PartyWatch(GGScaleClient client, long partyId, long me)
        {
            _client = client;
            _partyId = partyId;
            _me = me;
        }

        /// <summary>True after the caller was removed from the party.</summary>
        internal bool Done { get; private set; }

        internal IReadOnlyList<PartyEvent> TakeEvents()
        {
            var taken = _events.ToArray();
            _events.Clear();
            return taken;
        }

        internal async Task HandleAsync(RealtimeMessage msg, CancellationToken ct)
        {
            switch (msg.Type)
            {
                case RealtimeEvents.PartyInvite:
                    _events.Add(PartyEvent.ForInvite(PartyInviteEvent.FromPayload(msg.Payload)));
                    return;
                case RealtimeEvents.PartyChanged:
                    var changed = PartyChangedEvent.FromPayload(msg.Payload);
                    if (_partyId != 0 && changed.PartyId == _partyId && changed.Version > _version)
                    {
                        await RefreshAsync(ct).ConfigureAwait(false);
                    }
                    return;
                case RealtimeEvents.MatchmakerMatched:
                    if (_partyId != 0)
                    {
                        await MatchedAsync(MatchResult.FromPayload(msg.Payload), ct).ConfigureAwait(false);
                    }
                    return;
            }
        }

        internal async Task RefreshAsync(CancellationToken ct)
        {
            Party party;
            try
            {
                party = await _client.Parties.GetAsync(_partyId, ct).ConfigureAwait(false);
            }
            catch (GGScaleException ex) when (ex.IsNotFound)
            {
                Removed();
                return;
            }
            catch (GGScaleException)
            {
                return; // transient; the next heartbeat tries again
            }
            await UpdateAsync(party, ct).ConfigureAwait(false);
        }

        internal async Task HeartbeatAsync(CancellationToken ct)
        {
            Party party;
            try
            {
                party = await _client.Parties.HeartbeatAsync(_partyId, _version, ct).ConfigureAwait(false);
            }
            catch (GGScaleException ex) when (ex.IsNotFound)
            {
                Removed();
                return;
            }
            catch (GGScaleException ex) when (ex.IsStaleVersion)
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (GGScaleException)
            {
                return; // transient; the next heartbeat tries again
            }
            await UpdateAsync(party, ct).ConfigureAwait(false);
        }

        private void Removed()
        {
            _events.Add(PartyEvent.ForRemoval());
            Done = true;
        }

        /// <summary>
        /// Reports a matchmaker_matched event when its roster has the caller
        /// as a member of this party. The ticket is the complete result; the
        /// event is the fallback.
        /// </summary>
        private async Task MatchedAsync(MatchResult pushed, CancellationToken ct)
        {
            if (pushed.MatchId == _lastMatch)
            {
                return;
            }
            var inParty = false;
            foreach (var u in pushed.Users)
            {
                if (u.PlayerId == _me && u.PartyId == _partyId)
                {
                    inParty = true;
                    break;
                }
            }
            if (!inParty)
            {
                return;
            }
            var result = pushed;
            try
            {
                var t = await _client.Matchmaker.GetTicketAsync(pushed.TicketId, ct).ConfigureAwait(false);
                if (t.Status == "matched")
                {
                    result = MatchResult.FromTicket(t);
                }
            }
            catch (GGScaleException)
            {
                // Keep the pushed result.
            }
            _lastMatch = pushed.MatchId;
            _events.Add(PartyEvent.ForMatch(result));
        }

        /// <summary>
        /// Reports a newer party, then reports its match from the caller's
        /// ticket when the realtime event did not.
        /// </summary>
        private async Task UpdateAsync(Party party, CancellationToken ct)
        {
            if (party.Version > _version)
            {
                _version = party.Version;
                _events.Add(PartyEvent.ForParty(party));
            }
            if (party.State != PartyState.Matched || party.LastMatchId.Length == 0 || party.LastMatchId == _lastMatch)
            {
                return;
            }
            var mine = party.Member(_me);
            if (mine == null || mine.TicketId == 0)
            {
                return;
            }
            Ticket t;
            try
            {
                t = await _client.Matchmaker.GetTicketAsync(mine.TicketId, ct).ConfigureAwait(false);
            }
            catch (GGScaleException)
            {
                return; // the next heartbeat tries again
            }
            if (t.Status != "matched" || t.MatchId != party.LastMatchId)
            {
                return;
            }
            _lastMatch = t.MatchId;
            _events.Add(PartyEvent.ForMatch(MatchResult.FromTicket(t)));
        }
    }
}
