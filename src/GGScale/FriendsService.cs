using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>A friend's live presence (accepted friendships only).</summary>
    public sealed class FriendPresence
    {
        internal FriendPresence(string status, string? sessionId)
        {
            Status = status;
            SessionId = sessionId;
        }

        /// <summary>Free-form presence status (e.g. "online").</summary>
        public string Status { get; }

        /// <summary>The game session the friend shared, when any.</summary>
        public string? SessionId { get; }
    }

    /// <summary>One edge in the caller's friends list.</summary>
    public sealed class FriendInfo
    {
        internal FriendInfo(long id, string accountId, long? playerId, string status, string? email, string? displayName, FriendPresence? presence, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        {
            Id = id;
            AccountId = accountId;
            PlayerId = playerId;
            Status = status;
            Email = email;
            DisplayName = displayName;
            Presence = presence;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
        }

        /// <summary>The edge id (used as the paging cursor).</summary>
        public long Id { get; }

        /// <summary>The friend's global account id (UUID).</summary>
        public string AccountId { get; }

        /// <summary>The friend's player id in this project, when they have one.</summary>
        public long? PlayerId { get; }

        /// <summary>Edge status: pending, accepted, rejected, or blocked.</summary>
        public string Status { get; }

        /// <summary>The friend's email, when shared.</summary>
        public string? Email { get; }

        /// <summary>The friend's display name, when set.</summary>
        public string? DisplayName { get; }

        /// <summary>Live presence; only present on accepted friendships.</summary>
        public FriendPresence? Presence { get; }

        /// <summary>Edge creation time.</summary>
        public DateTimeOffset CreatedAt { get; }

        /// <summary>Last status change.</summary>
        public DateTimeOffset UpdatedAt { get; }

        internal static FriendInfo FromJson(JsonValue v)
        {
            FriendPresence? presence = null;
            var p = v.Opt("presence");
            if (p != null)
            {
                presence = new FriendPresence(p.OptString("status") ?? string.Empty, p.OptString("session_id"));
            }
            var player = v.Opt("player_id");
            return new FriendInfo(
                v.OptLong("id"),
                v.OptString("account_id") ?? string.Empty,
                player?.AsLong(),
                v.OptString("status") ?? string.Empty,
                v.OptString("email"),
                v.OptString("display_name"),
                presence,
                v.OptTime("created_at") ?? DateTimeOffset.MinValue,
                v.OptTime("updated_at") ?? DateTimeOffset.MinValue);
        }
    }

    /// <summary>One page of friends; NextCursor is empty on the last page.</summary>
    public sealed class FriendsPage
    {
        internal FriendsPage(IReadOnlyList<FriendInfo> items, string nextCursor)
        {
            Items = items;
            NextCursor = nextCursor;
        }

        /// <summary>The page's friend edges.</summary>
        public IReadOnlyList<FriendInfo> Items { get; }

        /// <summary>Cursor for the next page; empty when done.</summary>
        public string NextCursor { get; }
    }

    /// <summary>Options for <see cref="FriendsService.ListAsync"/>.</summary>
    public sealed class FriendsListOptions
    {
        /// <summary>Edge status filter: pending, accepted, rejected, blocked. Empty = accepted.</summary>
        public string? Status { get; set; }

        /// <summary>Page size; server default 50, cap 100.</summary>
        public int Limit { get; set; }

        /// <summary>NextCursor from a prior page.</summary>
        public string? Cursor { get; set; }
    }

    /// <summary>
    /// The /v1/friends endpoints. Friendships are account-scoped: both
    /// players need linked (non-anonymous) accounts, otherwise the server
    /// answers with IsForbidden. Reach it via
    /// <see cref="GGScaleClient.Friends"/>.
    /// </summary>
    public sealed class FriendsService
    {
        private readonly GGScaleClient _client;

        internal FriendsService(GGScaleClient client) => _client = client;

        /// <summary>Lists the caller's friend edges filtered by status.</summary>
        public async Task<FriendsPage> ListAsync(FriendsListOptions? options = null, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest { Method = "GET", Path = "/v1/friends" };
            if (!string.IsNullOrEmpty(options?.Status))
            {
                req.AddQuery("status", options!.Status!);
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
            var items = new List<FriendInfo>();
            var arr = resp.Opt("items");
            if (arr != null)
            {
                foreach (var item in arr.Items)
                {
                    items.Add(FriendInfo.FromJson(item));
                }
            }
            return new FriendsPage(items, resp.OptString("next_cursor") ?? string.Empty);
        }

        /// <summary>
        /// Sends (or re-sends) a friend request and returns the resulting
        /// edge status — "pending" for a fresh request, or the existing
        /// status. A blocked or unknown target reports IsNotFound; the
        /// server never reveals which.
        /// </summary>
        public async Task<string> RequestAsync(long playerId, CancellationToken cancellationToken = default)
        {
            var resp = await PostAsync(playerId, "/request", cancellationToken).ConfigureAwait(false);
            return resp.OptString("status") ?? string.Empty;
        }

        /// <summary>Accepts a pending request; IsConflict when not acceptable.</summary>
        public Task AcceptAsync(long playerId, CancellationToken cancellationToken = default) =>
            PostAsync(playerId, "/accept", cancellationToken);

        /// <summary>Declines a pending (or revokes an accepted) request.</summary>
        public Task RejectAsync(long playerId, CancellationToken cancellationToken = default) =>
            PostAsync(playerId, "/reject", cancellationToken);

        /// <summary>Deletes the friend edge in either direction.</summary>
        public Task RemoveAsync(long playerId, CancellationToken cancellationToken = default)
        {
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "DELETE",
                Path = FriendPath(playerId),
            }, cancellationToken);
        }

        /// <summary>
        /// Blocks the player: any friendship is severed and their future
        /// requests are silently swallowed (they see IsNotFound, never the
        /// block).
        /// </summary>
        public Task BlockAsync(long playerId, CancellationToken cancellationToken = default) =>
            PostAsync(playerId, "/block", cancellationToken);

        /// <summary>Removes a block; does not restore any severed friendship.</summary>
        public Task UnblockAsync(long playerId, CancellationToken cancellationToken = default) =>
            PostAsync(playerId, "/unblock", cancellationToken);

        /// <summary>
        /// Returns the remote addresses an ACCEPTED friend published (see
        /// <see cref="AccountService.SetRemoteAddrsAsync"/>). Non-friends
        /// and blocked pairs get IsForbidden.
        /// </summary>
        public async Task<IReadOnlyList<RemoteAddr>> RemoteAddrsAsync(long playerId, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = FriendPath(playerId) + "/remote-addrs",
            }, cancellationToken).ConfigureAwait(false);
            return RemoteAddr.ListFromJson(resp);
        }

        private Task<JsonValue> PostAsync(long playerId, string action, CancellationToken cancellationToken)
        {
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = FriendPath(playerId) + action,
            }, cancellationToken);
        }

        private static string FriendPath(long playerId) =>
            "/v1/friends/" + playerId.ToString(CultureInfo.InvariantCulture);
    }
}
