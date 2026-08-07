using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>Another player's public directory entry.</summary>
    public sealed class PublicPlayer
    {
        internal PublicPlayer(long id, string displayName, DateTimeOffset createdAt)
        {
            Id = id;
            DisplayName = displayName;
            CreatedAt = createdAt;
        }

        /// <summary>The player id.</summary>
        public long Id { get; }

        /// <summary>The player's display name; empty when unset.</summary>
        public string DisplayName { get; }

        /// <summary>Player creation time.</summary>
        public DateTimeOffset CreatedAt { get; }

        internal static PublicPlayer FromJson(JsonValue v) =>
            new PublicPlayer(
                v.OptLong("id"),
                v.OptString("display_name") ?? string.Empty,
                v.OptTime("created_at") ?? DateTimeOffset.MinValue);
    }

    /// <summary>
    /// The /v1/players directory: public profiles of other players in the
    /// project, by id or friend code. Requires a player session. Reach it
    /// via <see cref="GGScaleClient.Players"/>.
    /// </summary>
    public sealed class PlayersService
    {
        private readonly GGScaleClient _client;

        internal PlayersService(GGScaleClient client) => _client = client;

        /// <summary>Returns one player's public entry; IsNotFound when unknown.</summary>
        public async Task<PublicPlayer> GetAsync(long playerId, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/players/" + playerId.ToString(CultureInfo.InvariantCulture),
                Operation = "GET /v1/players/{id}",
            }, cancellationToken).ConfigureAwait(false);
            return PublicPlayer.FromJson(resp);
        }

        /// <summary>
        /// Resolves up to 100 player ids in one call. Unknown ids are
        /// omitted from the result — check for missing entries instead of
        /// expecting an error.
        /// </summary>
        public async Task<IReadOnlyList<PublicPlayer>> ResolveAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        {
            if (ids == null)
            {
                throw new ArgumentNullException(nameof(ids));
            }
            if (ids.Count == 0)
            {
                throw new ArgumentException("at least one player id is required", nameof(ids));
            }
            var joined = new StringBuilder();
            for (var i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    joined.Append(',');
                }
                joined.Append(ids[i].ToString(CultureInfo.InvariantCulture));
            }
            var req = new GGRequest
            {
                Method = "GET",
                Path = "/v1/players",
                Operation = "GET /v1/players",
            };
            req.AddQuery("ids", joined.ToString());
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            var players = new List<PublicPlayer>();
            var arr = resp.Opt("players");
            if (arr != null)
            {
                foreach (var p in arr.Items)
                {
                    players.Add(PublicPlayer.FromJson(p));
                }
            }
            return players;
        }

        /// <summary>
        /// Resolves a friend code (see
        /// <see cref="ProfileService.RegenerateFriendCodeAsync"/>) to its
        /// player. IsNotFound for unknown or rotated codes.
        /// </summary>
        public async Task<PublicPlayer> ResolveFriendCodeAsync(string code, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException("code is required", nameof(code));
            }
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/players/by-code/" + Uri.EscapeDataString(code),
                Operation = "GET /v1/players/by-code/{code}",
            }, cancellationToken).ConfigureAwait(false);
            return PublicPlayer.FromJson(resp);
        }
    }
}
