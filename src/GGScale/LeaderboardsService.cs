using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>Leaderboard orderings. Open set: unknown wire values pass through.</summary>
    public static class LeaderboardSortOrder
    {
        /// <summary>Lower scores rank first.</summary>
        public const string Asc = "asc";

        /// <summary>Higher scores rank first.</summary>
        public const string Desc = "desc";
    }

    /// <summary>How a submitted score combines with the stored one. Open set.</summary>
    public static class LeaderboardScoreOperator
    {
        /// <summary>Keep the better score per the sort order.</summary>
        public const string Best = "best";

        /// <summary>Always overwrite the stored score.</summary>
        public const string Set = "set";

        /// <summary>Add the submission to the stored score.</summary>
        public const string Incr = "incr";
    }

    /// <summary>Leaderboard reset cadences. Open set.</summary>
    public static class LeaderboardResetSchedule
    {
        /// <summary>Never resets.</summary>
        public const string None = "none";

        /// <summary>Resets daily.</summary>
        public const string Daily = "daily";

        /// <summary>Resets weekly.</summary>
        public const string Weekly = "weekly";

        /// <summary>Resets monthly.</summary>
        public const string Monthly = "monthly";
    }

    /// <summary>One leaderboard row. Rank is 0-based.</summary>
    public sealed class LeaderboardEntry
    {
        internal LeaderboardEntry(long playerId, long score, long rank, string displayName, JsonValue? metadata)
        {
            PlayerId = playerId;
            Score = score;
            Rank = rank;
            DisplayName = displayName;
            Metadata = metadata;
        }

        /// <summary>The scoring player.</summary>
        public long PlayerId { get; }

        /// <summary>The player's best score.</summary>
        public long Score { get; }

        /// <summary>0-based rank in the server's ordering.</summary>
        public long Rank { get; }

        /// <summary>The player's display name; empty when unset.</summary>
        public string DisplayName { get; }

        /// <summary>Opaque metadata stored with the score; null when absent.</summary>
        public JsonValue? Metadata { get; }

        internal static LeaderboardEntry FromJson(JsonValue v) =>
            new LeaderboardEntry(
                v.OptLong("player_id"),
                v.OptLong("score"),
                v.OptLong("rank"),
                v.OptString("display_name") ?? string.Empty,
                v.Opt("metadata"));
    }

    /// <summary>A leaderboard's configuration and period state.</summary>
    public sealed class LeaderboardInfo
    {
        internal LeaderboardInfo(long id, string name, string sortOrder, string scoreOperator, bool clientSubmissions, long? scoreMin, long? scoreMax, int? attemptCap, string resetSchedule, int currentPeriod, DateTimeOffset? periodStartedAt, DateTimeOffset? nextResetAt, JsonValue? metadata)
        {
            Id = id;
            Name = name;
            SortOrder = sortOrder;
            ScoreOperator = scoreOperator;
            ClientSubmissions = clientSubmissions;
            ScoreMin = scoreMin;
            ScoreMax = scoreMax;
            AttemptCap = attemptCap;
            ResetSchedule = resetSchedule;
            CurrentPeriod = currentPeriod;
            PeriodStartedAt = periodStartedAt;
            NextResetAt = nextResetAt;
            Metadata = metadata;
        }

        /// <summary>The board id used in every other leaderboard call.</summary>
        public long Id { get; }

        /// <summary>Project-unique board name.</summary>
        public string Name { get; }

        /// <summary>Ordering (see <see cref="LeaderboardSortOrder"/>; open set).</summary>
        public string SortOrder { get; }

        /// <summary>Score combine rule (see <see cref="LeaderboardScoreOperator"/>; open set).</summary>
        public string ScoreOperator { get; }

        /// <summary>True when publishable-key clients may submit scores directly.</summary>
        public bool ClientSubmissions { get; }

        /// <summary>Minimum accepted score; null when unbounded.</summary>
        public long? ScoreMin { get; }

        /// <summary>Maximum accepted score; null when unbounded.</summary>
        public long? ScoreMax { get; }

        /// <summary>Max submissions per player per period; null when uncapped.</summary>
        public int? AttemptCap { get; }

        /// <summary>Reset cadence (see <see cref="LeaderboardResetSchedule"/>; open set).</summary>
        public string ResetSchedule { get; }

        /// <summary>The current period number (0 for never-reset boards).</summary>
        public int CurrentPeriod { get; }

        /// <summary>When the current period started; null for never-reset boards.</summary>
        public DateTimeOffset? PeriodStartedAt { get; }

        /// <summary>When the next reset happens; null for never-reset boards.</summary>
        public DateTimeOffset? NextResetAt { get; }

        /// <summary>Project-defined board metadata; null when absent.</summary>
        public JsonValue? Metadata { get; }

        internal static LeaderboardInfo FromJson(JsonValue v) =>
            new LeaderboardInfo(
                v.OptLong("id"),
                v.OptString("name") ?? string.Empty,
                v.OptString("sort_order") ?? string.Empty,
                v.OptString("score_operator") ?? string.Empty,
                v.OptBool("client_submissions"),
                v.Opt("score_min") != null ? v.OptLong("score_min") : (long?)null,
                v.Opt("score_max") != null ? v.OptLong("score_max") : (long?)null,
                v.Opt("attempt_cap") != null ? (int)v.OptLong("attempt_cap") : (int?)null,
                v.OptString("reset_schedule") ?? string.Empty,
                (int)v.OptLong("current_period"),
                v.OptTime("period_started_at"),
                v.OptTime("next_reset_at"),
                v.Opt("metadata"));
    }

    /// <summary>One closed or current scoring period.</summary>
    public sealed class LeaderboardPeriodSummary
    {
        internal LeaderboardPeriodSummary(int period, DateTimeOffset startedAt, DateTimeOffset endedAt)
        {
            Period = period;
            StartedAt = startedAt;
            EndedAt = endedAt;
        }

        /// <summary>The period number.</summary>
        public int Period { get; }

        /// <summary>Period start.</summary>
        public DateTimeOffset StartedAt { get; }

        /// <summary>Period end (scheduled end for the current period).</summary>
        public DateTimeOffset EndedAt { get; }

        internal static LeaderboardPeriodSummary FromJson(JsonValue v) =>
            new LeaderboardPeriodSummary(
                (int)v.OptLong("period"),
                v.OptTime("started_at") ?? DateTimeOffset.MinValue,
                v.OptTime("ended_at") ?? DateTimeOffset.MinValue);
    }

    /// <summary>One page of a board's period history; NextCursor is empty on the last page.</summary>
    public sealed class LeaderboardPeriodsPage
    {
        internal LeaderboardPeriodsPage(int currentPeriod, string resetSchedule, DateTimeOffset? periodStartedAt, DateTimeOffset? nextResetAt, IReadOnlyList<LeaderboardPeriodSummary> periods, string nextCursor)
        {
            CurrentPeriod = currentPeriod;
            ResetSchedule = resetSchedule;
            PeriodStartedAt = periodStartedAt;
            NextResetAt = nextResetAt;
            Periods = periods;
            NextCursor = nextCursor;
        }

        /// <summary>The current period number.</summary>
        public int CurrentPeriod { get; }

        /// <summary>Reset cadence (see <see cref="LeaderboardResetSchedule"/>; open set).</summary>
        public string ResetSchedule { get; }

        /// <summary>When the current period started; null for never-reset boards.</summary>
        public DateTimeOffset? PeriodStartedAt { get; }

        /// <summary>When the next reset happens; null for never-reset boards.</summary>
        public DateTimeOffset? NextResetAt { get; }

        /// <summary>The page's periods, newest first.</summary>
        public IReadOnlyList<LeaderboardPeriodSummary> Periods { get; }

        /// <summary>Cursor for the next page; empty when done.</summary>
        public string NextCursor { get; }

        internal static LeaderboardPeriodsPage FromJson(JsonValue v)
        {
            var periods = new List<LeaderboardPeriodSummary>();
            var arr = v.Opt("periods");
            if (arr != null)
            {
                foreach (var p in arr.Items)
                {
                    periods.Add(LeaderboardPeriodSummary.FromJson(p));
                }
            }
            return new LeaderboardPeriodsPage(
                (int)v.OptLong("current_period"),
                v.OptString("reset_schedule") ?? string.Empty,
                v.OptTime("period_started_at"),
                v.OptTime("next_reset_at"),
                periods,
                v.OptString("next_cursor") ?? string.Empty);
        }
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
        /// (see <see cref="SubmitForAsync(string, long, long, CancellationToken)"/>).
        /// </summary>
        public Task SubmitAsync(long leaderboardId, long score, CancellationToken cancellationToken = default) =>
            SubmitAsync(leaderboardId, score, null, cancellationToken);

        /// <summary>
        /// Posts a score with opaque metadata (ghost data, loadout, …)
        /// stored alongside it and returned in entries. Same key-tier
        /// policy as the metadata-less overload.
        /// </summary>
        public Task SubmitAsync(long leaderboardId, long score, JsonValue? metadata, CancellationToken cancellationToken = default)
        {
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = ScoresPath(leaderboardId),
                Operation = "POST /v1/leaderboards/{id}/scores",
                Body = SubmitBody(score, metadata),
            }, cancellationToken);
        }

        /// <summary>
        /// Posts a score on behalf of the player identified by
        /// <paramref name="playerSessionToken"/>. The client's own (secret)
        /// API key authorizes the caller; the token identifies the subject.
        /// For dedicated servers processing match results for many players.
        /// </summary>
        public Task SubmitForAsync(string playerSessionToken, long leaderboardId, long score, CancellationToken cancellationToken = default) =>
            SubmitForAsync(playerSessionToken, leaderboardId, score, null, cancellationToken);

        /// <summary>
        /// Posts a score with metadata on behalf of the player identified
        /// by <paramref name="playerSessionToken"/>.
        /// </summary>
        public Task SubmitForAsync(string playerSessionToken, long leaderboardId, long score, JsonValue? metadata, CancellationToken cancellationToken = default)
        {
            return _client.Transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = ScoresPath(leaderboardId),
                Operation = "POST /v1/leaderboards/{id}/scores",
                Body = SubmitBody(score, metadata),
                ApiKey = _client.ApiKey,
                SessionToken = playerSessionToken,
            }, cancellationToken);
        }

        private static JsonValue SubmitBody(long score, JsonValue? metadata)
        {
            var body = JsonValue.NewObject().Set("score", JsonValue.Of(score));
            if (metadata != null)
            {
                body.Set("metadata", metadata);
            }
            return body;
        }

        /// <summary>Returns the top entries; limit 0 uses the server default (cap 100).</summary>
        public async Task<IReadOnlyList<LeaderboardEntry>> TopAsync(long leaderboardId, int limit = 0, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "GET",
                Path = "/v1/leaderboards/" + leaderboardId.ToString(CultureInfo.InvariantCulture) + "/top",
                Operation = "GET /v1/leaderboards/{id}/top",
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
                Operation = "GET /v1/leaderboards/{id}/around-me",
            };
            if (radius > 0)
            {
                req.AddQuery("radius", radius.ToString(CultureInfo.InvariantCulture));
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            var selfRank = resp.Opt("self_rank") != null ? resp.OptLong("self_rank") : -1;
            return new AroundMeResult(ParseEntries(resp), selfRank);
        }

        /// <summary>Lists the project's leaderboards with their configuration.</summary>
        public async Task<IReadOnlyList<LeaderboardInfo>> ListAsync(CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/leaderboards",
                Operation = "GET /v1/leaderboards",
            }, cancellationToken).ConfigureAwait(false);
            var boards = new List<LeaderboardInfo>();
            var arr = resp.Opt("leaderboards");
            if (arr != null)
            {
                foreach (var b in arr.Items)
                {
                    boards.Add(LeaderboardInfo.FromJson(b));
                }
            }
            return boards;
        }

        /// <summary>
        /// Returns the caller's and their accepted friends' entries,
        /// ranked 0-based within that group.
        /// </summary>
        public async Task<IReadOnlyList<LeaderboardEntry>> FriendsAsync(long leaderboardId, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = BoardPath(leaderboardId) + "/friends",
                Operation = "GET /v1/leaderboards/{id}/friends",
            }, cancellationToken).ConfigureAwait(false);
            return ParseEntries(resp);
        }

        /// <summary>
        /// Pages through a board's period history, newest first. Pass
        /// limit 0 for the server default (50, cap 200).
        /// </summary>
        public async Task<LeaderboardPeriodsPage> PeriodsAsync(long leaderboardId, int limit = 0, string? cursor = null, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "GET",
                Path = BoardPath(leaderboardId) + "/periods",
                Operation = "GET /v1/leaderboards/{id}/periods",
            };
            if (limit > 0)
            {
                req.AddQuery("limit", limit.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(cursor))
            {
                req.AddQuery("cursor", cursor!);
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            return LeaderboardPeriodsPage.FromJson(resp);
        }

        /// <summary>
        /// Returns the top entries of one period (current or closed).
        /// IsNotFound for a period the board has not reached.
        /// </summary>
        public async Task<IReadOnlyList<LeaderboardEntry>> PeriodTopAsync(long leaderboardId, int period, int limit = 0, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest
            {
                Method = "GET",
                Path = BoardPath(leaderboardId) + "/periods/" + period.ToString(CultureInfo.InvariantCulture) + "/top",
                Operation = "GET /v1/leaderboards/{id}/periods/{period}/top",
            };
            if (limit > 0)
            {
                req.AddQuery("limit", limit.ToString(CultureInfo.InvariantCulture));
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            return ParseEntries(resp);
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

        private static string BoardPath(long leaderboardId) =>
            "/v1/leaderboards/" + leaderboardId.ToString(CultureInfo.InvariantCulture);

        private static string ScoresPath(long leaderboardId) => BoardPath(leaderboardId) + "/scores";
    }
}
