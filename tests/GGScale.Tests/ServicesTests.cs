using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    internal static class TestClients
    {
        public static GGScaleClient WithSession(FakeTransport ft)
        {
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", Transport = ft });
            c.SetSession(Canned.Live());
            return c;
        }
    }

    public class StorageServiceTests
    {
        private static JsonValue CannedObject(long version = 1) =>
            JsonValue.NewObject()
                .Set("key", JsonValue.Of("settings"))
                .Set("value", JsonValue.NewObject().Set("theme", JsonValue.Of("dark")))
                .Set("version", JsonValue.Of(version))
                .Set("updated_at", JsonValue.Of("2026-07-06T10:00:00Z"));

        [Fact]
        public async Task Get_reads_object_by_escaped_key()
        {
            var ft = new FakeTransport { Respond = _ => CannedObject() };
            var c = TestClients.WithSession(ft);

            var obj = await c.Storage.GetAsync("settings");

            Assert.Equal("GET", ft.LastRequest!.Method);
            Assert.Equal("/v1/storage/objects/settings", ft.LastRequest.Path);
            Assert.Equal("dark", obj.Value.OptString("theme"));
            Assert.Equal(1L, obj.Version);
        }

        [Fact]
        public async Task Get_escapes_key_path_segment()
        {
            var ft = new FakeTransport { Respond = _ => CannedObject() };
            var c = TestClients.WithSession(ft);

            await c.Storage.GetAsync("a/b c");

            Assert.Equal("/v1/storage/objects/a%2Fb%20c", ft.LastRequest!.Path);
        }

        [Fact]
        public async Task Put_sends_body_and_if_match()
        {
            var ft = new FakeTransport { Respond = _ => CannedObject(version: 2) };
            var c = TestClients.WithSession(ft);

            var obj = await c.Storage.PutAsync("settings", JsonValue.NewObject().Set("theme", JsonValue.Of("light")), ifMatchVersion: 1);

            Assert.Equal("PUT", ft.LastRequest!.Method);
            Assert.Equal("1", ft.LastRequest.IfMatch);
            Assert.Equal("light", ft.LastRequest.Body!.OptString("theme"));
            Assert.Equal(2L, obj.Version);
        }

        [Fact]
        public async Task Put_conflict_maps_to_IsConflict()
        {
            var ft = new FakeTransport
            {
                Respond = _ => throw new GGScaleException(412, "version_conflict", "stale", conflictVersion: 5),
            };
            var c = TestClients.WithSession(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => c.Storage.PutAsync("k", JsonValue.NewObject(), ifMatchVersion: 1));

            Assert.True(ex.IsConflict);
            Assert.Equal(5L, ex.ConflictVersion);
        }

        [Fact]
        public async Task Delete_uses_delete_method()
        {
            var ft = new FakeTransport();
            var c = TestClients.WithSession(ft);

            await c.Storage.DeleteAsync("settings");

            Assert.Equal("DELETE", ft.LastRequest!.Method);
        }

        [Fact]
        public async Task List_passes_paging_options()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("items", JsonValue.NewArray().Add(CannedObject()))
                    .Set("next_cursor", JsonValue.Of("9")),
            };
            var c = TestClients.WithSession(ft);

            var page = await c.Storage.ListAsync(new StorageListOptions { KeyPrefix = "s", Limit = 10, Cursor = "5" });

            Assert.Equal("s", ft.LastRequest!.QueryValue("key_prefix"));
            Assert.Equal("10", ft.LastRequest.QueryValue("limit"));
            Assert.Equal("5", ft.LastRequest.QueryValue("cursor"));
            Assert.Single(page.Items);
            Assert.Equal("9", page.NextCursor);
        }
    }

    public class ProfileServiceTests
    {
        [Fact]
        public async Task Get_decodes_full_profile()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("id", JsonValue.Of(42L))
                    .Set("project_id", JsonValue.Of(7L))
                    .Set("external_id", JsonValue.Of("ext-abc"))
                    .Set("email", JsonValue.Of("demo@example.com"))
                    .Set("xuid", JsonValue.Of("xuid-123"))
                    .Set("email_verified_at", JsonValue.Of("2026-05-01T00:00:00Z"))
                    .Set("created_at", JsonValue.Of("2026-04-15T12:00:00Z")),
            };
            var c = TestClients.WithSession(ft);

            var p = await c.Profile.GetAsync();

            Assert.Equal("/v1/profile", ft.LastRequest!.Path);
            Assert.Equal(42L, p.Id);
            Assert.Equal("xuid-123", p.Xuid);
            Assert.NotNull(p.EmailVerifiedAt);
        }

        [Fact]
        public async Task Get_unverified_email_leaves_timestamp_null()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("id", JsonValue.Of(42L))
                    .Set("created_at", JsonValue.Of("2026-04-15T12:00:00Z")),
            };
            var c = TestClients.WithSession(ft);

            var p = await c.Profile.GetAsync();

            Assert.Null(p.EmailVerifiedAt);
        }

        [Fact]
        public async Task Update_sends_only_set_fields()
        {
            var ft = new FakeTransport();
            var c = TestClients.WithSession(ft);

            await c.Profile.UpdateAsync(new ProfilePatch { Xuid = "x-9" });

            Assert.Equal("PATCH", ft.LastRequest!.Method);
            Assert.Equal("x-9", ft.LastRequest.Body!.OptString("xuid"));
            Assert.Null(ft.LastRequest.Body!.Opt("email"));
        }
    }

    public class LeaderboardsServiceTests
    {
        [Fact]
        public async Task Submit_posts_score()
        {
            var ft = new FakeTransport();
            var c = TestClients.WithSession(ft);

            await c.Leaderboards.SubmitAsync(1, 1500);

            Assert.Equal("/v1/leaderboards/1/scores", ft.LastRequest!.Path);
            Assert.Equal(1500L, ft.LastRequest.Body!.OptLong("score"));
        }

        [Fact]
        public async Task SubmitFor_bypasses_client_session()
        {
            var ft = new FakeTransport();
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "secret-k", Transport = ft });

            await c.Leaderboards.SubmitForAsync("player-token", 1, 900);

            Assert.Equal("secret-k", ft.LastRequest!.ApiKey);
            Assert.Equal("player-token", ft.LastRequest.SessionToken);
            Assert.Equal(900L, ft.LastRequest.Body!.OptLong("score"));
        }

        [Fact]
        public async Task Top_parses_entries()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject().Set("entries", JsonValue.NewArray()
                    .Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(1L)).Set("score", JsonValue.Of(9000L)).Set("rank", JsonValue.Of(0L)))
                    .Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(2L)).Set("score", JsonValue.Of(8000L)).Set("rank", JsonValue.Of(1L)))),
            };
            var c = TestClients.WithSession(ft);

            var top = await c.Leaderboards.TopAsync(1, 5);

            Assert.Equal("/v1/leaderboards/1/top", ft.LastRequest!.Path);
            Assert.Equal("5", ft.LastRequest.QueryValue("limit"));
            Assert.Equal(2, top.Count);
            Assert.Equal(9000L, top[0].Score);
        }

        [Fact]
        public async Task AroundMe_parses_self_rank()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("entries", JsonValue.NewArray()
                        .Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(7L)).Set("score", JsonValue.Of(90L)).Set("rank", JsonValue.Of(5L))))
                    .Set("self_rank", JsonValue.Of(5L)),
            };
            var c = TestClients.WithSession(ft);

            var res = await c.Leaderboards.AroundMeAsync(1, 3);

            Assert.Equal("3", ft.LastRequest!.QueryValue("radius"));
            Assert.Equal(5L, res.SelfRank);
            Assert.Single(res.Entries);
        }

        [Fact]
        public async Task AroundMe_negative_self_rank_round_trips()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("entries", JsonValue.NewArray())
                    .Set("self_rank", JsonValue.Of(-1L)),
            };
            var c = TestClients.WithSession(ft);

            var res = await c.Leaderboards.AroundMeAsync(1);

            Assert.Equal(-1L, res.SelfRank);
        }
    }
}
