using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    internal static class CannedParty
    {
        public const long Me = 9;

        public static string Json(long version, string state = "idle", string lastMatch = "", long myTicket = 0) =>
            "{\"id\":4,\"project_id\":1,\"leader_id\":9,\"state\":\"" + state + "\",\"version\":" + version +
            ",\"roster_version\":1,\"settings\":{\"mode\":\"match_only\",\"fleet_id\":3,\"region\":\"eu\",\"game_mode\":\"ctf\"," +
            "\"min_count\":2,\"max_count\":4,\"count_multiple\":2,\"allow_cross_region\":true,\"query\":\"q\"},\"max_members\":4," +
            "\"current_queue_entry_id\":11,\"last_match_id\":\"" + lastMatch + "\",\"members\":[{\"player_id\":9,\"ready_version\":1," +
            "\"ticket_id\":" + myTicket + ",\"string_properties\":{\"role\":\"tank\"},\"numeric_properties\":{\"skill\":3}," +
            "\"attributes\":{\"name\":\"<b>x</b>\"},\"joined_at\":\"2026-10-07T10:00:00Z\",\"last_seen_at\":\"2026-10-07T10:00:00Z\"," +
            "\"disconnect_deadline\":\"2026-10-07T10:00:30Z\"}]}";

        public static JsonValue Party(long version, string state = "idle", string lastMatch = "", long myTicket = 0) =>
            JsonValue.Parse(Json(version, state, lastMatch, myTicket));

        public static GGScaleException Error(int status, string detail) =>
            HttpTransport.MapError(status, "{\"detail\":\"" + detail + "\"}", null);
    }

    public class PartiesWrapperTests
    {
        private static GGScaleClient NewClient(FakeTransport ft)
        {
            var client = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft });
            client.SetSession(new Session("tok", "ref", CannedParty.Me, DateTimeOffset.UtcNow.AddHours(1)));
            return client;
        }

        private static readonly MatchRequest Settings = new MatchRequest { Mode = MatchMode.MatchOnly, MinCount = 2, MaxCount = 2 };

        private static Task Call(PartiesService p, string name)
        {
            switch (name)
            {
                case "create": return p.CreateAsync(Settings);
                case "current": return p.CurrentAsync();
                case "get": return p.GetAsync(4);
                case "update": return p.UpdateAsync(4, 2, Settings);
                case "disband": return p.DisbandAsync(4, 2);
                case "heartbeat": return p.HeartbeatAsync(4, 2);
                case "join": return p.JoinByCodeAsync("ABCDEFGHIJKLMNOP");
                case "leave": return p.LeaveAsync(4, 2);
                case "ready": return p.SetReadyAsync(4, 2, true);
                case "kick": return p.KickAsync(4, 2, 7);
                case "createCode": return p.CreateCodeAsync(4, 2);
                case "revokeCode": return p.RevokeCodeAsync(4, 2, 6);
                case "invite": return p.InviteFriendAsync(4, 2, 7);
                case "listInvites": return p.ListInvitesAsync();
                case "accept": return p.AcceptInviteAsync(8, 2);
                case "decline": return p.DeclineInviteAsync(8, 2);
                case "queue": return p.QueueAsync(4, 2);
                case "cancelQueue": return p.CancelQueueAsync(4, 2);
                default: return p.RematchAsync(4, 2, "m1");
            }
        }

        [Theory]
        [InlineData("create", "POST /v1/parties", "/v1/parties")]
        [InlineData("current", "GET /v1/parties/current", "/v1/parties/current")]
        [InlineData("get", "GET /v1/parties/{id}", "/v1/parties/4")]
        [InlineData("update", "PATCH /v1/parties/{id}", "/v1/parties/4")]
        [InlineData("disband", "DELETE /v1/parties/{id}", "/v1/parties/4")]
        [InlineData("heartbeat", "POST /v1/parties/{id}/heartbeat", "/v1/parties/4/heartbeat")]
        [InlineData("join", "POST /v1/parties/join", "/v1/parties/join")]
        [InlineData("leave", "DELETE /v1/parties/{id}/members/me", "/v1/parties/4/members/me")]
        [InlineData("ready", "PUT /v1/parties/{id}/members/me/ready", "/v1/parties/4/members/me/ready")]
        [InlineData("kick", "DELETE /v1/parties/{id}/members/{player_id}", "/v1/parties/4/members/7")]
        [InlineData("createCode", "POST /v1/parties/{id}/invite-codes", "/v1/parties/4/invite-codes")]
        [InlineData("revokeCode", "DELETE /v1/parties/{id}/invite-codes/{code_id}", "/v1/parties/4/invite-codes/6")]
        [InlineData("invite", "POST /v1/parties/{id}/invites", "/v1/parties/4/invites")]
        [InlineData("listInvites", "GET /v1/party-invites", "/v1/party-invites")]
        [InlineData("accept", "POST /v1/party-invites/{id}/accept", "/v1/party-invites/8/accept")]
        [InlineData("decline", "DELETE /v1/party-invites/{id}", "/v1/party-invites/8")]
        [InlineData("queue", "POST /v1/parties/{id}/queue", "/v1/parties/4/queue")]
        [InlineData("cancelQueue", "DELETE /v1/parties/{id}/queue", "/v1/parties/4/queue")]
        [InlineData("rematch", "POST /v1/parties/{id}/rematch", "/v1/parties/4/rematch")]
        public async Task Wrapper_sends_method_path_and_operation(string name, string operation, string path)
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await Call(client.Parties, name);

            var r = ft.LastRequest!;
            Assert.Equal(operation + " " + path + " tok", r.Operation + " " + r.Path + " " + r.SessionToken);
        }

        [Theory]
        [InlineData("update")]
        [InlineData("disband")]
        [InlineData("heartbeat")]
        [InlineData("leave")]
        [InlineData("ready")]
        [InlineData("kick")]
        [InlineData("createCode")]
        [InlineData("revokeCode")]
        [InlineData("invite")]
        [InlineData("accept")]
        [InlineData("decline")]
        [InlineData("queue")]
        [InlineData("cancelQueue")]
        [InlineData("rematch")]
        public async Task Write_sends_the_expected_version(string name)
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await Call(client.Parties, name);

            Assert.Equal(2L, ft.LastRequest!.Body!.OptLong("expected_version"));
        }

        [Theory]
        [InlineData("create", "{\"settings\":{\"mode\":\"match_only\",\"min_count\":2,\"max_count\":2}}")]
        [InlineData("update", "{\"expected_version\":2,\"settings\":{\"mode\":\"match_only\",\"min_count\":2,\"max_count\":2}}")]
        [InlineData("join", "{\"code\":\"ABCDEFGHIJKLMNOP\"}")]
        [InlineData("ready", "{\"expected_version\":2,\"ready\":true,\"properties\":{}}")]
        [InlineData("createCode", "{\"expected_version\":2}")]
        [InlineData("invite", "{\"expected_version\":2,\"player_id\":7}")]
        [InlineData("rematch", "{\"expected_version\":2,\"last_match_id\":\"m1\"}")]
        public async Task Write_sends_the_body(string name, string body)
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await Call(client.Parties, name);

            Assert.Equal(body, ft.LastRequest!.Body!.ToString());
        }

        [Fact]
        public async Task SetReadyAsync_sends_the_member_properties()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);
            var props = new PartyProperties
            {
                StringProperties = new Dictionary<string, string> { ["role"] = "tank" },
                NumericProperties = new Dictionary<string, double> { ["skill"] = 3 },
                Attributes = JsonValue.Parse("{\"name\":\"x\"}"),
            };

            await client.Parties.SetReadyAsync(4, 2, true, props);

            Assert.Equal("{\"string_properties\":{\"role\":\"tank\"},\"numeric_properties\":{\"skill\":3},\"attributes\":{\"name\":\"x\"}}",
                ft.LastRequest!.Body!.Opt("properties")!.ToString());
        }

        [Fact]
        public async Task CreateCodeAsync_sends_max_uses_when_it_is_set()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Parties.CreateCodeAsync(4, 2, 3);

            Assert.Equal(3L, ft.LastRequest!.Body!.OptLong("max_uses"));
        }

        [Theory]
        [InlineData("queue")]
        [InlineData("rematch")]
        public async Task Queue_and_rematch_make_an_idempotency_key_when_none_is_given(string name)
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await Call(client.Parties, name);

            Assert.False(string.IsNullOrEmpty(ft.LastRequest!.Headers["Idempotency-Key"]));
        }

        [Fact]
        public async Task QueueAsync_makes_a_new_key_for_each_call()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Parties.QueueAsync(4, 2);
            await client.Parties.QueueAsync(4, 2);

            Assert.NotEqual(ft.Requests[0].Headers["Idempotency-Key"], ft.Requests[1].Headers["Idempotency-Key"]);
        }

        [Fact]
        public async Task QueueAsync_sends_the_given_idempotency_key()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Parties.QueueAsync(4, 2, "my-key");

            Assert.Equal("my-key", ft.LastRequest!.Headers["Idempotency-Key"]);
        }

        [Fact]
        public async Task RematchAsync_sends_the_given_idempotency_key()
        {
            var ft = new FakeTransport();
            using var client = NewClient(ft);

            await client.Parties.RematchAsync(4, 2, "m1", "my-key");

            Assert.Equal("my-key", ft.LastRequest!.Headers["Idempotency-Key"]);
        }

        [Fact]
        public async Task QueueAsync_is_retried_after_a_503_because_it_has_a_key()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(CannedParty.Error(503, "unavailable"));
            ft.EnqueueResult(CannedParty.Party(3, PartyState.Queued));
            using var client = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft, Clock = new FakeClock() });
            client.SetSession(new Session("tok", "ref", CannedParty.Me, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

            await client.Parties.QueueAsync(4, 2);

            Assert.Equal(2, ft.CallCount);
        }

        [Fact]
        public async Task QueueAsync_throws_party_enqueue_disabled_without_a_retry()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(CannedParty.Error(503, "party_enqueue_disabled"));
            using var client = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft, Clock = new FakeClock() });
            client.SetSession(new Session("tok", "ref", CannedParty.Me, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => client.Parties.QueueAsync(4, 2));

            Assert.True(ex.IsPartyEnqueueDisabled && ft.CallCount == 1);
        }

        [Fact]
        public async Task UpdateAsync_throws_stale_version_when_the_server_answers_409()
        {
            var ft = new FakeTransport { Respond = _ => throw CannedParty.Error(409, "stale_version") };
            using var client = NewClient(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => client.Parties.UpdateAsync(4, 1, Settings));

            Assert.True(ex.IsStaleVersion);
        }

        [Fact]
        public async Task JoinByCodeAsync_throws_code_cooldown_with_retry_after()
        {
            var ft = new FakeTransport
            {
                Respond = _ => throw HttpTransport.MapError(429, "{\"detail\":\"code_redemption_cooldown\"}", TimeSpan.FromSeconds(900)),
            };
            using var client = NewClient(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => client.Parties.JoinByCodeAsync("ZZZZZZZZZZZZZZZZ"));

            Assert.True(ex.IsCodeCooldown && ex.RetryAfter == TimeSpan.FromSeconds(900));
        }

        [Fact]
        public async Task GetAsync_reads_the_party()
        {
            var ft = new FakeTransport { Respond = _ => CannedParty.Party(5, PartyState.Matched, "m1", 12) };
            using var client = NewClient(ft);

            var p = await client.Parties.GetAsync(4);

            Assert.Equal("4 1 9 matched 5 1 4 11 m1", string.Join(" ", p.Id, p.ProjectId, p.LeaderId, p.State, p.Version,
                p.RosterVersion, p.MaxMembers, p.CurrentQueueEntryId, p.LastMatchId));
        }

        [Fact]
        public async Task GetAsync_reads_the_party_settings()
        {
            var ft = new FakeTransport { Respond = _ => CannedParty.Party(5) };
            using var client = NewClient(ft);

            var s = (await client.Parties.GetAsync(4)).Settings;

            Assert.Equal("match_only 3 eu ctf 2 4 2 True q", string.Join(" ", s.Mode, s.FleetId, s.Region, s.GameMode,
                s.MinCount, s.MaxCount, s.CountMultiple, s.AllowCrossRegion, s.Query));
        }

        [Fact]
        public async Task GetAsync_reads_the_members()
        {
            var ft = new FakeTransport { Respond = _ => CannedParty.Party(5, myTicket: 12) };
            using var client = NewClient(ft);

            var p = await client.Parties.GetAsync(4);
            var m = p.Member(CannedParty.Me)!;

            Assert.Equal("12 9 True tank 3 <b>x</b> 2026-10-07T10:00:30.0000000+00:00", string.Join(" ", m.TicketId, m.PlayerId,
                m.IsReady(p), m.StringProperties["role"], m.NumericProperties["skill"], m.Attributes.OptString("name"),
                m.DisconnectDeadline.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        [Fact]
        public async Task ListInvitesAsync_reads_the_invites()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("[{\"id\":8,\"party_id\":4,\"party_version\":3,\"target_id\":9,\"expires_at\":\"2026-10-07T11:00:00Z\"}]"),
            };
            using var client = NewClient(ft);

            var invites = await client.Parties.ListInvitesAsync();

            Assert.Equal("8 4 3 9", string.Join(" ", invites[0].Id, invites[0].PartyId, invites[0].PartyVersion, invites[0].TargetId));
        }

        [Fact]
        public async Task CreateCodeAsync_reads_the_code()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse("{\"id\":6,\"code\":\"ABCDEFGHIJKLMNOP\",\"expires_at\":\"2026-10-07T11:00:00Z\",\"party_version\":3}"),
            };
            using var client = NewClient(ft);

            var code = await client.Parties.CreateCodeAsync(4, 2);

            Assert.Equal("6 ABCDEFGHIJKLMNOP 3", string.Join(" ", code.Id, code.Code, code.PartyVersion));
        }
    }

    public class PartyWatchTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        private const string TicketPath = "/v1/matchmaker/tickets/5";

        private static GGScaleClient NewClient(FakeTransport ft, TimeSpan? heartbeat = null)
        {
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", BaseUrl = "http://api.test", Transport = ft });
            c.SetSession(Canned.Live(CannedParty.Me));
            c.Parties.HeartbeatInterval = heartbeat ?? TimeSpan.FromHours(1);
            return c;
        }

        private static async Task<List<PartyEvent>> Take(IAsyncEnumerable<PartyEvent> source, int count)
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            var list = new List<PartyEvent>();
            await foreach (var ev in source.WithCancellation(cts.Token))
            {
                list.Add(ev);
                if (list.Count == count)
                {
                    break;
                }
            }
            return list;
        }

        private static string Describe(IEnumerable<PartyEvent> events)
        {
            var parts = new List<string>();
            foreach (var ev in events)
            {
                if (ev.Party != null)
                {
                    parts.Add("party:" + ev.Party.Version);
                }
                else if (ev.Match != null)
                {
                    parts.Add("match:" + ev.Match.MatchId);
                }
                else if (ev.Invite != null)
                {
                    parts.Add("invite:" + ev.Invite.InviteId);
                }
                else if (ev.Removed)
                {
                    parts.Add("removed");
                }
            }
            return string.Join(",", parts);
        }

        private static JsonValue Matched(string matchId) =>
            JsonValue.Parse("{\"id\":5,\"status\":\"matched\",\"mode\":\"match_only\",\"match_id\":\"" + matchId +
                "\",\"users\":[{\"player_id\":9,\"queue_entry_id\":11,\"party_id\":4}]}");

        [Fact]
        public async Task WatchAsync_reports_the_party_first()
        {
            var ft = new FakeTransport { Respond = _ => CannedParty.Party(1) };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();

            var events = await Take(c.Parties.WatchAsync(4, adapter), 1);

            Assert.Equal("party:1", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_reports_a_party_only_when_the_version_is_newer()
        {
            var heartbeats = 0;
            var ft = new FakeTransport
            {
                Respond = r => r.Path.EndsWith("/heartbeat", StringComparison.Ordinal)
                    ? CannedParty.Party(++heartbeats < 3 ? 1 : 2)
                    : CannedParty.Party(1),
            };
            using var c = NewClient(ft, TimeSpan.FromMilliseconds(1));
            using var adapter = new FakeSocketAdapter();

            var events = await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal("party:1,party:2", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_ignores_party_changed_when_the_version_is_not_newer()
        {
            var gets = 0;
            var ft = new FakeTransport { Respond = _ => CannedParty.Party(++gets == 1 ? 1 : 2) };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"party_changed\",\"payload\":{\"party_id\":4,\"version\":1,\"state\":\"idle\"}}");
            adapter.Push("{\"type\":\"party_changed\",\"payload\":{\"party_id\":4,\"version\":2,\"state\":\"idle\"}}");

            var events = await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal("party:1,party:2 gets=2", Describe(events) + " gets=" + gets);
        }

        [Fact]
        public async Task WatchAsync_ignores_party_changed_for_another_party()
        {
            var gets = 0;
            var ft = new FakeTransport { Respond = _ => CannedParty.Party(++gets) };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"party_changed\",\"payload\":{\"party_id\":99,\"version\":7,\"state\":\"idle\"}}");
            adapter.Push("{\"type\":\"party_changed\",\"payload\":{\"party_id\":4,\"version\":2,\"state\":\"idle\"}}");

            await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal(2, gets);
        }

        [Fact]
        public async Task WatchAsync_heartbeats_with_the_last_reported_version()
        {
            var ft = new FakeTransport { Respond = _ => CannedParty.Party(3) };
            using var c = NewClient(ft, TimeSpan.FromMilliseconds(1));
            using var adapter = new FakeSocketAdapter();
            var heartbeat = new TaskCompletionSource<GGRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            ft.Respond = r =>
            {
                if (r.Path.EndsWith("/heartbeat", StringComparison.Ordinal))
                {
                    heartbeat.TrySetResult(r);
                }
                return CannedParty.Party(3);
            };

            using var cts = new CancellationTokenSource(TestTimeout);
            var watch = Take(c.Parties.WatchAsync(4, adapter, cts.Token), 2);
            var sent = await heartbeat.Task.WaitAsync(TestTimeout);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);

            Assert.Equal(3L, sent.Body!.OptLong("expected_version"));
        }

        [Fact]
        public async Task WatchAsync_reads_the_party_again_when_the_heartbeat_is_stale()
        {
            var gets = 0;
            var ft = new FakeTransport
            {
                Respond = r => r.Path.EndsWith("/heartbeat", StringComparison.Ordinal)
                    ? throw CannedParty.Error(409, "stale_version")
                    : CannedParty.Party(++gets == 1 ? 1 : 3),
            };
            using var c = NewClient(ft, TimeSpan.FromMilliseconds(1));
            using var adapter = new FakeSocketAdapter();

            var events = await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal("party:1,party:3", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_reports_removal_and_ends_when_the_party_is_gone()
        {
            var ft = new FakeTransport { Respond = _ => throw CannedParty.Error(404, "not found") };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();

            var events = await Take(c.Parties.WatchAsync(4, adapter), 5);

            Assert.Equal("removed", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_reports_removal_when_the_heartbeat_is_not_found()
        {
            var ft = new FakeTransport
            {
                Respond = r => r.Path.EndsWith("/heartbeat", StringComparison.Ordinal)
                    ? throw CannedParty.Error(404, "not found")
                    : CannedParty.Party(1),
            };
            using var c = NewClient(ft, TimeSpan.FromMilliseconds(1));
            using var adapter = new FakeSocketAdapter();

            var events = await Take(c.Parties.WatchAsync(4, adapter), 5);

            Assert.Equal("party:1,removed", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_keeps_going_when_a_heartbeat_fails_for_a_transient_reason()
        {
            var heartbeats = 0;
            var ft = new FakeTransport
            {
                Respond = r => r.Path.EndsWith("/heartbeat", StringComparison.Ordinal)
                    ? (++heartbeats == 1 ? throw CannedParty.Error(500, "boom") : CannedParty.Party(2))
                    : CannedParty.Party(1),
            };
            using var c = NewClient(ft, TimeSpan.FromMilliseconds(1));
            using var adapter = new FakeSocketAdapter();

            var events = await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal("party:1,party:2", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_reports_the_match_from_the_ticket_when_the_party_is_matched()
        {
            var ft = new FakeTransport
            {
                Respond = r => r.Path == TicketPath ? Matched("m1") : CannedParty.Party(4, PartyState.Matched, "m1", 5),
            };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();

            var events = await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal("party:4,match:m1", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_skips_the_ticket_match_when_it_is_not_the_last_match()
        {
            var gets = 0;
            var ft = new FakeTransport
            {
                Respond = r => r.Path == TicketPath
                    ? Matched("old")
                    : CannedParty.Party(++gets, PartyState.Matched, "m1", 5),
            };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"party_changed\",\"payload\":{\"party_id\":4,\"version\":2,\"state\":\"matched\"}}");

            var events = await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal("party:1,party:2", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_reports_the_match_from_the_event_when_the_roster_has_this_party()
        {
            var ft = new FakeTransport { Respond = r => r.Path == TicketPath ? Matched("m1") : CannedParty.Party(1) };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"matchmaker_matched\",\"payload\":{\"ticket_id\":5,\"match_id\":\"m1\",\"users\":[{\"player_id\":9,\"party_id\":4}]}}");

            var events = await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal("party:1,match:m1", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_reports_the_match_once_when_the_event_and_the_party_both_show_it()
        {
            var gets = 0;
            var ft = new FakeTransport
            {
                Respond = r => r.Path == TicketPath
                    ? Matched("m1")
                    : (++gets == 1 ? CannedParty.Party(1) : CannedParty.Party(gets, PartyState.Matched, "m1", 5)),
            };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"matchmaker_matched\",\"payload\":{\"ticket_id\":5,\"match_id\":\"m1\",\"users\":[{\"player_id\":9,\"party_id\":4}]}}");
            adapter.Push("{\"type\":\"party_changed\",\"payload\":{\"party_id\":4,\"version\":2,\"state\":\"matched\"}}");
            adapter.Push("{\"type\":\"party_changed\",\"payload\":{\"party_id\":4,\"version\":3,\"state\":\"matched\"}}");

            var events = await Take(c.Parties.WatchAsync(4, adapter), 4);

            Assert.Equal("party:1,match:m1,party:2,party:3", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_ignores_a_match_for_another_party()
        {
            var gets = 0;
            var ft = new FakeTransport { Respond = r => r.Path == TicketPath ? Matched("m1") : CannedParty.Party(++gets) };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"matchmaker_matched\",\"payload\":{\"ticket_id\":5,\"match_id\":\"m1\",\"users\":[{\"player_id\":9,\"party_id\":99}]}}");
            adapter.Push("{\"type\":\"party_changed\",\"payload\":{\"party_id\":4,\"version\":2,\"state\":\"idle\"}}");

            var events = await Take(c.Parties.WatchAsync(4, adapter), 2);

            Assert.Equal("party:1,party:2", Describe(events));
        }

        [Fact]
        public async Task WatchAsync_reports_invites_without_a_party_when_the_id_is_zero()
        {
            var ft = new FakeTransport();
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"party_invite\",\"payload\":{\"invite_id\":8,\"party_id\":4,\"from_player_id\":7}}");

            var events = await Take(c.Parties.WatchAsync(0, adapter), 1);

            Assert.Equal("invite:8 calls=0", Describe(events) + " calls=" + ft.CallCount);
        }

        [Fact]
        public async Task WatchAsync_throws_the_dial_error_when_the_id_is_zero_and_the_socket_fails()
        {
            using var c = NewClient(new FakeTransport());
            using var adapter = new FakeSocketAdapter();
            adapter.FailNextConnect(new GGScaleException(GGFailureKind.Handshake, "ws_handshake_failed", "unauthorized") { Status = 401 });

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => Take(c.Parties.WatchAsync(0, adapter), 1));

            Assert.Equal(GGFailureKind.Handshake, ex.Kind);
        }

        [Fact]
        public async Task WatchAsync_continues_on_the_heartbeat_when_the_socket_fails_for_a_party()
        {
            var ft = new FakeTransport { Respond = _ => CannedParty.Party(1) };
            var logger = new RecordingLogger();
            using var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", BaseUrl = "http://api.test", Transport = ft, Logger = logger });
            c.SetSession(Canned.Live(CannedParty.Me));
            using var adapter = new FakeSocketAdapter();
            adapter.FailNextConnect(new GGScaleException(GGFailureKind.Handshake, "ws_handshake_failed", "unauthorized") { Status = 401 });

            var events = await Take(c.Parties.WatchAsync(4, adapter), 1);

            Assert.Equal("party:1", Describe(events));
        }

        [Fact]
        public async Task WaitForMatchAsync_returns_the_match_when_the_party_matches()
        {
            var ft = new FakeTransport { Respond = r => r.Path == TicketPath ? Matched("m1") : CannedParty.Party(1, PartyState.Queued, "", 5) };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"matchmaker_matched\",\"payload\":{\"ticket_id\":5,\"match_id\":\"m1\",\"users\":[{\"player_id\":9,\"party_id\":4}]}}");
            using var cts = new CancellationTokenSource(TestTimeout);

            var result = await c.Parties.WaitForMatchAsync(4, adapter, cts.Token);

            Assert.Equal("m1", result.MatchId);
        }

        [Fact]
        public async Task WaitForMatchAsync_throws_the_ticket_failure_when_the_queued_party_goes_idle()
        {
            var ft = new FakeTransport
            {
                Respond = r =>
                {
                    if (r.Path == TicketPath)
                    {
                        return JsonValue.Parse("{\"id\":5,\"status\":\"failed\",\"failure_reason\":\"expired\"}");
                    }
                    return r.Path.EndsWith("/heartbeat", StringComparison.Ordinal)
                        ? CannedParty.Party(2, PartyState.Idle)
                        : CannedParty.Party(1, PartyState.Queued, "", 5);
                },
            };
            using var c = NewClient(ft, TimeSpan.FromMilliseconds(1));
            using var adapter = new FakeSocketAdapter();
            using var cts = new CancellationTokenSource(TestTimeout);

            var ex = await Assert.ThrowsAsync<MatchFailedException>(() => c.Parties.WaitForMatchAsync(4, adapter, cts.Token));

            Assert.Equal("expired", ex.Reason);
        }

        [Fact]
        public async Task WaitForMatchAsync_throws_not_a_member_when_the_player_is_removed()
        {
            var ft = new FakeTransport { Respond = _ => throw CannedParty.Error(404, "not found") };
            using var c = NewClient(ft);
            using var adapter = new FakeSocketAdapter();
            using var cts = new CancellationTokenSource(TestTimeout);

            var ex = await Assert.ThrowsAsync<NotPartyMemberException>(() => c.Parties.WaitForMatchAsync(4, adapter, cts.Token));

            Assert.Equal(4L, ex.PartyId);
        }
    }
}
