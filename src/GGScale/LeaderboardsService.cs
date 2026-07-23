using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>One leaderboard row. Rank is 0-based.</summary>
    public sealed class LeaderboardEntry
    {
        internal LeaderboardEntry(long playerId, long score, long rank)
        {
            PlayerId = playerId;
            Score = score;
            Rank = rank;
        }

        /// <summary>The scoring player.</summary>
        public long PlayerId { get; }

        /// <summary>The player's best score.</summary>
        public long Score { get; }

        /// <summary>0-based rank in the server's ordering.</summary>
        public long Rank { get; }

        internal static LeaderboardEntry FromJson(JsonValue v) =>
            new LeaderboardEntry(v.OptLong("player_id"), v.OptLong("score"), v.OptLong("rank"));
    }

    /// <summary>Around-me result; SelfRank is -1 when the caller has no score.</summary>
    public sealed class AroundMeResult
    {
        internal AroundMeResult(IReadOnlyList<LeaderboardEntry> entries, long selfRank)
        {
            Entries = entries;
            SelfRank = selfRank;
        }

        /// <summary>Entries surrounding the caller's rank.</summary>
        public IReadOnlyList<LeaderboardEntry> Entries { get; }

        /// <summary>The caller's 0-based rank, or -1 without a score.</summary>
        public long SelfRank { get; }
    }

    /// <summary>
    /// The /v1/leaderboards endpoints. Reach it via
    /// <see cref="GGScaleClient.Leaderboards"/>.
    /// </summary>
    public sealed class LeaderboardsService
    {
        private readonly GGScaleClient _client;

        internal LeaderboardsService(GGScaleClient client) => _client = client;

        /// <summary>
        /// Posts a score for the calling player. Server policy: submission
        /// requires a secret-tier API key — publishable-key clients get
        /// IsForbidden. Ship score writes through a trusted game server
        /// (see <see cref="SubmitForAsync"/>).
        /// </summary>
        public Task SubmitAsync(long leaderboardId, long score, CancellationToken cancellationToken = default)
        {
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = ScoresPath(leaderboardId),
                Body = JsonValue.NewObject().Set("score", JsonValue.Of(score)),
            }, cancellationToken);
        }

        /// <summary>
        /// Posts a score on behalf of the player identified by
        /// <paramref name="playerSessionToken"/>. The client's own (secret)
        /// API key authorizes the caller; the token identifies the subject.
        /// For dedicated servers processing match results for many players.
        /// </summary>
        public Task SubmitForAsync(string playerSessionToken, long leaderboardId, long score, CancellationToken cancellationToken = default)
        {
            return _client.Transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = ScoresPath(leaderboardId),
                Body = JsonValue.NewObject().Set("score", JsonValue.Of(score)),
                ApiKey = _client.ApiKey,
                SessionToken = playerSessionToken,
            }, cancellationToken);
        }

        /// <summary>Returns the top entries; limit 0 uses the server default (cap 100).</summary>
        public async Task<IReadOnlyList<LeaderboardEntry>> TopAsync(long leaderboardId, int limit = 0, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "GET",
                Path = "/v1/leaderboards/" + leaderboardId.ToString(CultureInfo.InvariantCulture) + "/top",
            };
            if (limit > 0)
            {
                req.AddQuery("limit", limit.ToString(CultureInfo.InvariantCulture));
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            return ParseEntries(resp);
        }

        /// <summary>Returns up to radius entries either side of the caller's rank (cap 50).</summary>
        public async Task<AroundMeResult> AroundMeAsync(long leaderboardId, int radius = 0, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "GET",
                Path = "/v1/leaderboards/" + leaderboardId.ToString(CultureInfo.InvariantCulture) + "/around-me",
            };
            if (radius > 0)
            {
                req.AddQuery("radius", radius.ToString(CultureInfo.InvariantCulture));
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            var selfRank = resp.Opt("self_rank") != null ? resp.OptLong("self_rank") : -1;
            return new AroundMeResult(ParseEntries(resp), selfRank);
        }

        private static List<LeaderboardEntry> ParseEntries(JsonValue resp)
        {
            var entries = new List<LeaderboardEntry>();
            var arr = resp.Opt("entries");
            if (arr != null)
            {
                foreach (var e in arr.Items)
                {
                    entries.Add(LeaderboardEntry.FromJson(e));
                }
            }
            return entries;
        }

        private static string ScoresPath(long leaderboardId) =>
            "/v1/leaderboards/" + leaderboardId.ToString(CultureInfo.InvariantCulture) + "/scores";
    }
}
