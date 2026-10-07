using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    /// <summary>Wrappers and fields added for the server v0.9.71 sync, outside parties.</summary>
    public class SyncSurfaceTests
    {
        private static GGScaleClient NewClient(FakeTransport ft, bool withSession = true)
        {
            var client = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft });
            if (withSession)
            {
                client.SetSession(new Session("tok", "ref", 1, DateTimeOffset.UtcNow.AddHours(1)));
            }
            return client;
        }

        // ---- Matchmaker fields ----

        private const string TicketJson =
            "{\"id\":5,\"entry_id\":77,\"party_id\":12,\"status\":\"queued\",\"query\":\"skill > 3\"," +
            "\"string_properties\":{\"map\":\"arena\"},\"numeric_properties\":{\"skill\":4.5}," +
            "\"users\":[{\"player_id\":1,\"queue_entry_id\":77,\"party_id\":12}]}";

        [Fact]
        public void Ticket_reads_entry_id() =>
            Assert.Equal(77L, Ticket.FromJson(JsonValue.Parse(TicketJson)).EntryId);

        [Fact]
        public void Ticket_reads_party_id() =>
            Assert.Equal(12L, Ticket.FromJson(JsonValue.Parse(TicketJson)).PartyId);

        [Fact]
        public void Ticket_reads_query() =>
            Assert.Equal("skill > 3", Ticket.FromJson(JsonValue.Parse(TicketJson)).Query);

        [Fact]
        public void Ticket_reads_string_properties() =>
            Assert.Equal("arena", Ticket.FromJson(JsonValue.Parse(TicketJson)).StringProperties["map"]);

        [Fact]
        public void Ticket_reads_numeric_properties() =>
            Assert.Equal(4.5, Ticket.FromJson(JsonValue.Parse(TicketJson)).NumericProperties["skill"]);

        [Fact]
        public void RosterEntry_reads_queue_entry_id() =>
            Assert.Equal(77L, Ticket.FromJson(JsonValue.Parse(TicketJson)).Users[0].QueueEntryId);

        [Fact]
        public void MatchResult_FromPayload_reads_roster_party_id()
        {
            var payload = JsonValue.Parse("{\"ticket_id\":5,\"match_id\":\"m1\",\"users\":[{\"player_id\":1,\"queue_entry_id\":9,\"party_id\":12}]}");

            var result = MatchResult.FromPayload(payload);

            Assert.Equal(12L, result.Users[0].PartyId);
        }

        // ---- Health ----

        [Fact]
        public async Task Health_GetAsync_reads_status_version_and_commit()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"status\":\"ok\",\"version\":\"v0.9.71\",\"commit\":\"abc\"}") };
            using var client = NewClient(ft, withSession: false);

            var health = await client.Health.GetAsync();

            Assert.Equal("ok|v0.9.71|abc", health.Status + "|" + health.Version + "|" + health.Commit);
        }

        [Fact]
        public async Task Health_GetAsync_calls_the_healthz_route()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft, withSession: false);

            await client.Health.GetAsync();

            Assert.Equal("GET /v1/healthz", ft.LastRequest!.Method + " " + ft.LastRequest.Path);
        }

        // ---- Account deletion ----

        [Fact]
        public async Task RequestDeleteAsync_returns_the_pending_delete()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"delete_requested_at\":\"2026-10-07T10:00:00Z\",\"scheduled_purge_at\":\"2026-11-06T10:00:00Z\"}"),
            };
            using var client = NewClient(ft);

            var pending = await client.Auth.RequestDeleteAsync("pw");

            Assert.Equal(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero), pending.ScheduledPurgeAt);
        }

        [Fact]
        public async Task RequestDeleteAsync_sends_the_password_to_the_delete_route()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Auth.RequestDeleteAsync("pw");

            Assert.Equal("POST /v1/auth/delete {\"password\":\"pw\"}", ft.LastRequest!.Operation + " " + ft.LastRequest.Body);
        }

        [Fact]
        public async Task RequestDeleteAsync_clears_the_session_when_it_succeeds()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Auth.RequestDeleteAsync(null);

            Assert.Null(client.Session);
        }

        [Fact]
        public async Task CancelDeleteAsync_sends_no_session_token()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Auth.CancelDeleteAsync("a@b.test", "pw");

            Assert.Null(ft.LastRequest!.SessionToken);
        }

        [Fact]
        public async Task CancelDeleteAsync_posts_email_and_password_with_the_api_key()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Auth.CancelDeleteAsync("a@b.test", "pw");

            Assert.Equal("POST /v1/auth/delete/cancel pk {\"email\":\"a@b.test\",\"password\":\"pw\"}",
                ft.LastRequest!.Operation + " " + ft.LastRequest.ApiKey + " " + ft.LastRequest.Body);
        }

        [Fact]
        public async Task CancelDeleteAsync_throws_delete_requested_by_team_when_403_has_the_slug()
        {
            var ft = new FakeTransport { Respond = _ => throw HttpTransport.MapError(403, "{\"detail\":\"delete_requested_by_team\"}", null) };
            using var client = NewClient(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => client.Auth.CancelDeleteAsync("a@b.test", "pw"));

            Assert.True(ex.IsDeleteRequestedByTeam);
        }

        [Fact]
        public async Task CancelDeleteAsync_throws_not_found_when_no_deletion_is_pending()
        {
            var ft = new FakeTransport { Respond = _ => throw HttpTransport.MapError(404, "{\"detail\":\"no pending deletion\"}", null) };
            using var client = NewClient(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => client.Auth.CancelDeleteAsync("a@b.test", "pw"));

            Assert.True(ex.IsNotFound && !ex.IsDeleteRequestedByTeam);
        }

        // ---- Fleet heartbeat on the server client ----

        private static FleetHeartbeat Beat() =>
            new FleetHeartbeat { AgonesName = "gs-1", Fleet = "main", Address = "1.2.3.4:7777", MaxPlayers = 8 };

        [Fact]
        public async Task Server_FleetHeartbeatAsync_posts_with_the_api_key_and_no_session()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft, withSession: false);

            await client.Server.FleetHeartbeatAsync(Beat());

            Assert.Equal("POST /v1/fleets/heartbeat pk", ft.LastRequest!.Operation + " " + ft.LastRequest.ApiKey);
        }

        [Fact]
        public async Task Server_FleetHeartbeatAsync_rejects_a_heartbeat_without_capacity()
        {
            using var client = NewClient(new FakeTransport(), withSession: false);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                client.Server.FleetHeartbeatAsync(new FleetHeartbeat { AgonesName = "a", Fleet = "f", Address = "x" }));
        }

