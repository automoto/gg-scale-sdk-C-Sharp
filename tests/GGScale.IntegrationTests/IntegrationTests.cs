using System;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.IntegrationTests
{
    /// <summary>
    /// Shared clients for the whole suite. The server's auth routes carry
    /// a per-IP rate limit (burst 10) and every test calls from
    /// 127.0.0.1, so the suite shares two anonymous players instead of
    /// minting one per test. xUnit runs tests in a collection
    /// sequentially, which also keeps us under the limiter.
    /// </summary>
    public sealed class ItFixture : IAsyncLifetime
    {
        public static string BaseUrl =>
            Environment.GetEnvironmentVariable("GGSCALE_IT_BASE_URL") ?? "http://127.0.0.1:18081";

        public static string PublishableKey =>
            Environment.GetEnvironmentVariable("GGSCALE_IT_PUBLISHABLE_KEY") ?? "ggp_integration_publishable_key";

        public static string SecretKey =>
            Environment.GetEnvironmentVariable("GGSCALE_IT_SECRET_KEY") ?? "ggs_integration_secret_key";

        public GGScaleClient Player1 { get; private set; } = null!;

        public GGScaleClient Player2 { get; private set; } = null!;

        public GGScaleClient ServerClient { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Player1 = await NewPlayerAsync();
            Player2 = await NewPlayerAsync();
            ServerClient = new GGScaleClient(new GGScaleClientOptions { BaseUrl = BaseUrl, ApiKey = SecretKey });
        }

        public Task DisposeAsync()
        {
            Player1.Dispose();
            Player2.Dispose();
            ServerClient.Dispose();
            return Task.CompletedTask;
        }

        private static async Task<GGScaleClient> NewPlayerAsync()
        {
            var c = new GGScaleClient(new GGScaleClientOptions { BaseUrl = BaseUrl, ApiKey = PublishableKey });
            await c.LoginAsync(new AnonymousAuth(c.Transport, PublishableKey));
            return c;
        }
    }

    [CollectionDefinition("integration")]
    public class IntegrationSuite : ICollectionFixture<ItFixture>
    {
    }

    [Collection("integration")]
    public class AuthIntegrationTests
    {
        private readonly ItFixture _fx;

        public AuthIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Anonymous_session_and_refresh_rotation()
        {
            var c = _fx.Player1;
            var sess = c.Session!;
            Assert.True(sess.PlayerId > 0, "player_id must round-trip from the real server");
            Assert.NotEmpty(sess.AccessToken);
            Assert.NotEmpty(sess.RefreshToken);
            Assert.True(sess.ExpiresAt > DateTimeOffset.UtcNow);

            var rotated = await c.Auth.RefreshAsync(sess.RefreshToken);
            Assert.Equal(sess.PlayerId, rotated.PlayerId);
            Assert.NotEqual(sess.RefreshToken, rotated.RefreshToken);

            // The old refresh token is revoked server-side; keep the shared
            // client on the rotated session for the rest of the suite.
            c.SetSession(rotated);
        }
    }

    [Collection("integration")]
    public class ProfileIntegrationTests
    {
        private readonly ItFixture _fx;

        public ProfileIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Profile_get_and_patch_xuid()
        {
            var c = _fx.Player1;
            var p = await c.Profile.GetAsync();
            Assert.Equal(c.Session!.PlayerId, p.Id);
            Assert.NotEmpty(p.ExternalId);
            Assert.Empty(p.Email);

            var xuid = "it-xuid-" + p.Id;
            await c.Profile.UpdateAsync(new ProfilePatch { Xuid = xuid });

            p = await c.Profile.GetAsync();
            Assert.Equal(xuid, p.Xuid);
        }
    }

    [Collection("integration")]
    public class StorageIntegrationTests
    {
        private readonly ItFixture _fx;

        public StorageIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Storage_crud_and_occ()
        {
            var c = _fx.Player1;
            var v1 = JsonValue.NewObject().Set("theme", JsonValue.Of("dark")).Set("volume", JsonValue.Of(80L));

            var obj = await c.Storage.PutAsync("settings", v1);
            var firstVersion = obj.Version;

            var got = await c.Storage.GetAsync("settings");
            Assert.Equal("dark", got.Value.OptString("theme"));

            obj = await c.Storage.PutAsync("settings", JsonValue.NewObject().Set("theme", JsonValue.Of("light")), firstVersion);
            Assert.True(obj.Version > firstVersion);

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => c.Storage.PutAsync("settings", JsonValue.NewObject(), firstVersion));
            Assert.True(ex.IsConflict);

            var page = await c.Storage.ListAsync();
            Assert.Single(page.Items);

            await c.Storage.DeleteAsync("settings");
            var nf = await Assert.ThrowsAsync<GGScaleException>(() => c.Storage.GetAsync("settings"));
            Assert.True(nf.IsNotFound);
        }
    }

    [Collection("integration")]
    public class LeaderboardsIntegrationTests
    {
        private const long SeededLeaderboardId = 1;

        private readonly ItFixture _fx;

        public LeaderboardsIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Submit_forbidden_for_publishable_then_submitfor_top_aroundme()
        {
            var player = _fx.Player1;
            var playerId = player.Session!.PlayerId;

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => player.Leaderboards.SubmitAsync(SeededLeaderboardId, 1500));
            Assert.True(ex.IsForbidden);

            await _fx.ServerClient.Leaderboards.SubmitForAsync(player.Session!.AccessToken, SeededLeaderboardId, 1500);

            var top = await player.Leaderboards.TopAsync(SeededLeaderboardId, 100);
            var found = false;
            foreach (var e in top)
            {
                if (e.PlayerId == playerId)
                {
                    found = true;
                    Assert.Equal(1500L, e.Score);
                }
            }
            Assert.True(found, "submitted score appears in top");

            var around = await player.Leaderboards.AroundMeAsync(SeededLeaderboardId, 5);
            Assert.True(around.SelfRank >= 0);
            Assert.NotEmpty(around.Entries);
        }
    }

    [Collection("integration")]
    public class PresenceIntegrationTests
    {
        private readonly ItFixture _fx;

        public PresenceIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Presence_set_and_validation()
        {
            await _fx.Player1.Presence.SetAsync("online");

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => _fx.Player1.Presence.SetAsync(""));
            Assert.True(ex.IsBadRequest);
        }
    }

    [Collection("integration")]
    public class GameSessionIntegrationTests
    {
        private readonly ItFixture _fx;

        public GameSessionIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Two_player_lifecycle()
        {
            var host = _fx.Player1;
            var joiner = _fx.Player2;

            var sess = await host.GameSessions.CreateAsync(new GameSessionCreate
            {
                TitleId = "integration",
                PublicAddr = new GameSessionAddr("203.0.113.1", 7777),
                MaxPlayers = 4,
                Props = JsonValue.NewObject().Set("map", JsonValue.Of("it_lobby")),
            });
            Assert.NotEmpty(sess.SessionId);
            Assert.NotEmpty(sess.JoinCode);
            Assert.Equal("open", sess.State);
            Assert.Single(sess.Peers);

            var resolvedId = await joiner.GameSessions.ResolveAsync(sess.JoinCode);
            Assert.Equal(sess.SessionId, resolvedId);

            var joined = await joiner.GameSessions.JoinAsync(resolvedId, new GameSessionAddr("198.51.100.7", 7778));
            Assert.Equal(2, joined.Peers.Count);

            var peers = await host.GameSessions.HeartbeatAsync(sess.SessionId, JsonValue.NewObject().Set("rtt_ms", JsonValue.Of(20L)));
            Assert.Equal(2, peers.Count);

            await joiner.GameSessions.LeaveAsync(sess.SessionId);
            await host.GameSessions.LeaveAsync(sess.SessionId);

            var ended = await host.GameSessions.GetAsync(sess.SessionId);
            Assert.Equal("ended", ended.State);
        }
    }

    [Collection("integration")]
    public class InvitesIntegrationTests
    {
        private readonly ItFixture _fx;

        public InvitesIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task List_is_empty_for_fresh_player()
        {
            var invites = await _fx.Player2.Invites.ListAsync();
            Assert.Empty(invites);
        }
    }

    [Collection("integration")]
    public class LinkedAccountGatesIntegrationTests
    {
        private readonly ItFixture _fx;

        public LinkedAccountGatesIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Friends_and_account_require_linked_account()
        {
            var ex = await Assert.ThrowsAsync<GGScaleException>(() => _fx.Player1.Friends.ListAsync());
            Assert.True(ex.IsForbidden);

            ex = await Assert.ThrowsAsync<GGScaleException>(() => _fx.Player1.Account.RemoteAddrsAsync());
            Assert.True(ex.IsForbidden);
        }
    }

    [Collection("integration")]
    public class ServerVerifyIntegrationTests
    {
        private readonly ItFixture _fx;

        public ServerVerifyIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Verify_valid_garbage_and_wrong_key_tier()
        {
            var player = _fx.Player1;

            var res = await _fx.ServerClient.Server.VerifySessionAsync(player.Session!.AccessToken);
            Assert.Equal(player.Session!.PlayerId, res.PlayerId);
            Assert.NotEmpty(res.ExternalId);
            Assert.Empty(res.Email);

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => _fx.ServerClient.Server.VerifySessionAsync("not.a.real.token"));
            Assert.True(ex.IsUnauthorized);

            // Publishable keys are kept off the verify oracle entirely.
            ex = await Assert.ThrowsAsync<GGScaleException>(
                () => player.Server.VerifySessionAsync(player.Session!.AccessToken));
            Assert.True(ex.IsForbidden);
        }
    }

    [Collection("integration")]
    public class RealtimeIntegrationTests
    {
        private readonly ItFixture _fx;

        public RealtimeIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Dial_and_close()
        {
            var rc = await _fx.Player1.DialRealtimeAsync();
            await rc.DisposeAsync();
        }
    }
}
