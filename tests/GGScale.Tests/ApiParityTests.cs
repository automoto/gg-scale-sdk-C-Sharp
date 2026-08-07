using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    /// <summary>Unit tests for the surface added for server v0.9.3 parity.</summary>
    public class ApiParityTests
    {
        private static GGScaleClient NewClientWithSession(FakeTransport ft)
        {
            var client = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft });
            client.SetSession(new Session("tok", "ref", 1, DateTimeOffset.UtcNow.AddHours(1)));
            return client;
        }

        // ---- Profile ----

        [Fact]
        public async Task Profile_get_decodes_display_name_and_friend_code()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"id\":1,\"display_name\":\"Nova\",\"friend_code\":\"XKCD4242\"}"),
            };
            using var client = NewClientWithSession(ft);

            var profile = await client.Profile.GetAsync();

            Assert.Equal("Nova", profile.DisplayName);
            Assert.Equal("XKCD4242", profile.FriendCode);
        }

        [Fact]
        public async Task Profile_update_sends_display_name_when_set()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Profile.UpdateAsync(new ProfilePatch { DisplayName = "Nova" });

            Assert.Equal("{\"display_name\":\"Nova\"}", ft.LastRequest!.Body!.ToString());
        }

        [Fact]
        public async Task Profile_regenerate_friend_code_posts_and_returns_code()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"friend_code\":\"NEW42\"}") };
            using var client = NewClientWithSession(ft);

            var code = await client.Profile.RegenerateFriendCodeAsync();

            Assert.Equal("NEW42", code);
            Assert.Equal("POST", ft.LastRequest!.Method);
            Assert.Equal("/v1/profile/friend-code", ft.LastRequest.Path);
        }

        // ---- Game sessions ----

        [Fact]
        public async Task GameSession_get_parses_expires_at_and_peer_display_name()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"session_id\":\"gs_1\",\"join_code\":\"J\",\"state\":\"open\",\"expires_at\":\"2026-08-07T10:00:00Z\"," +
                    "\"peers\":[{\"player_id\":7,\"display_name\":\"Nova\",\"addr\":{\"ip\":\"1.2.3.4\",\"port\":7777}}]}"),
            };
            using var client = NewClientWithSession(ft);

            var session = await client.GameSessions.GetAsync("gs_1");

            Assert.Equal(new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero), session.ExpiresAt);
            Assert.Equal("Nova", session.Peers[0].DisplayName);
        }

        [Fact]
        public async Task GameSessions_list_passes_title_and_paging_options()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.GameSessions.ListAsync(new GameSessionListOptions { TitleId = "my-game", Limit = 25, Cursor = "gs_9" });

            Assert.Equal("/v1/game-sessions", ft.LastRequest!.Path);
            Assert.Equal("my-game", ft.LastRequest.QueryValue("title_id"));
            Assert.Equal("25", ft.LastRequest.QueryValue("limit"));
            Assert.Equal("gs_9", ft.LastRequest.QueryValue("cursor"));
        }

        [Fact]
        public async Task GameSessions_list_parses_public_entries_and_cursor()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"items\":[{\"session_id\":\"gs_1\",\"title_id\":\"t\",\"props\":{\"map\":\"arena\"}," +
                    "\"player_count\":3,\"max_players\":8,\"host_player_id\":87,\"host_display_name\":\"Nova\"," +
                    "\"created_at\":\"2026-08-07T09:00:00Z\"}],\"next_cursor\":\"gs_1\"}"),
            };
            using var client = NewClientWithSession(ft);

            var page = await client.GameSessions.ListAsync();

            var entry = Assert.Single(page.Items);
            Assert.Equal("gs_1", entry.SessionId);
            Assert.Equal(3, entry.PlayerCount);
            Assert.Equal(8, entry.MaxPlayers);
            Assert.Equal(87L, entry.HostPlayerId);
            Assert.Equal("Nova", entry.HostDisplayName);
            Assert.Equal("arena", entry.Props.OptString("map"));
            Assert.Equal("gs_1", page.NextCursor);
        }

        [Fact]
        public async Task GameSessions_list_tolerates_null_items_array()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"items\":null,\"next_cursor\":\"\"}") };
            using var client = NewClientWithSession(ft);

            var page = await client.GameSessions.ListAsync();

            Assert.Empty(page.Items);
        }

        [Fact]
        public async Task SendSignal_posts_all_fields_and_returns_id()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"id\":512}") };
            using var client = NewClientWithSession(ft);

            var id = await client.GameSessions.SendSignalAsync("gs 1", 87, "neg-1", GameSessionSignalKind.Offer, "b64");

            Assert.Equal(512L, id);
            Assert.Equal("/v1/game-session/gs%201/signals", ft.LastRequest!.Path);
            Assert.Equal(
                "{\"to_player_id\":87,\"negotiation_id\":\"neg-1\",\"kind\":\"offer\",\"payload\":\"b64\"}",
                ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task PollSignals_passes_after_id_and_parses_entries()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"signals\":[{\"id\":513,\"from_player_id\":42,\"to_player_id\":87," +
                    "\"negotiation_id\":\"neg-1\",\"kind\":\"answer\",\"payload\":\"b64\",\"created_at\":\"2026-08-07T09:00:00Z\"}]}"),
            };
            using var client = NewClientWithSession(ft);

            var signals = await client.GameSessions.PollSignalsAsync("gs_1", 512);

            Assert.Equal("512", ft.LastRequest!.QueryValue("after_id"));
            var s = Assert.Single(signals);
            Assert.Equal(513L, s.Id);
            Assert.Equal(42L, s.FromPlayerId);
            Assert.Equal("answer", s.Kind);
        }

        [Fact]
        public async Task PollSignals_omits_zero_after_id_and_tolerates_null_array()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"signals\":null}") };
            using var client = NewClientWithSession(ft);

            var signals = await client.GameSessions.PollSignalsAsync("gs_1");

            Assert.Null(ft.LastRequest!.QueryValue("after_id"));
            Assert.Empty(signals);
        }

        // ---- Leaderboards ----

        [Fact]
        public async Task Leaderboards_top_parses_display_name_and_metadata()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"entries\":[{\"player_id\":42,\"score\":1500,\"rank\":0,\"display_name\":\"Nova\",\"metadata\":{\"ghost\":\"r-42\"}}]}"),
            };
            using var client = NewClientWithSession(ft);

            var entries = await client.Leaderboards.TopAsync(1);

            var e = Assert.Single(entries);
            Assert.Equal("Nova", e.DisplayName);
            Assert.Equal("r-42", e.Metadata!.OptString("ghost"));
        }

        [Fact]
        public async Task Leaderboards_top_tolerates_null_entries_array()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"entries\":null}") };
            using var client = NewClientWithSession(ft);

            var entries = await client.Leaderboards.TopAsync(1);

            Assert.Empty(entries);
        }

        [Fact]
        public async Task Leaderboards_submit_posts_metadata_object()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Leaderboards.SubmitAsync(1, 1500, JsonValue.Parse("{\"ghost\":\"r-42\"}"));

            Assert.Equal("{\"score\":1500,\"metadata\":{\"ghost\":\"r-42\"}}", ft.LastRequest!.Body!.ToString());
        }

        [Fact]
        public async Task Leaderboards_submit_omits_metadata_when_null()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Leaderboards.SubmitAsync(1, 1500);

            Assert.Equal("{\"score\":1500}", ft.LastRequest!.Body!.ToString());
        }

        [Fact]
        public async Task Leaderboards_submit_for_posts_metadata_with_player_token()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Leaderboards.SubmitForAsync("ptok", 1, 9, JsonValue.Parse("{\"m\":1}"));

            Assert.Equal("ptok", ft.LastRequest!.SessionToken);
            Assert.Equal("{\"score\":9,\"metadata\":{\"m\":1}}", ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task Leaderboards_list_parses_infos_with_optional_fields()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"leaderboards\":[" +
                    "{\"id\":1,\"name\":\"weekly\",\"sort_order\":\"desc\",\"score_operator\":\"best\"," +
                    "\"client_submissions\":false,\"score_min\":0,\"score_max\":1000000,\"attempt_cap\":3," +
                    "\"reset_schedule\":\"weekly\",\"current_period\":2,\"period_started_at\":\"2026-08-03T00:00:00Z\"," +
                    "\"next_reset_at\":\"2026-08-10T00:00:00Z\",\"metadata\":{\"icon\":\"gold\"}}," +
                    "{\"id\":2,\"name\":\"alltime\",\"sort_order\":\"asc\",\"score_operator\":\"set\"," +
                    "\"client_submissions\":true,\"reset_schedule\":\"none\",\"current_period\":0}]}"),
            };
            using var client = NewClientWithSession(ft);

            var boards = await client.Leaderboards.ListAsync();

            Assert.Equal(2, boards.Count);
            Assert.Equal(0L, boards[0].ScoreMin);
            Assert.Equal(1000000L, boards[0].ScoreMax);
            Assert.Equal(3, boards[0].AttemptCap);
            Assert.Equal("gold", boards[0].Metadata!.OptString("icon"));
            Assert.Null(boards[1].ScoreMin);
            Assert.Null(boards[1].AttemptCap);
            Assert.Null(boards[1].PeriodStartedAt);
            Assert.Null(boards[1].Metadata);
            Assert.Equal(LeaderboardResetSchedule.None, boards[1].ResetSchedule);
        }

        [Fact]
        public async Task Leaderboards_friends_parses_entries()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"entries\":[{\"player_id\":1,\"score\":5,\"rank\":0}]}"),
            };
            using var client = NewClientWithSession(ft);

            var entries = await client.Leaderboards.FriendsAsync(3);

            Assert.Equal("/v1/leaderboards/3/friends", ft.LastRequest!.Path);
            Assert.Single(entries);
        }

        [Fact]
        public async Task Leaderboards_periods_passes_paging_and_parses_page()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"current_period\":13,\"reset_schedule\":\"weekly\"," +
                    "\"periods\":[{\"period\":12,\"started_at\":\"2026-07-27T00:00:00Z\",\"ended_at\":\"2026-08-03T00:00:00Z\"}]," +
                    "\"next_cursor\":\"11\"}"),
            };
            using var client = NewClientWithSession(ft);

            var page = await client.Leaderboards.PeriodsAsync(1, limit: 5, cursor: "12");

            Assert.Equal("5", ft.LastRequest!.QueryValue("limit"));
            Assert.Equal("12", ft.LastRequest.QueryValue("cursor"));
            Assert.Equal(13, page.CurrentPeriod);
            Assert.Equal("11", page.NextCursor);
            Assert.Equal(12, Assert.Single(page.Periods).Period);
        }

        [Fact]
        public async Task Leaderboards_period_top_builds_nested_path()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"entries\":[]}") };
            using var client = NewClientWithSession(ft);

            await client.Leaderboards.PeriodTopAsync(5, 12, limit: 10);

            Assert.Equal("/v1/leaderboards/5/periods/12/top", ft.LastRequest!.Path);
            Assert.Equal("10", ft.LastRequest.QueryValue("limit"));
        }

        // ---- Relay ----

        [Fact]
        public async Task Relay_credentials_parse_stun_urls()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"username\":\"u\",\"password\":\"p\",\"ttl\":600,\"realm\":\"gg\"," +
                    "\"urls\":[\"turn:r:3478\"],\"stun_urls\":[\"stun:s:3478\"]}"),
            };
            using var client = NewClientWithSession(ft);

            var creds = await client.Relay.GetCredentialsAsync();

            Assert.Equal("stun:s:3478", Assert.Single(creds.StunUrls));
        }

        [Fact]
        public async Task Relay_credentials_tolerate_missing_stun_urls()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"username\":\"u\",\"password\":\"p\",\"ttl\":600,\"realm\":\"gg\"}"),
            };
            using var client = NewClientWithSession(ft);

            var creds = await client.Relay.GetCredentialsAsync();

            Assert.Empty(creds.StunUrls);
        }

        // ---- Auth ----

        [Fact]
        public async Task SteamAuth_exchanges_ticket_for_session()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"access_token\":\"a\",\"refresh_token\":\"r\",\"player_id\":9,\"expires_at\":\"2026-08-07T10:00:00Z\"}"),
            };

            var session = await new SteamAuth(ft, "pk", "14000000abcd").AuthenticateAsync(CancellationToken.None);

            Assert.Equal("/v1/auth/steam", ft.LastRequest!.Path);
            Assert.Equal("pk", ft.LastRequest.ApiKey);
            Assert.Equal("{\"ticket\":\"14000000abcd\"}", ft.LastRequest.Body!.ToString());
            Assert.Equal(9L, session.PlayerId);
        }

        [Fact]
        public async Task LinkEmail_posts_credentials_with_session_token()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Auth.LinkEmailAsync("p@example.com", "pw");

            Assert.Equal("/v1/auth/link", ft.LastRequest!.Path);
            Assert.Equal("tok", ft.LastRequest.SessionToken);
            Assert.Equal("{\"email\":\"p@example.com\",\"password\":\"pw\"}", ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task LinkSteam_posts_ticket_with_session_token()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Auth.LinkSteamAsync("14000000abcd");

            Assert.Equal("/v1/auth/link/steam", ft.LastRequest!.Path);
            Assert.Equal("tok", ft.LastRequest.SessionToken);
            Assert.Equal("{\"ticket\":\"14000000abcd\"}", ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task ChangePassword_posts_current_and_new()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Auth.ChangePasswordAsync("old", "new");

            Assert.Equal("/v1/auth/password", ft.LastRequest!.Path);
            Assert.Equal("{\"current_password\":\"old\",\"new_password\":\"new\"}", ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task Disable_posts_password_with_session_token()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Auth.DisableAsync("pw");

            Assert.Equal("/v1/auth/disable", ft.LastRequest!.Path);
            Assert.Equal("tok", ft.LastRequest.SessionToken);
            Assert.Equal("{\"password\":\"pw\"}", ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task Disable_omits_password_for_passwordless_players()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Auth.DisableAsync();

            Assert.Equal("/v1/auth/disable", ft.LastRequest!.Path);
            Assert.Equal("{}", ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task RequestPasswordReset_sends_api_key_without_session()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Auth.RequestPasswordResetAsync("p@example.com");

            Assert.Equal("/v1/auth/password-reset", ft.LastRequest!.Path);
            Assert.Equal("pk", ft.LastRequest.ApiKey);
            Assert.Null(ft.LastRequest.SessionToken);
        }

        [Fact]
        public async Task ConfirmPasswordReset_posts_email_code_new_password()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Auth.ConfirmPasswordResetAsync("p@example.com", "483920", "pw2");

            Assert.Equal("/v1/auth/password-reset/confirm", ft.LastRequest!.Path);
            Assert.Equal(
                "{\"email\":\"p@example.com\",\"code\":\"483920\",\"new_password\":\"pw2\"}",
                ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task ResendVerification_posts_email()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Auth.ResendVerificationAsync("p@example.com");

            Assert.Equal("/v1/auth/verify/resend", ft.LastRequest!.Path);
            Assert.Equal("{\"email\":\"p@example.com\"}", ft.LastRequest.Body!.ToString());
        }

        // ---- Players ----

        [Fact]
        public async Task Players_get_reads_player_by_id()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"id\":42,\"display_name\":\"Nova\",\"created_at\":\"2026-01-02T15:04:05Z\"}"),
            };
            using var client = NewClientWithSession(ft);

            var player = await client.Players.GetAsync(42);

            Assert.Equal("/v1/players/42", ft.LastRequest!.Path);
            Assert.Equal(42L, player.Id);
            Assert.Equal("Nova", player.DisplayName);
        }

        [Fact]
        public async Task Players_resolve_joins_ids_with_commas_and_parses()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"players\":[{\"id\":42},{\"id\":87}]}"),
            };
            using var client = NewClientWithSession(ft);

            var players = await client.Players.ResolveAsync(new List<long> { 42, 87, 101 });

            Assert.Equal("42,87,101", ft.LastRequest!.QueryValue("ids"));
            Assert.Equal(2, players.Count);
        }

        [Fact]
        public async Task Players_resolve_tolerates_null_players_array()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"players\":null}") };
            using var client = NewClientWithSession(ft);

            var players = await client.Players.ResolveAsync(new List<long> { 1 });

            Assert.Empty(players);
        }

        [Fact]
        public async Task Players_resolve_throws_on_empty_ids()
        {
            using var client = NewClientWithSession(new FakeTransport());

            await Assert.ThrowsAsync<ArgumentException>(() => client.Players.ResolveAsync(new List<long>()));
        }

        [Fact]
        public async Task Players_resolve_friend_code_escapes_code_path_segment()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"id\":42}") };
            using var client = NewClientWithSession(ft);

            await client.Players.ResolveFriendCodeAsync("X K/42");

            Assert.Equal("/v1/players/by-code/X%20K%2F42", ft.LastRequest!.Path);
        }

        // ---- Server tier ----

        [Fact]
        public async Task Server_submit_score_posts_player_id_score_metadata()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Server.SubmitScoreAsync(1, 42, 1500, JsonValue.Parse("{\"ghost\":\"r-42\"}"));

            Assert.Equal("/v1/server/leaderboards/1/scores", ft.LastRequest!.Path);
            Assert.Equal("pk", ft.LastRequest.ApiKey);
            Assert.Equal(
                "{\"player_id\":42,\"score\":1500,\"metadata\":{\"ghost\":\"r-42\"}}",
                ft.LastRequest.Body!.ToString());
        }

        [Fact]
        public async Task Server_submit_score_omits_metadata_when_null()
        {
            var ft = new FakeTransport();
            using var client = NewClientWithSession(ft);

            await client.Server.SubmitScoreAsync(1, 42, 1500);

            Assert.Equal("{\"player_id\":42,\"score\":1500}", ft.LastRequest!.Body!.ToString());
        }

        [Fact]
        public async Task Server_get_player_storage_builds_server_path()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"key\":\"save slot\",\"value\":{},\"version\":1,\"updated_at\":\"2026-08-07T09:00:00Z\"}"),
            };
            using var client = NewClientWithSession(ft);

            var obj = await client.Server.GetPlayerStorageAsync(42, "save slot");

            Assert.Equal("/v1/server/players/42/storage/objects/save%20slot", ft.LastRequest!.Path);
            Assert.Equal("save slot", obj.Key);
        }

        [Fact]
        public async Task Server_put_player_storage_sends_body_and_if_match()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"key\":\"k\",\"value\":{\"a\":1},\"version\":8,\"updated_at\":\"2026-08-07T09:00:00Z\"}"),
            };
            using var client = NewClientWithSession(ft);

            var obj = await client.Server.PutPlayerStorageAsync(42, "k", JsonValue.Parse("{\"a\":1}"), ifMatchVersion: 7);

            Assert.Equal("PUT", ft.LastRequest!.Method);
            Assert.Equal("7", ft.LastRequest.IfMatch);
            Assert.Equal("{\"a\":1}", ft.LastRequest.Body!.ToString());
            Assert.Equal(8L, obj.Version);
        }

        [Fact]
        public async Task Server_list_player_storage_passes_paging_options()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"items\":[{\"key\":\"a\"}],\"next_cursor\":\"104\"}"),
            };
            using var client = NewClientWithSession(ft);

            var page = await client.Server.ListPlayerStorageAsync(42, new StorageListOptions { KeyPrefix = "save-", Limit = 50, Cursor = "10" });

            Assert.Equal("/v1/server/players/42/storage/objects", ft.LastRequest!.Path);
            Assert.Equal("save-", ft.LastRequest.QueryValue("key_prefix"));
            Assert.Equal("50", ft.LastRequest.QueryValue("limit"));
            Assert.Equal("10", ft.LastRequest.QueryValue("cursor"));
            Assert.Equal("104", page.NextCursor);
        }
    }
}