#pragma warning disable CS0618 // the old method must still work
        [Fact]
        public async Task Fleets_SendHeartbeatAsync_still_sends_the_heartbeat()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft, withSession: false);

            await client.Fleets.SendHeartbeatAsync(Beat());

            Assert.Equal("/v1/fleets/heartbeat", ft.LastRequest!.Path);
        }
#pragma warning restore CS0618

        // ---- Realtime ticket ----

        [Fact]
        public async Task Realtime_CreateTicketAsync_reads_the_ticket()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"ticket\":\"t-1\",\"expires_in_seconds\":30}") };
            using var client = NewClient(ft);

            var ticket = await client.Realtime.CreateTicketAsync();

            Assert.Equal("t-1 30", ticket.Ticket + " " + ticket.ExpiresInSeconds);
        }

        [Fact]
        public async Task Realtime_CreateTicketAsync_posts_with_the_session()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Realtime.CreateTicketAsync();

            Assert.Equal("POST /v1/ws/ticket tok", ft.LastRequest!.Operation + " " + ft.LastRequest.SessionToken);
        }

        [Fact]
        public void TicketUri_escapes_the_ticket()
        {
            var uri = GGScaleClient.TicketUri(new Uri("ws://api.test/v1/ws"), "a+b/c=");

            Assert.Equal("ws://api.test/v1/ws?ticket=a%2Bb%2Fc%3D", uri.AbsoluteUri);
        }

        // ---- Events ----

        [Fact]
        public void PresenceEvent_reads_the_payload()
        {
            var ev = PresenceEvent.FromPayload(JsonValue.Parse("{\"player_id\":7,\"status\":\"in_game\",\"session_id\":\"gs_1\"}"));

            Assert.Equal("7 in_game gs_1", ev.PlayerId + " " + ev.Status + " " + ev.SessionId);
        }

        [Fact]
        public void PresenceEvent_has_no_session_when_it_is_null() =>
            Assert.Null(PresenceEvent.FromPayload(JsonValue.Parse("{\"player_id\":7,\"status\":\"online\",\"session_id\":null}")).SessionId);

        [Fact]
        public void GameInviteEvent_reads_the_payload()
        {
            var ev = GameInviteEvent.FromPayload(JsonValue.Parse("{\"invite_id\":3,\"session_id\":\"gs_1\",\"join_code\":\"J\"}"));

            Assert.Equal("3 gs_1 J", ev.InviteId + " " + ev.SessionId + " " + ev.JoinCode);
        }

        [Fact]
        public void PartyChangedEvent_reads_the_payload()
        {
            var ev = PartyChangedEvent.FromPayload(JsonValue.Parse("{\"party_id\":4,\"version\":9,\"state\":\"queued\"}"));

            Assert.Equal("4 9 queued", ev.PartyId + " " + ev.Version + " " + ev.State);
        }

        [Fact]
        public void PartyInviteEvent_reads_the_payload()
        {
            var ev = PartyInviteEvent.FromPayload(JsonValue.Parse("{\"invite_id\":3,\"party_id\":4,\"from_player_id\":8}"));

            Assert.Equal("3 4 8", ev.InviteId + " " + ev.PartyId + " " + ev.FromPlayerId);
        }

        [Fact]
        public void RealtimeEvents_names_match_the_wire() =>
            Assert.Equal("presence game_invite party_changed party_invite matchmaker_matched",
                string.Join(" ", RealtimeEvents.Presence, RealtimeEvents.GameInvite, RealtimeEvents.PartyChanged,
                    RealtimeEvents.PartyInvite, RealtimeEvents.MatchmakerMatched));

        // ---- Iterate all pages ----

        [Fact]
        public async Task GameSessions_ListAllAsync_reads_every_page()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"session_id\":\"a\"}],\"next_cursor\":\"c1\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"session_id\":\"b\"}],\"next_cursor\":\"\"}"));
            using var client = NewClient(ft);

            var ids = new List<string>();
            await foreach (var s in client.GameSessions.ListAllAsync())
            {
                ids.Add(s.SessionId);
            }

            Assert.Equal("a,b", string.Join(",", ids));
        }

        [Fact]
        public async Task GameSessions_ListAllAsync_sends_the_cursor_for_the_next_page()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[],\"next_cursor\":\"c1\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[],\"next_cursor\":\"\"}"));
            using var client = NewClient(ft);

            await foreach (var _ in client.GameSessions.ListAllAsync())
            {
            }

            Assert.Equal("c1", ft.Requests[1].QueryValue("cursor"));
        }

        [Fact]
        public async Task Leaderboards_ListAllPeriodsAsync_reads_every_page()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"periods\":[{\"period\":3}],\"next_cursor\":\"c1\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"periods\":[{\"period\":2}],\"next_cursor\":\"\"}"));
            using var client = NewClient(ft);

            var periods = new List<int>();
            await foreach (var p in client.Leaderboards.ListAllPeriodsAsync(1))
            {
                periods.Add(p.Period);
            }

            Assert.Equal("3,2", string.Join(",", periods));
        }

        [Fact]
        public async Task Server_ListAllStorageAsync_reads_every_page()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"key\":\"a\"}],\"next_cursor\":\"c1\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"key\":\"b\"}],\"next_cursor\":\"\"}"));
            using var client = NewClient(ft, withSession: false);

            var keys = new List<string>();
            await foreach (var o in client.Server.ListAllStorageAsync(5))
            {
                keys.Add(o.Key);
            }

            Assert.Equal("a,b", string.Join(",", keys));
        }

        // ---- Operation strings match the spec route ----

        [Theory]
        [InlineData("request", "POST /v1/friends/{player_id}/request")]
        [InlineData("accept", "POST /v1/friends/{player_id}/accept")]
        [InlineData("block", "POST /v1/friends/{player_id}/block")]
        public async Task Friends_writes_use_the_spec_route_as_operation(string action, string operation)
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            switch (action)
            {
                case "request":
                    await client.Friends.RequestAsync(2);
                    break;
                case "accept":
                    await client.Friends.AcceptAsync(2);
                    break;
                default:
                    await client.Friends.BlockAsync(2);
                    break;
            }

            Assert.Equal(operation, ft.LastRequest!.Operation);
        }
    }

    public class TicketDialTests
    {
        [Fact]
        public async Task Dial_gets_a_new_ticket_for_each_connect_when_the_adapter_uses_tickets()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"ticket\":\"t\",\"expires_in_seconds\":30}") };
            var clock = new FakeClock();
            using var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", BaseUrl = "http://api.test", Transport = ft, Clock = clock });
            c.SetSession(Canned.Live());
            using var adapter = new FakeTicketSocketAdapter();
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rc = await c.DialRealtimeAsync(adapter);
            rc.StateChanged += (_, ch) =>
            {
                if (ch.State == RealtimeState.Connected)
                {
                    connected.TrySetResult(true);
                }
            };

            adapter.PushClose(null);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await rc.CloseAsync();

            Assert.Equal(2, ft.CountForPath("/v1/ws/ticket"));
        }

        [Fact]
        public async Task Dial_opens_the_ticket_url_when_the_adapter_uses_tickets()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"ticket\":\"t 1\",\"expires_in_seconds\":30}") };
            using var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", BaseUrl = "https://api.test", Transport = ft });
            c.SetSession(Canned.Live());
            using var adapter = new FakeTicketSocketAdapter();

            var rc = await c.DialRealtimeAsync(adapter);
            await rc.CloseAsync();

            Assert.Equal("wss://api.test/v1/ws?ticket=t%201", adapter.TicketUris[0].AbsoluteUri);
        }

        [Fact]
        public async Task Dial_sends_no_header_credentials_when_the_adapter_uses_tickets()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.Parse("{\"ticket\":\"t\",\"expires_in_seconds\":30}") };
            using var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", BaseUrl = "http://api.test", Transport = ft });
            c.SetSession(Canned.Live());
            using var adapter = new FakeTicketSocketAdapter();

            var rc = await c.DialRealtimeAsync(adapter);
            await rc.CloseAsync();

            Assert.Null(adapter.ApiKey);
        }
    }

    /// <summary>A scripted adapter that connects with a ticket URL and no headers.</summary>
    internal sealed class FakeTicketSocketAdapter : ITicketSocketAdapter, IDisposable
    {
        private readonly FakeSocketAdapter _inner = new FakeSocketAdapter();

        public List<Uri> TicketUris { get; } = new List<Uri>();

        public string? ApiKey => _inner.ApiKey;

        public int? CloseCode => _inner.CloseCode;

        public string? CloseDescription => _inner.CloseDescription;

        public void PushClose(int? code) => _inner.PushClose(code);

        public Task ConnectAsync(Uri uri, string apiKey, string sessionToken, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a ticket adapter must not get header credentials");

        public async Task ConnectWithTicketAsync(Func<CancellationToken, Task<Uri>> ticketUri, CancellationToken cancellationToken)
        {
            var uri = await ticketUri(cancellationToken);
            lock (TicketUris)
            {
                TicketUris.Add(uri);
            }
        }

        public Task<string?> ReceiveAsync(CancellationToken cancellationToken) => _inner.ReceiveAsync(cancellationToken);

        public Task CloseAsync() => _inner.CloseAsync();

        public void Dispose() => _inner.Dispose();
    }
}
