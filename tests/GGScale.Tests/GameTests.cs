using System;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    internal static class CannedGame
    {
        public static JsonValue Session() =>
            JsonValue.NewObject()
                .Set("session_id", JsonValue.Of("gs_abc123"))
                .Set("join_code", JsonValue.Of("XKCD42"))
                .Set("state", JsonValue.Of("open"))
                .Set("peers", JsonValue.NewArray()
                    .Add(JsonValue.NewObject()
                        .Set("player_id", JsonValue.Of(1L))
                        .Set("xuid", JsonValue.Of("x1"))
                        .Set("addr", JsonValue.NewObject()
                            .Set("ip", JsonValue.Of("203.0.113.1"))
                            .Set("port", JsonValue.Of(7777L)))));
    }

    public class GameSessionsServiceTests
    {
        [Fact]
        public async Task Create_posts_body_and_decodes_session()
        {
            var ft = new FakeTransport { Respond = _ => CannedGame.Session() };
            var c = TestClients.WithSession(ft);

            var sess = await c.GameSessions.CreateAsync(new GameSessionCreate
            {
                TitleId = "my-game",
                PublicAddr = new GameSessionAddr("203.0.113.1", 7777),
                MaxPlayers = 4,
                Private = true,
                Props = JsonValue.NewObject().Set("map", JsonValue.Of("dm_lobby")),
            });

            Assert.Equal("POST", ft.LastRequest!.Method);
            Assert.Equal("/v1/game-session", ft.LastRequest.Path);
            var body = ft.LastRequest.Body!;
            Assert.Equal("my-game", body.OptString("title_id"));
            Assert.Equal(7777L, body["public_addr"].OptLong("port"));
            Assert.Equal(4L, body.OptLong("max_players"));
            Assert.True(body.OptBool("private"));
            Assert.Equal("dm_lobby", body["props"].OptString("map"));

            Assert.Equal("gs_abc123", sess.SessionId);
            Assert.Equal("XKCD42", sess.JoinCode);
            Assert.Single(sess.Peers);
            Assert.Equal(7777, sess.Peers[0].Addr.Port);
        }

        [Fact]
        public async Task Create_omits_optional_fields()
        {
            var ft = new FakeTransport { Respond = _ => CannedGame.Session() };
            var c = TestClients.WithSession(ft);

            await c.GameSessions.CreateAsync(new GameSessionCreate { PublicAddr = new GameSessionAddr("1.2.3.4", 1) });

            var body = ft.LastRequest!.Body!;
            Assert.Null(body.Opt("title_id"));
            Assert.Null(body.Opt("max_players"));
            Assert.Null(body.Opt("private"));
            Assert.Null(body.Opt("props"));
        }

        [Fact]
        public async Task Get_reads_session_by_id()
        {
            var ft = new FakeTransport { Respond = _ => CannedGame.Session() };
            var c = TestClients.WithSession(ft);

            var sess = await c.GameSessions.GetAsync("gs_abc123");

            Assert.Equal("/v1/game-session/gs_abc123", ft.LastRequest!.Path);
            Assert.Equal("open", sess.State);
        }

        [Fact]
        public async Task Resolve_passes_join_code_query()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.NewObject().Set("session_id", JsonValue.Of("gs_abc123")) };
            var c = TestClients.WithSession(ft);

            var id = await c.GameSessions.ResolveAsync("XKCD42");

            Assert.Equal("/v1/game-session", ft.LastRequest!.Path);
            Assert.Equal("XKCD42", ft.LastRequest.QueryValue("joinCode"));
            Assert.Equal("gs_abc123", id);
        }

        [Fact]
        public async Task Join_posts_public_addr()
        {
            var ft = new FakeTransport { Respond = _ => CannedGame.Session() };
            var c = TestClients.WithSession(ft);

            await c.GameSessions.JoinAsync("gs_abc123", new GameSessionAddr("198.51.100.7", 7778));

            Assert.Equal("/v1/game-session/gs_abc123/join", ft.LastRequest!.Path);
            Assert.Equal("198.51.100.7", ft.LastRequest.Body!["public_addr"].OptString("ip"));
        }

        [Fact]
        public async Task Heartbeat_with_qos_sends_blob()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("ok", JsonValue.True)
                    .Set("peers", CannedGame.Session()["peers"]),
            };
            var c = TestClients.WithSession(ft);

            var peers = await c.GameSessions.HeartbeatAsync("gs_abc123", JsonValue.NewObject().Set("rtt_ms", JsonValue.Of(23L)));

            Assert.Equal("/v1/game-session/gs_abc123/heartbeat", ft.LastRequest!.Path);
            Assert.Equal(23L, ft.LastRequest.Body!["qos"].OptLong("rtt_ms"));
            Assert.Single(peers);
        }

        [Fact]
        public async Task Heartbeat_null_qos_sends_empty_object()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject().Set("ok", JsonValue.True).Set("peers", JsonValue.NewArray()),
            };
            var c = TestClients.WithSession(ft);

            await c.GameSessions.HeartbeatAsync("gs_abc123");

            Assert.Equal("{}", ft.LastRequest!.Body!.ToString());
        }

        [Fact]
        public async Task Leave_deletes_session()
        {
            var ft = new FakeTransport();
            var c = TestClients.WithSession(ft);

            await c.GameSessions.LeaveAsync("gs_abc123");

            Assert.Equal("DELETE", ft.LastRequest!.Method);
            Assert.Equal("/v1/game-session/gs_abc123", ft.LastRequest.Path);
        }

        [Fact]
        public async Task Join_gone_surfaces_410_status()
        {
            var ft = new FakeTransport { Respond = _ => throw new GGScaleException(410, "", "session no longer joinable") };
            var c = TestClients.WithSession(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => c.GameSessions.JoinAsync("gs_dead", new GameSessionAddr("1.2.3.4", 1)));

            Assert.Equal(410, ex.Status);
        }
    }

    public class MatchmakerServiceTests
    {
        private static JsonValue CannedTicket(string status = "queued") =>
            JsonValue.NewObject()
                .Set("id", JsonValue.Of(7L))
                .Set("status", JsonValue.Of(status))
                .Set("region", JsonValue.Of("us-east-1"))
                .Set("game_mode", JsonValue.Of("deathmatch"))
                .Set("match_address", JsonValue.Of(status == "matched" ? "10.0.0.1:7777" : ""))
                .Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"));

        [Fact]
        public async Task CreateTicket_posts_request()
        {
            var ft = new FakeTransport { Respond = _ => CannedTicket() };
            var c = TestClients.WithSession(ft);

            var ticket = await c.Matchmaker.CreateTicketAsync(new MatchRequest
            {
                Fleet = "docker-default",
                Region = "us-east-1",
                GameMode = "deathmatch",
            });

            Assert.Equal("/v1/matchmaker/tickets", ft.LastRequest!.Path);
            Assert.Equal("docker-default", ft.LastRequest.Body!.OptString("fleet"));
            Assert.Equal(7L, ticket.Id);
            Assert.Equal("queued", ticket.Status);
        }

        [Fact]
        public async Task GetTicket_reads_by_id()
        {
            var ft = new FakeTransport { Respond = _ => CannedTicket("matched") };
            var c = TestClients.WithSession(ft);

            var ticket = await c.Matchmaker.GetTicketAsync(7);

            Assert.Equal("/v1/matchmaker/tickets/7", ft.LastRequest!.Path);
            Assert.Equal("10.0.0.1:7777", ticket.MatchAddress);
            Assert.Null(ticket.MatchedAt);
        }

        [Fact]
        public async Task CancelTicket_deletes()
        {
            var ft = new FakeTransport();
            var c = TestClients.WithSession(ft);

            await c.Matchmaker.CancelTicketAsync(99);

            Assert.Equal("DELETE", ft.LastRequest!.Method);
            Assert.Equal("/v1/matchmaker/tickets/99", ft.LastRequest.Path);
        }

        [Fact]
        public async Task GetTicket_parses_GA_fields()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("id", JsonValue.Of(42L))
                    .Set("status", JsonValue.Of("matched"))
                    .Set("mode", JsonValue.Of("game_session"))
                    .Set("match_id", JsonValue.Of("mm_abc"))
                    .Set("session_id", JsonValue.Of("gs_1"))
                    .Set("join_code", JsonValue.Of("CODE01"))
                    .Set("host_player_id", JsonValue.Of(41L))
                    .Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"))
                    .Set("users", JsonValue.NewArray()
                        .Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(41L))
                            .Set("attributes", JsonValue.NewObject().Set("lobby", JsonValue.Of("A"))))
                        .Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(42L)))),
            };
            var c = TestClients.WithSession(ft);

            var ticket = await c.Matchmaker.GetTicketAsync(42);

            Assert.Equal("game_session", ticket.Mode);
            Assert.Equal("gs_1", ticket.SessionId);
            Assert.Equal("CODE01", ticket.JoinCode);
            Assert.Equal(41L, ticket.HostPlayerId);
            Assert.Equal(2, ticket.Users.Count);
            Assert.Equal("A", ticket.Users[0].Attributes.OptString("lobby"));
        }

        [Fact]
        public async Task GetTicket_reads_failure_reason()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("id", JsonValue.Of(9L))
                    .Set("status", JsonValue.Of("failed"))
                    .Set("failure_reason", JsonValue.Of("expired"))
                    .Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z")),
            };
            var c = TestClients.WithSession(ft);

            var ticket = await c.Matchmaker.GetTicketAsync(9);

            Assert.Equal("failed", ticket.Status);
            Assert.Equal("expired", ticket.FailureReason);
        }

        [Fact]
        public async Task CreateTicket_surfaces_ticket_already_active()
        {
            var ft = new FakeTransport
            {
                Respond = _ => throw new GGScaleException(
                    409, string.Empty, "ticket_already_active", null, 0,
                    new[] { new GGErrorDetail("player already has an active ticket", "active_ticket_id", JsonValue.Of(55L)) }),
            };
            var c = TestClients.WithSession(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => c.Matchmaker.CreateTicketAsync(new MatchRequest { Mode = MatchMode.MatchOnly }));

            Assert.True(ex.IsTicketAlreadyActive);
            Assert.Equal(55L, ex.ActiveTicketId);
        }

        [Fact]
        public async Task WaitForMatch_recovers_by_polling()
        {
            var calls = 0;
            var ft = new FakeTransport
            {
                Respond = req =>
                {
                    if (req.Method == "POST")
                    {
                        return JsonValue.NewObject().Set("id", JsonValue.Of(7L)).Set("status", JsonValue.Of("queued")).Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"));
                    }
                    calls++;
                    var status = calls >= 2 ? "matched" : "queued";
                    return JsonValue.NewObject()
                        .Set("id", JsonValue.Of(7L)).Set("status", JsonValue.Of(status)).Set("mode", JsonValue.Of("match_only"))
                        .Set("match_id", JsonValue.Of("mm_xy")).Set("host_player_id", JsonValue.Of(7L))
                        .Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"))
                        .Set("users", JsonValue.NewArray().Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(7L))).Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(8L))));
                },
            };
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "http://api.test", Transport = ft });
            c.SetSession(Canned.Live());
            c.Matchmaker.PollInterval = TimeSpan.FromMilliseconds(10);
            var adapter = new FakeSocketAdapter();
            adapter.EndStream(); // no realtime delivery → polling recovers

            var result = await c.Matchmaker.WaitForMatchAsync(new MatchRequest { Mode = MatchMode.MatchOnly }, adapter);

            Assert.Equal("match_only", result.Mode);
            Assert.Equal(7L, result.HostPlayerId);
            Assert.Equal(2, result.Users.Count);
        }

        [Fact]
        public async Task ConnectP2P_game_session_joins_and_scopes_relay()
        {
            var ft = new FakeTransport
            {
                Respond = req =>
                {
                    if (req.Path == "/v1/matchmaker/tickets" && req.Method == "POST")
                    {
                        return JsonValue.NewObject().Set("id", JsonValue.Of(7L)).Set("status", JsonValue.Of("queued")).Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"));
                    }
                    if (req.Path == "/v1/matchmaker/tickets/7")
                    {
                        return JsonValue.NewObject()
                            .Set("id", JsonValue.Of(7L)).Set("status", JsonValue.Of("matched")).Set("mode", JsonValue.Of("game_session"))
                            .Set("match_id", JsonValue.Of("mm_room")).Set("session_id", JsonValue.Of("gs_9")).Set("join_code", JsonValue.Of("JC01"))
                            .Set("host_player_id", JsonValue.Of(9L)).Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"))
                            .Set("users", JsonValue.NewArray().Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(9L))));
                    }
                    if (req.Path == "/v1/relay/credentials")
                    {
                        Assert.Equal("mm_room", req.QueryValue("match_id"));
                        return JsonValue.NewObject().Set("username", JsonValue.Of("u")).Set("password", JsonValue.Of("p")).Set("ttl", JsonValue.Of(300L)).Set("realm", JsonValue.Of("ggscale"));
                    }
                    if (req.Path == "/v1/game-session/gs_9/join")
                    {
                        return JsonValue.NewObject().Set("session_id", JsonValue.Of("gs_9")).Set("join_code", JsonValue.Of("JC01")).Set("state", JsonValue.Of("open"))
                            .Set("peers", JsonValue.NewArray()
                                .Add(JsonValue.NewObject().Set("player_id", JsonValue.Of(9L)).Set("addr", JsonValue.NewObject().Set("ip", JsonValue.Of("1.1.1.1")).Set("port", JsonValue.Of(40000L)))));
                    }
                    return JsonValue.Null;
                },
            };
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "http://api.test", Transport = ft });
            c.SetSession(Canned.Live()); // player id 9
            c.Matchmaker.PollInterval = TimeSpan.FromMilliseconds(10);
            var adapter = new FakeSocketAdapter();
            adapter.EndStream();

            var p2p = await c.Matchmaker.ConnectP2PAsync(new MatchRequest { Mode = MatchMode.GameSession }, new GameSessionAddr("3.3.3.3", 50000), adapter);

            Assert.True(p2p.IsHost);
            Assert.NotNull(p2p.Relay);
            Assert.Equal("u", p2p.Relay!.Username);
            Assert.NotNull(p2p.Session);
            Assert.Single(p2p.Session!.Peers);
        }
    }

    public class FleetsServiceTests
    {
        [Fact]
        public async Task SendHeartbeat_uses_api_key_without_session()
        {
            var ft = new FakeTransport();
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "secret-k", Transport = ft });

            await c.Fleets.SendHeartbeatAsync(new FleetHeartbeat
            {
                AgonesName = "srv-1",
                Fleet = "docker-default",
                Address = "10.0.0.1:7777",
                MaxPlayers = 16,
            });

            Assert.Equal("/v1/fleets/heartbeat", ft.LastRequest!.Path);
            Assert.Equal("secret-k", ft.LastRequest.ApiKey);
            Assert.Null(ft.LastRequest.SessionToken);
            Assert.Equal(16L, ft.LastRequest.Body!.OptLong("max_players"));
        }

        [Fact]
        public async Task SendHeartbeat_validates_required_fields()
        {
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", Transport = new FakeTransport() });
            await Assert.ThrowsAsync<ArgumentException>(
                () => c.Fleets.SendHeartbeatAsync(new FleetHeartbeat { Fleet = "f", Address = "a", MaxPlayers = 1 }));
        }

        [Fact]
        public async Task ListServers_parses_entries()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject().Set("servers", JsonValue.NewArray()
                    .Add(JsonValue.NewObject()
                        .Set("name", JsonValue.Of("srv-1"))
                        .Set("address", JsonValue.Of("10.0.0.1:7777"))
                        .Set("current_players", JsonValue.Of(3L))
                        .Set("max_players", JsonValue.Of(16L)))),
            };
            var c = TestClients.WithSession(ft);

            var servers = await c.Fleets.ListServersAsync("docker-default");

            Assert.Equal("/v1/fleets/docker-default/servers", ft.LastRequest!.Path);
            Assert.Single(servers);
            Assert.Equal(3, servers[0].CurrentPlayers);
        }
    }

    public class RelayServiceTests
    {
        [Fact]
        public async Task GetCredentials_posts_and_parses()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("username", JsonValue.Of("u"))
                    .Set("password", JsonValue.Of("p"))
                    .Set("ttl", JsonValue.Of(600L))
                    .Set("realm", JsonValue.Of("ggscale"))
                    .Set("urls", JsonValue.NewArray().Add(JsonValue.Of("turn:relay.example.com:3478"))),
            };
            var c = TestClients.WithSession(ft);

            var creds = await c.Relay.GetCredentialsAsync();

            Assert.Equal("POST", ft.LastRequest!.Method);
            Assert.Equal("/v1/relay/credentials", ft.LastRequest.Path);
            Assert.Null(ft.LastRequest.QueryValue("match_id"));
            Assert.Equal(600L, creds.Ttl);
            Assert.Single(creds.Urls);
        }

        [Fact]
        public async Task GetCredentials_with_match_id_adds_query()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("username", JsonValue.Of("u")).Set("password", JsonValue.Of("p"))
                    .Set("ttl", JsonValue.Of(300L)).Set("realm", JsonValue.Of("ggscale")),
            };
            var c = TestClients.WithSession(ft);

            await c.Relay.GetCredentialsAsync("mm_room");

            Assert.Equal("/v1/relay/credentials", ft.LastRequest!.Path);
            Assert.Equal("mm_room", ft.LastRequest.QueryValue("match_id"));
        }
    }

    public class ServerServiceTests
    {
        [Fact]
        public async Task VerifySession_posts_token_and_parses_player()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("player_id", JsonValue.Of(7L))
                    .Set("external_id", JsonValue.Of("steam:1234"))
                    .Set("email", JsonValue.Of("demo@example.com")),
            };
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "ggs_secret", Transport = ft });

            var res = await c.Server.VerifySessionAsync("player.jwt");

            Assert.Equal("/v1/server/player-sessions/verify", ft.LastRequest!.Path);
            Assert.Equal("ggs_secret", ft.LastRequest.ApiKey);
            Assert.Null(ft.LastRequest.SessionToken);
            Assert.Equal("player.jwt", ft.LastRequest.Body!.OptString("session_token"));
            Assert.Equal(7L, res.PlayerId);
            Assert.Equal("steam:1234", res.ExternalId);
        }

        [Fact]
        public async Task VerifySession_rejects_empty_token_before_network()
        {
            var ft = new FakeTransport();
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", Transport = ft });

            await Assert.ThrowsAsync<ArgumentException>(() => c.Server.VerifySessionAsync(""));
            Assert.Equal(0, ft.CallCount);
        }

        [Fact]
        public async Task PlayerRemoteAddrs_reads_by_player_id()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject().Set("addresses", JsonValue.NewArray()),
            };
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", Transport = ft });

            var addrs = await c.Server.PlayerRemoteAddrsAsync(42);

            Assert.Equal("/v1/server/players/42/remote-addrs", ft.LastRequest!.Path);
            Assert.Empty(addrs);
        }
    }
}
