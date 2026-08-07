using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>One pending game-session invite addressed to the caller.</summary>
    public sealed class Invite
    {
        internal Invite(long inviteId, string fromEmail, string fromXuid, string sessionId, string joinCode, DateTimeOffset expiresAt)
        {
            InviteId = inviteId;
            FromEmail = fromEmail;
            FromXuid = fromXuid;
            SessionId = sessionId;
            JoinCode = joinCode;
            ExpiresAt = expiresAt;
        }

        /// <summary>The invite id (for Delete).</summary>
        public long InviteId { get; }

        /// <summary>Sender's email; empty when not shared.</summary>
        public string FromEmail { get; }

        /// <summary>Sender's XUID; empty when unset.</summary>
        public string FromXuid { get; }

        /// <summary>The session being invited to.</summary>
        public string SessionId { get; }

        /// <summary>Join code for the session.</summary>
        public string JoinCode { get; }

        /// <summary>When the invite expires (~5 minutes after creation).</summary>
        public DateTimeOffset ExpiresAt { get; }

        internal static Invite FromJson(JsonValue v) =>
            new Invite(
                v.OptLong("invite_id"),
                v.OptString("from_email") ?? string.Empty,
                v.OptString("from_xuid") ?? string.Empty,
                v.OptString("session_id") ?? string.Empty,
                v.OptString("join_code") ?? string.Empty,
                v.OptTime("expires_at") ?? DateTimeOffset.MinValue);
    }

    /// <summary>
    /// The /v1/invite endpoints: invite an accepted friend into a game
    /// session, list pending invites, dismiss them. Invitees connected to
    /// the realtime WebSocket also receive a "game_invite" push the moment
    /// an invite is created; ListAsync covers players who were offline.
    /// Reach it via <see cref="GGScaleClient.Invites"/>.
    /// </summary>
    public sealed class InvitesService
    {
        private readonly GGScaleClient _client;

        internal InvitesService(GGScaleClient client) => _client = client;

        /// <summary>
        /// Invites the player registered under <paramref name="toEmail"/>
        /// into the session and returns the invite id. The recipient must
        /// be an accepted friend and the caller must be in the session
        /// (IsForbidden otherwise); a closed session reports IsConflict.
        /// </summary>
        public async Task<long> CreateAsync(string sessionId, string toEmail, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/invite",
                Operation = "POST /v1/invite",
                Body = JsonValue.NewObject()
                    .Set("to_email", JsonValue.Of(toEmail))
                    .Set("session_id", JsonValue.Of(sessionId)),
            }, cancellationToken).ConfigureAwait(false);
            return resp.OptLong("invite_id");
        }

        /// <summary>Returns the caller's pending, unexpired invites.</summary>
        public async Task<IReadOnlyList<Invite>> ListAsync(CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/invite",
                Operation = "GET /v1/invite",
            }, cancellationToken).ConfigureAwait(false);
            var invites = new List<Invite>();
            var arr = resp.Opt("invites");
            if (arr != null)
            {
                foreach (var i in arr.Items)
                {
                    invites.Add(Invite.FromJson(i));
                }
            }
            return invites;
        }

        /// <summary>Removes an invite — sender cancels, or recipient declines/dismisses.</summary>
        public Task DeleteAsync(long inviteId, CancellationToken cancellationToken = default)
        {
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "DELETE",
                Path = "/v1/invite/" + inviteId.ToString(CultureInfo.InvariantCulture),
                Operation = "DELETE /v1/invite/{id}",
            }, cancellationToken);
        }
    }
}
