using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GGScale.IntegrationTests
{
    [Collection("integration")]
    public class E2ERelayIntegrationTests
    {
        // The relay nodes that scripts/integration-test.sh registers. Both are
        // in one region. Peers must allocate on the same relay: each relay's
        // relayed addresses are loopback inside its own container.
        private const string RelayRegion = "it-east";
        private const string TurnUrl = "turn:127.0.0.1:13479?transport=udp";
        private const string TurnUrl2 = "turn:127.0.0.1:13481?transport=udp";
        private const string StunUrl = "stun:127.0.0.1:13479";

        // relay2's health listener, published for the failover test.
        private const string Relay2Lifecycle = "http://127.0.0.1:19092/lifecycle";
        private const string LifecycleToken = "integration-relay-lifecycle-token-0123456789";
        private const string AdminToken = "integration-platform-admin-token-0123456789";

        private static readonly HttpClient Http = new HttpClient();

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

            var (host, guest) = await MatchPairAsync("gopher-bash-it", ct);
            foreach (var p in new[] { host, guest })
            {
                Assert.Equal(new[] { TurnUrl, TurnUrl2 }, p.Creds.Urls.OrderBy(u => u, StringComparer.Ordinal));
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

            using var hostTurn = await TurnClient.AllocateAsync(host.Creds, TurnUrl, ct);
            using var guestTurn = await TurnClient.AllocateAsync(guest.Creds, TurnUrl, ct);
            await ConnectThroughRelayAsync(host, guest, hostTurn, guestTurn,
                GameSessionSignalKind.Offer, GameSessionSignalKind.Answer, ct);
        }

        /// <summary>
        /// Drains the relay a match uses and recovers the session on the
        /// region's other relay with the README's ICE restart recipe: fresh
        /// credentials (they list only relays that take new sessions), then
        /// restart_offer from the peer with the lower player ID and
        /// restart_answer from the other.
        /// </summary>
        [Fact]
        public async Task Session_recovers_through_the_other_relay_after_a_drain()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var ct = cts.Token;

            var (host, guest) = await MatchPairAsync("gopher-bash-it-failover", ct);
            using var hostTurn = await TurnClient.AllocateAsync(host.Creds, TurnUrl2, ct);
            using var guestTurn = await TurnClient.AllocateAsync(guest.Creds, TurnUrl2, ct);
            var (_, guestAddr) = await ConnectThroughRelayAsync(host, guest, hostTurn, guestTurn,
                GameSessionSignalKind.Offer, GameSessionSignalKind.Answer, ct);

            await Relay2LifecycleAsync("drain", ct);
            try
            {
                await WaitRelay2EligibleAsync(false, ct);

                // A drain refuses new allocations; the live one keeps
                // relaying until the relay stops.
                await hostTurn.SendAsync(guestAddr, "still here");
                await guestTurn.ReceiveAsync("still here", ct);

                foreach (var p in new[] { host, guest })
                {
                    p.Creds = await p.Client.Relay.GetCredentialsAsync(
                        new RelayCredentialsRequest { MatchId = p.Match.MatchId, Region = RelayRegion }, ct);
                    Assert.Equal(new[] { TurnUrl }, p.Creds.Urls);
                }

                var (offerer, answerer) = host.PlayerId < guest.PlayerId ? (host, guest) : (guest, host);
                using var offererTurn = await TurnClient.AllocateAsync(offerer.Creds, offerer.Creds.Urls[0], ct);
                using var answererTurn = await TurnClient.AllocateAsync(answerer.Creds, answerer.Creds.Urls[0], ct);
                await ConnectThroughRelayAsync(offerer, answerer, offererTurn, answererTurn,
                    GameSessionSignalKind.RestartOffer, GameSessionSignalKind.RestartAnswer, ct);
            }
            finally
            {
                await Relay2LifecycleAsync("resume", CancellationToken.None);
                await WaitRelay2EligibleAsync(true, CancellationToken.None);
            }
        }

        // MatchPairAsync matches the fixture's two players into one game
        // session and gets each one match-scoped credentials for the test
        // region. It returns the host first.
        private async Task<(Peer Host, Peer Guest)> MatchPairAsync(string gameMode, CancellationToken ct)
        {
            var request = new MatchRequest
            {
                Mode = "game_session",
                GameMode = gameMode,
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
            return (host, guest);
        }

        // ConnectThroughRelayAsync sends the offerer's relayed address in an
        // offerKind signal and the answerer's in the answerKind reply, then
        // checks datagrams pass both ways. It returns both relayed addresses.
        private static async Task<(IPEndPoint Offerer, IPEndPoint Answerer)> ConnectThroughRelayAsync(
            Peer offerer, Peer answerer, TurnClient offererTurn, TurnClient answererTurn,
            string offerKind, string answerKind, CancellationToken ct)
        {
            var sessionId = offerer.Match.SessionId;
            await offerer.Client.GameSessions.SendSignalAsync(sessionId, answerer.PlayerId, offerKind,
                offerKind, offererTurn.RelayedAddress.ToString(), ct);
            var offer = await WaitSignalAsync(answerer.Client, sessionId, offerer.PlayerId, offerKind, ct);

            await answerer.Client.GameSessions.SendSignalAsync(sessionId, offerer.PlayerId, offer.NegotiationId,
                answerKind, answererTurn.RelayedAddress.ToString(), ct);
            var answer = await WaitSignalAsync(offerer.Client, sessionId, answerer.PlayerId, answerKind, ct);

            var offererAddr = IPEndPoint.Parse(offer.Payload);
            var answererAddr = IPEndPoint.Parse(answer.Payload);

            // The relay drops data from a peer without a permission.
            await offererTurn.CreatePermissionAsync(answererAddr, ct);
            await answererTurn.CreatePermissionAsync(offererAddr, ct);

            await offererTurn.SendAsync(answererAddr, "ping");
            await answererTurn.ReceiveAsync("ping", ct);
            await answererTurn.SendAsync(offererAddr, "pong");
            await offererTurn.ReceiveAsync("pong", ct);
            return (offererAddr, answererAddr);
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

        // Relay2LifecycleAsync posts a drain or resume to relay2, as an
        // operator's deploy does.
        private static async Task Relay2LifecycleAsync(string action, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Relay2Lifecycle}/{action}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LifecycleToken);
            using var resp = await Http.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // WaitRelay2EligibleAsync waits until the registry's probe (every
        // 10 s) shows relay2 as eligible or not, plus the API server's 2 s
        // credential cache.
        private static async Task WaitRelay2EligibleAsync(bool want, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await Relay2EligibleAsync(ct) != want)
            {
                Assert.True(DateTime.UtcNow < deadline, $"relay2 eligible={want} after 30 s");
                await Task.Delay(1000, ct);
            }
            await Task.Delay(3000, ct);
        }

        private static async Task<bool?> Relay2EligibleAsync(CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(ItFixture.BaseUrl + "/v1/admin/relay-nodes"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            using var resp = await Http.SendAsync(req, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var n in doc.RootElement.GetProperty("nodes").EnumerateArray())
            {
                if (n.GetProperty("state").GetString() == "known"
                    && n.GetProperty("turn_urls").EnumerateArray().Any(u => u.GetString() == TurnUrl2))
                {
                    return n.GetProperty("eligible").GetBoolean();
                }
            }
            return null;
        }
    }
}
