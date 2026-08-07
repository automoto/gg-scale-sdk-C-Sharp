using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.IntegrationTests
{
    [Collection("integration")]
    public class ConfigIntegrationTests
    {
        [Fact]
        public async Task Config_get_and_etag_304_roundtrip()
        {
            using var c = new GGScaleClient(new GGScaleClientOptions
            {
                BaseUrl = ItFixture.BaseUrl,
                ApiKey = ItFixture.PublishableKey,
            });

            var first = await c.Config.GetAsync();
            Assert.False(first.NotModified);
            Assert.NotEmpty(first.ETag);

            var second = await c.Config.GetAsync(first.ETag);
            Assert.True(second.NotModified);
            Assert.Equal(JsonKind.Null, second.Value.Kind);
        }
    }

    [Collection("integration")]
    public class FriendCodeIntegrationTests
    {
        private readonly ItFixture _fx;

        public FriendCodeIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Friend_code_regenerates_and_resolves()
        {
            var code = await _fx.Player1.Profile.RegenerateFriendCodeAsync();
            Assert.NotEmpty(code);

            var profile = await _fx.Player1.Profile.GetAsync();
            Assert.Equal(code, profile.FriendCode);

            var resolved = await _fx.Player2.Players.ResolveFriendCodeAsync(code);
            Assert.Equal(_fx.Player1.Session!.PlayerId, resolved.Id);

            var rotated = await _fx.Player1.Profile.RegenerateFriendCodeAsync();
            Assert.NotEqual(code, rotated);
            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => _fx.Player2.Players.ResolveFriendCodeAsync(code));
            Assert.True(ex.IsNotFound);
        }
    }

    [Collection("integration")]
    public class PlayersIntegrationTests
    {
        private readonly ItFixture _fx;

        public PlayersIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Players_resolve_batch_and_get()
        {
            var id1 = _fx.Player1.Session!.PlayerId;
            var id2 = _fx.Player2.Session!.PlayerId;

            var resolved = await _fx.Player1.Players.ResolveAsync(new List<long> { id1, id2, 99999999 });
            Assert.Equal(2, resolved.Count);
            Assert.Contains(resolved, p => p.Id == id1);
            Assert.Contains(resolved, p => p.Id == id2);

            var one = await _fx.Player1.Players.GetAsync(id2);
            Assert.Equal(id2, one.Id);
        }
    }

    [Collection("integration")]
    public class LeaderboardDiscoveryIntegrationTests
    {
        private readonly ItFixture _fx;

        public LeaderboardDiscoveryIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Leaderboards_list_periods_and_server_submit()
        {
            var boards = await _fx.Player1.Leaderboards.ListAsync();
            var board = Assert.Single(boards, b => b.Id == 1);
            Assert.NotEmpty(board.SortOrder);

            var page = await _fx.Player1.Leaderboards.PeriodsAsync(1);
            Assert.True(page.CurrentPeriod >= 0);

            var playerId = _fx.Player1.Session!.PlayerId;
            await _fx.ServerClient.Server.SubmitScoreAsync(1, playerId, 4242,
                JsonValue.NewObject().Set("ghost", JsonValue.Of("it-run")));

            var top = await _fx.Player1.Leaderboards.TopAsync(1, 50);
            var entry = top.First(e => e.PlayerId == playerId);
            Assert.NotNull(entry.Metadata);
            Assert.Equal("it-run", entry.Metadata!.OptString("ghost"));
        }
    }

    [Collection("integration")]
    public class SessionBrowserIntegrationTests
    {
        private readonly ItFixture _fx;

        public SessionBrowserIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Browser_lists_public_session_and_signals_roundtrip()
        {
            var host = _fx.Player1;
            var guest = _fx.Player2;
            var session = await host.GameSessions.CreateAsync(new GameSessionCreate
            {
                TitleId = "it-browser",
                PublicAddr = new GameSessionAddr("203.0.113.10", 7777),
                MaxPlayers = 4,
            });
            try
            {
                var page = await guest.GameSessions.ListAsync(new GameSessionListOptions { TitleId = "it-browser" });
                var entry = Assert.Single(page.Items, e => e.SessionId == session.SessionId);
                Assert.Equal(host.Session!.PlayerId, entry.HostPlayerId);

                await guest.GameSessions.JoinAsync(session.SessionId, new GameSessionAddr("203.0.113.11", 7778));
                await host.GameSessions.HeartbeatAsync(session.SessionId);
                await guest.GameSessions.HeartbeatAsync(session.SessionId);

                var signalId = await host.GameSessions.SendSignalAsync(
                    session.SessionId, guest.Session!.PlayerId, "neg-it-1", GameSessionSignalKind.Offer, "b64-sdp-offer");
                Assert.True(signalId > 0);

                var signals = await guest.GameSessions.PollSignalsAsync(session.SessionId);
                var got = Assert.Single(signals, s => s.Id == signalId);
                Assert.Equal("neg-it-1", got.NegotiationId);
                Assert.Equal("b64-sdp-offer", got.Payload);

                var newer = await guest.GameSessions.PollSignalsAsync(session.SessionId, afterId: signalId);
                Assert.DoesNotContain(newer, s => s.Id == signalId);

                await guest.GameSessions.LeaveAsync(session.SessionId);
            }
            finally
            {
                await host.GameSessions.LeaveAsync(session.SessionId);
            }
        }
    }

    [Collection("integration")]
    public class ServerStorageIntegrationTests
    {
        private readonly ItFixture _fx;

        public ServerStorageIntegrationTests(ItFixture fx) => _fx = fx;

        [Fact]
        public async Task Server_tier_player_storage_crud_visible_to_player()
        {
            var playerId = _fx.Player1.Session!.PlayerId;
            var value = JsonValue.NewObject().Set("slot", JsonValue.Of(1L));

            var put = await _fx.ServerClient.Server.PutPlayerStorageAsync(playerId, "it-server-save", value);
            Assert.True(put.Version > 0);

            var got = await _fx.ServerClient.Server.GetPlayerStorageAsync(playerId, "it-server-save");
            Assert.Equal(1L, got.Value.OptLong("slot"));

            var page = await _fx.ServerClient.Server.ListPlayerStorageAsync(playerId,
                new StorageListOptions { KeyPrefix = "it-server-" });
            Assert.Contains(page.Items, o => o.Key == "it-server-save");

            var mine = await _fx.Player1.Storage.GetAsync("it-server-save");
            Assert.Equal(1L, mine.Value.OptLong("slot"));

            var conflict = await Assert.ThrowsAsync<GGScaleException>(
                () => _fx.ServerClient.Server.PutPlayerStorageAsync(playerId, "it-server-save", value, ifMatchVersion: put.Version + 100));
            Assert.True(conflict.IsConflict);
        }
    }

    [Collection("integration")]
    public class PasswordResetIntegrationTests
    {
        [Fact]
        public async Task Password_reset_request_always_accepted()
        {
            using var c = new GGScaleClient(new GGScaleClientOptions
            {
                BaseUrl = ItFixture.BaseUrl,
                ApiKey = ItFixture.PublishableKey,
            });

            // Noop mailer in the integration stack: the server answers 202
            // regardless of account existence — no exception means pass.
            await c.Auth.RequestPasswordResetAsync("it-nobody@example.com");
        }
    }
}
