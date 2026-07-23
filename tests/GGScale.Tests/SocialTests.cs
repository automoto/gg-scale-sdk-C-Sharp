using System;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    public class FriendsServiceTests
    {
        [Fact]
        public async Task List_decodes_page_with_presence()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("items", JsonValue.NewArray()
                        .Add(JsonValue.NewObject()
                            .Set("id", JsonValue.Of(1L))
                            .Set("account_id", JsonValue.Of("9a1e3f60-0000-0000-0000-000000000001"))
                            .Set("player_id", JsonValue.Of(11L))
                            .Set("status", JsonValue.Of("accepted"))
                            .Set("email", JsonValue.Of("friend@example.com"))
                            .Set("presence", JsonValue.NewObject()
                                .Set("status", JsonValue.Of("online"))
                                .Set("session_id", JsonValue.Of("gs_abc")))
                            .Set("created_at", JsonValue.Of("2026-07-01T10:00:00Z"))
                            .Set("updated_at", JsonValue.Of("2026-07-02T10:00:00Z")))
                        .Add(JsonValue.NewObject()
                            .Set("id", JsonValue.Of(2L))
                            .Set("account_id", JsonValue.Of("9a1e3f60-0000-0000-0000-000000000002"))
                            .Set("status", JsonValue.Of("accepted"))
                            .Set("created_at", JsonValue.Of("2026-07-01T10:00:00Z"))
                            .Set("updated_at", JsonValue.Of("2026-07-01T10:00:00Z"))))
                    .Set("next_cursor", JsonValue.Of("2")),
            };
            var c = TestClients.WithSession(ft);

            var page = await c.Friends.ListAsync(new FriendsListOptions { Status = "accepted", Limit = 2 });

            Assert.Equal("/v1/friends", ft.LastRequest!.Path);
            Assert.Equal("accepted", ft.LastRequest.QueryValue("status"));
            Assert.Equal("2", ft.LastRequest.QueryValue("limit"));
            Assert.Equal(2, page.Items.Count);
            Assert.Equal(11L, page.Items[0].PlayerId);
            Assert.Equal("online", page.Items[0].Presence!.Status);
            Assert.Null(page.Items[1].PlayerId);
            Assert.Null(page.Items[1].Presence);
            Assert.Equal("2", page.NextCursor);
        }

        [Fact]
        public async Task List_omits_empty_status()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject().Set("items", JsonValue.NewArray()).Set("next_cursor", JsonValue.Of("")),
            };
            var c = TestClients.WithSession(ft);

            await c.Friends.ListAsync();

            Assert.Null(ft.LastRequest!.QueryValue("status"));
        }

        [Fact]
        public async Task Request_returns_edge_status()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.NewObject().Set("status", JsonValue.Of("pending")) };
            var c = TestClients.WithSession(ft);

            var status = await c.Friends.RequestAsync(42);

            Assert.Equal("POST", ft.LastRequest!.Method);
            Assert.Equal("/v1/friends/42/request", ft.LastRequest.Path);
            Assert.Null(ft.LastRequest.Body);
            Assert.Equal("pending", status);
        }

        [Theory]
        [InlineData("accept")]
        [InlineData("reject")]
        [InlineData("block")]
        [InlineData("unblock")]
        public async Task Actions_post_to_expected_paths(string action)
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.NewObject().Set("status", JsonValue.Of("x")) };
            var c = TestClients.WithSession(ft);

            var task = action switch
            {
                "accept" => c.Friends.AcceptAsync(42),
                "reject" => c.Friends.RejectAsync(42),
                "block" => c.Friends.BlockAsync(42),
                _ => c.Friends.UnblockAsync(42),
            };
            await task;

            Assert.Equal("/v1/friends/42/" + action, ft.LastRequest!.Path);
        }

        [Fact]
        public async Task Remove_deletes_edge()
        {
            var ft = new FakeTransport();
            var c = TestClients.WithSession(ft);

            await c.Friends.RemoveAsync(42);

            Assert.Equal("DELETE", ft.LastRequest!.Method);
            Assert.Equal("/v1/friends/42", ft.LastRequest.Path);
        }

        [Fact]
        public async Task Accept_conflict_maps_to_IsConflict()
        {
            var ft = new FakeTransport { Respond = _ => throw new GGScaleException(409, "", "illegal transition") };
            var c = TestClients.WithSession(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => c.Friends.AcceptAsync(42));

            Assert.True(ex.IsConflict);
        }

        [Fact]
        public async Task RemoteAddrs_reads_friend_addresses()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject().Set("addresses", JsonValue.NewArray()
                    .Add(JsonValue.NewObject()
                        .Set("type", JsonValue.Of("ip_lan"))
                        .Set("scope", JsonValue.Of("lan"))
                        .Set("address", JsonValue.Of("192.168.1.20")))),
            };
            var c = TestClients.WithSession(ft);

            var addrs = await c.Friends.RemoteAddrsAsync(42);

            Assert.Equal("/v1/friends/42/remote-addrs", ft.LastRequest!.Path);
            Assert.Single(addrs);
            Assert.Equal("ip_lan", addrs[0].Type);
        }
    }

    public class PresenceServiceTests
    {
        [Fact]
        public async Task Set_puts_status_with_null_session()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.NewObject().Set("ok", JsonValue.True) };
            var c = TestClients.WithSession(ft);

            await c.Presence.SetAsync("online");

            Assert.Equal("PUT", ft.LastRequest!.Method);
            Assert.Equal("/v1/presence", ft.LastRequest.Path);
            Assert.Equal("online", ft.LastRequest.Body!.OptString("status"));
            Assert.Equal(JsonKind.Null, ft.LastRequest.Body!["session_id"].Kind);
        }

        [Fact]
        public async Task Set_includes_session_id()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.NewObject().Set("ok", JsonValue.True) };
            var c = TestClients.WithSession(ft);

            await c.Presence.SetAsync("in_match", "gs_abc");

            Assert.Equal("gs_abc", ft.LastRequest!.Body!.OptString("session_id"));
        }
    }

    public class InvitesServiceTests
    {
        [Fact]
        public async Task Create_posts_email_and_session_and_returns_id()
        {
            var ft = new FakeTransport { Respond = _ => JsonValue.NewObject().Set("invite_id", JsonValue.Of(31337L)) };
            var c = TestClients.WithSession(ft);

            var id = await c.Invites.CreateAsync("gs_abc", "friend@example.com");

            Assert.Equal("/v1/invite", ft.LastRequest!.Path);
            Assert.Equal("friend@example.com", ft.LastRequest.Body!.OptString("to_email"));
            Assert.Equal("gs_abc", ft.LastRequest.Body!.OptString("session_id"));
            Assert.Equal(31337L, id);
        }

        [Fact]
        public async Task List_decodes_invites()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject().Set("invites", JsonValue.NewArray()
                    .Add(JsonValue.NewObject()
                        .Set("invite_id", JsonValue.Of(1L))
                        .Set("from_email", JsonValue.Of("host@example.com"))
                        .Set("session_id", JsonValue.Of("gs_abc"))
                        .Set("join_code", JsonValue.Of("XKCD42"))
                        .Set("expires_at", JsonValue.Of("2026-07-06T12:05:00Z")))),
            };
            var c = TestClients.WithSession(ft);

            var invites = await c.Invites.ListAsync();

            Assert.Single(invites);
            Assert.Equal("XKCD42", invites[0].JoinCode);
            Assert.Equal(new DateTimeOffset(2026, 7, 6, 12, 5, 0, TimeSpan.Zero), invites[0].ExpiresAt);
        }

        [Fact]
        public async Task Delete_targets_invite_id()
        {
            var ft = new FakeTransport();
            var c = TestClients.WithSession(ft);

            await c.Invites.DeleteAsync(31337);

            Assert.Equal("DELETE", ft.LastRequest!.Method);
            Assert.Equal("/v1/invite/31337", ft.LastRequest.Path);
        }
    }

    public class AccountServiceTests
    {
        [Fact]
        public async Task SetRemoteAddrs_puts_and_returns_canonical_list()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject().Set("addresses", JsonValue.NewArray()
                    .Add(JsonValue.NewObject()
                        .Set("type", JsonValue.Of("ip_public"))
                        .Set("scope", JsonValue.Of("public"))
                        .Set("address", JsonValue.Of("203.0.113.9")))),
            };
            var c = TestClients.WithSession(ft);

            var addrs = await c.Account.SetRemoteAddrsAsync(new[] { new RemoteAddr("ip_public", "203.0.113.9") });

            Assert.Equal("PUT", ft.LastRequest!.Method);
            Assert.Equal("/v1/account/remote-addrs", ft.LastRequest.Path);
            var sent = ft.LastRequest.Body!["addresses"];
            Assert.Equal(1, sent.Count);
            Assert.Equal("ip_public", sent[0].OptString("type"));
            Assert.Null(sent[0].Opt("scope"));
            Assert.Equal("public", addrs[0].Scope);
        }

        [Fact]
        public async Task RemoteAddrs_forbidden_for_anonymous_maps()
        {
            var ft = new FakeTransport { Respond = _ => throw new GGScaleException(403, "", "link a gg-scale account to use friends") };
            var c = TestClients.WithSession(ft);

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => c.Account.RemoteAddrsAsync());

            Assert.True(ex.IsForbidden);
        }
    }
}
