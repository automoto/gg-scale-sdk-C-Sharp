using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GGScale.IntegrationTests
{
    [Collection("integration")]
    public class E2ERelayIntegrationTests
    {
        // The relay node that scripts/integration-test.sh registers.
        private const string RelayRegion = "it-east";
        private const string TurnUrl = "turn:127.0.0.1:13479?transport=udp";
        private const string StunUrl = "stun:127.0.0.1:13479";

        private readonly ItFixture _fx;

        public E2ERelayIntegrationTests(ItFixture fx) => _fx = fx;

        private sealed class Peer
        {
            public GGScaleClient Client = null!;
            public MatchResult Match = null!;
            public RelayCredentials Creds = null!;
            public long PlayerId => Client.Session!.PlayerId;
        }

        /// <summary>
        /// Follows the connect flow of a two-player WebRTC game such as
        /// gopher bash: anonymous login, a game_session match, match-scoped
        /// relay credentials, then an offer and an answer over game session
        /// signals. A WebRTC game sends SDP in the signals; here each peer
        /// sends its TURN relayed address, which is the candidate a
        /// relay-only ICE session would use. The peers then exchange
        /// datagrams through the relay.
        /// </summary>
        [Fact]
        public async Task Two_players_match_signal_and_connect_through_the_relay()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = cts.Token;

            var request = new MatchRequest
            {
                Mode = "game_session",
                GameMode = "gopher-bash-it",
                MinCount = 2,
                MaxCount = 2,
                CountMultiple = 2,
                StringProperties = new Dictionary<string, string> { ["transport"] = "webrtc" },
            };
            var peers = await Task.WhenAll(new[] { _fx.Player1, _fx.Player2 }.Select(async c =>
            {
                var match = await c.Matchmaker.WaitForMatchAsync(request, cancellationToken: ct);
                var creds = await c.Relay.GetCredentialsAsync(
                    new RelayCredentialsRequest { MatchId = match.MatchId, Region = RelayRegion }, ct);
                return new Peer { Client = c, Match = match, Creds = creds };
            }));

            var host = peers.Single(p => p.PlayerId == p.Match.HostPlayerId);
            var guest = peers.Single(p => p != host);
            Assert.Equal(host.Match.SessionId, guest.Match.SessionId);
            var sessionId = host.Match.SessionId;

            foreach (var p in peers)
            {
                Assert.Equal(new[] { TurnUrl }, p.Creds.Urls);
                Assert.Equal(new[] { StunUrl }, p.Creds.StunUrls);
                Assert.Equal(RelayRegion, p.Creds.Region);
            }

            // A long session renews its credentials with KeepFreshAsync; the
            // first callback comes at once. The guest allocates with those.
            using (var keep = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guest.Client.Relay.KeepFreshAsync(c =>
                {
                    guest.Creds = c;
                    keep.Cancel();
                }, new RelayCredentialsRequest { MatchId = guest.Match.MatchId, Region = RelayRegion }, keep.Token));
            }

            using var hostTurn = await TurnClient.AllocateAsync(host.Creds, ct);
            using var guestTurn = await TurnClient.AllocateAsync(guest.Creds, ct);

            await host.Client.GameSessions.SendSignalAsync(sessionId, guest.PlayerId, "n1",
                GameSessionSignalKind.Offer, hostTurn.RelayedAddress.ToString(), ct);
            var offer = await WaitSignalAsync(guest.Client, sessionId, host.PlayerId, GameSessionSignalKind.Offer, ct);

            await guest.Client.GameSessions.SendSignalAsync(sessionId, host.PlayerId, offer.NegotiationId,
                GameSessionSignalKind.Answer, guestTurn.RelayedAddress.ToString(), ct);
            var answer = await WaitSignalAsync(host.Client, sessionId, guest.PlayerId, GameSessionSignalKind.Answer, ct);

            var hostAddr = IPEndPoint.Parse(offer.Payload);
            var guestAddr = IPEndPoint.Parse(answer.Payload);

            // The relay drops data from a peer without a permission.
            await hostTurn.CreatePermissionAsync(guestAddr, ct);
            await guestTurn.CreatePermissionAsync(hostAddr, ct);

            await hostTurn.SendAsync(guestAddr, "ping");
            await guestTurn.ReceiveAsync("ping", ct);
            await guestTurn.SendAsync(hostAddr, "pong");
            await hostTurn.ReceiveAsync("pong", ct);
        }

        // WaitSignalAsync polls the session's signals until one of kind
        // arrives from fromId, as the game's signal loop does.
        private static async Task<GameSessionSignal> WaitSignalAsync(
            GGScaleClient c, string sessionId, long fromId, string kind, CancellationToken ct)
        {
            long cursor = 0;
            while (true)
            {
                foreach (var s in await c.GameSessions.PollSignalsAsync(sessionId, cursor, ct))
                {
                    cursor = Math.Max(cursor, s.Id);
                    if (s.FromPlayerId == fromId && s.Kind == kind)
                    {
                        return s;
                    }
                }
                await Task.Delay(250, ct);
            }
        }
    }
}
