using System;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>The calling player's profile.</summary>
    public sealed class PlayerProfile
    {
        internal PlayerProfile(long id, long projectId, string externalId, string email, string xuid, string displayName, string friendCode, DateTimeOffset? emailVerifiedAt, DateTimeOffset createdAt)
        {
            Id = id;
            ProjectId = projectId;
            ExternalId = externalId;
            Email = email;
            Xuid = xuid;
            DisplayName = displayName;
            FriendCode = friendCode;
            EmailVerifiedAt = emailVerifiedAt;
            CreatedAt = createdAt;
        }

        /// <summary>The player id in this project.</summary>
        public long Id { get; }

        /// <summary>The owning project id.</summary>
        public long ProjectId { get; }

        /// <summary>Per-game stable identifier (Steam id, anonymous UUID, …).</summary>
        public string ExternalId { get; }

        /// <summary>Email address; empty for anonymous players.</summary>
        public string Email { get; }

        /// <summary>Cross-platform user id (XUID); empty when unset.</summary>
        public string Xuid { get; }

        /// <summary>Player-chosen display name; empty when unset.</summary>
        public string DisplayName { get; }

        /// <summary>
        /// The player's shareable friend code (see
        /// <see cref="PlayersService.ResolveFriendCodeAsync"/>); empty for
        /// players without one.
        /// </summary>
        public string FriendCode { get; }

        /// <summary>When the email was verified; null while unverified.</summary>
        public DateTimeOffset? EmailVerifiedAt { get; }

        /// <summary>Player creation time.</summary>
        public DateTimeOffset CreatedAt { get; }

        internal static PlayerProfile FromJson(JsonValue v) =>
            new PlayerProfile(
                v.OptLong("id"),
                v.OptLong("project_id"),
                v.OptString("external_id") ?? string.Empty,
                v.OptString("email") ?? string.Empty,
                v.OptString("xuid") ?? string.Empty,
                v.OptString("display_name") ?? string.Empty,
                v.OptString("friend_code") ?? string.Empty,
                v.OptTime("email_verified_at"),
                v.OptTime("created_at") ?? DateTimeOffset.MinValue);
    }

    /// <summary>
    /// A PATCH /v1/profile body. Fields are null for "leave alone"; the
    /// server rejects an empty patch with IsBadRequest.
    /// </summary>
    public sealed class ProfilePatch
    {
        /// <summary>New email; setting it triggers a fresh verification mail.</summary>
        public string? Email { get; set; }

        /// <summary>New XUID.</summary>
        public string? Xuid { get; set; }

        /// <summary>New display name.</summary>
        public string? DisplayName { get; set; }

        internal JsonValue ToJson()
        {
            var body = JsonValue.NewObject();
            if (Email != null)
            {
                body.Set("email", JsonValue.Of(Email));
            }
            if (Xuid != null)
            {
                body.Set("xuid", JsonValue.Of(Xuid));
            }
            if (DisplayName != null)
            {
                body.Set("display_name", JsonValue.Of(DisplayName));
            }
            return body;
        }
    }

    /// <summary>
    /// The /v1/profile endpoints. Requires a player session. Reach it via
    /// <see cref="GGScaleClient.Profile"/>.
    /// </summary>
    public sealed class ProfileService
    {
        private readonly GGScaleClient _client;

        internal ProfileService(GGScaleClient client) => _client = client;

        /// <summary>Returns the calling player's profile.</summary>
        public async Task<PlayerProfile> GetAsync(CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/profile",
                Operation = "GET /v1/profile",
            }, cancellationToken).ConfigureAwait(false);
            return PlayerProfile.FromJson(resp);
        }

        /// <summary>Applies a patch to the profile (202/204 on success).</summary>
        public Task UpdateAsync(ProfilePatch patch, CancellationToken cancellationToken = default)
        {
            if (patch == null)
            {
                throw new ArgumentNullException(nameof(patch));
            }
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "PATCH",
                Path = "/v1/profile",
                Operation = "PATCH /v1/profile",
                Body = patch.ToJson(),
            }, cancellationToken);
        }

        /// <summary>
        /// Replaces the caller's friend code with a fresh one and returns
        /// it. The previous code stops resolving immediately. Read the
        /// current code from <see cref="PlayerProfile.FriendCode"/>.
        /// </summary>
        public async Task<string> RegenerateFriendCodeAsync(CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/profile/friend-code",
                Operation = "POST /v1/profile/friend-code",
            }, cancellationToken).ConfigureAwait(false);
            return resp.OptString("friend_code") ?? string.Empty;
        }
    }
}
