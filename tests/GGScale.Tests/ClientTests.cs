using System;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    public class ClientConstructionTests
    {
        [Fact]
        public void NewClient_requires_api_key()
        {
            Assert.Throws<ArgumentException>(() => new GGScaleClient(new GGScaleClientOptions { BaseUrl = "http://x" }));
        }

        [Fact]
        public void NewClient_requires_base_url_or_transport()
        {
            Assert.Throws<ArgumentException>(() => new GGScaleClient(new GGScaleClientOptions { ApiKey = "k" }));
        }

        [Fact]
        public void NewClient_with_transport_only_is_valid()
        {
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", Transport = new FakeTransport() });
            Assert.NotNull(c.Auth);
            Assert.NotNull(c.Storage);
            Assert.NotNull(c.Server);
        }
    }

    public class ClientSessionTests
    {
        private static GGScaleClient NewClient(FakeTransport ft, Action<Session?>? onUpdate = null) =>
            new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", Transport = ft, OnSessionUpdate = onUpdate });

        [Fact]
        public async Task Login_installs_session_and_notifies()
        {
            var ft = new FakeTransport { Respond = _ => Canned.Session() };
            Session? notified = null;
            var c = NewClient(ft, s => notified = s);

            await c.LoginAsync(new EmailPasswordAuth(ft, "k", "e@example.com", "pw"), CancellationToken.None);

            Assert.NotNull(c.Session);
            Assert.Equal(42L, c.Session!.PlayerId);
            Assert.Equal(42L, notified!.PlayerId);
        }

        [Fact]
        public async Task CallProtected_attaches_key_and_token()
        {
            var ft = new FakeTransport();
            var c = NewClient(ft);
            c.SetSession(Canned.Live());

            await c.CallProtectedAsync(new GGRequest { Method = "GET", Path = "/v1/test" }, CancellationToken.None);

            Assert.Equal("k", ft.LastRequest!.ApiKey);
            Assert.Equal("live-jwt", ft.LastRequest.SessionToken);
        }

        [Fact]
        public async Task CallProtected_throws_without_session()
        {
            var c = NewClient(new FakeTransport());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => c.CallProtectedAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));
        }

        [Fact]
        public async Task CallProtected_refreshes_proactively_when_near_expiry()
        {
            var ft = new FakeTransport();
            ft.Respond = req => req.Path == "/v1/auth/refresh" ? Canned.Session(access: "fresh") : JsonValue.Null;
            var c = NewClient(ft);
            c.SetSession(new Session("stale", "rt", 42, DateTimeOffset.UtcNow.AddSeconds(10)));

            await c.CallProtectedAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);

            Assert.Equal(1, ft.CountForPath("/v1/auth/refresh"));
            Assert.Equal("fresh", c.Session!.AccessToken);
            Assert.Equal("fresh", ft.LastRequest!.SessionToken);
        }

        [Fact]
        public async Task Concurrent_calls_refresh_once_per_expiry_boundary()
        {
            var ft = new FakeTransport();
            ft.Respond = req => req.Path == "/v1/auth/refresh" ? Canned.Session(access: "fresh") : JsonValue.Null;
            var c = NewClient(ft);
            c.SetSession(new Session("stale", "rt", 42, DateTimeOffset.UtcNow.AddSeconds(5)));

            var tasks = new Task[8];
            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = c.CallProtectedAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);
            }
            await Task.WhenAll(tasks);

            Assert.Equal(1, ft.CountForPath("/v1/auth/refresh"));
        }

        [Fact]
        public async Task CallProtected_retries_once_after_401_refresh()
        {
            var ft = new FakeTransport();
            var protectedCalls = 0;
            ft.Respond = req =>
            {
                if (req.Path == "/v1/auth/refresh")
                {
                    return Canned.Session(access: "fresh");
                }
                protectedCalls++;
                if (protectedCalls == 1)
                {
                    throw new GGScaleException(401, "", "unauthorized");
                }
                return JsonValue.NewObject().Set("ok", JsonValue.True);
            };
            var c = NewClient(ft);
            c.SetSession(Canned.Live());

            var got = await c.CallProtectedAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);

            Assert.True(got.OptBool("ok"));
            Assert.Equal(2, protectedCalls);
            Assert.Equal(1, ft.CountForPath("/v1/auth/refresh"));
        }

        [Fact]
        public async Task CallProtected_surfaces_original_401_when_refresh_fails()
        {
            var ft = new FakeTransport();
            ft.Respond = req =>
            {
                if (req.Path == "/v1/auth/refresh")
                {
                    throw new GGScaleException(500, "", "boom");
                }
                throw new GGScaleException(401, "", "unauthorized");
            };
            var c = NewClient(ft);
            c.SetSession(Canned.Live());

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => c.CallProtectedAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.True(ex.IsUnauthorized);
        }

        [Fact]
        public async Task CallProtected_surfaces_401_when_no_refresh_token()
        {
            var ft = new FakeTransport();
            ft.Respond = _ => throw new GGScaleException(401, "", "unauthorized");
            var c = NewClient(ft);
            c.SetSession(new Session("tok", "", 9, DateTimeOffset.UtcNow.AddMinutes(10)));

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => c.CallProtectedAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.True(ex.IsUnauthorized);
        }

        [Fact]
        public void SetSession_null_clears_and_notifies()
        {
            var notifications = 0;
            Session? last = Canned.Live();
            var c = NewClient(new FakeTransport(), s =>
            {
                notifications++;
                last = s;
            });

            c.SetSession(Canned.Live());
            c.SetSession(null);

            Assert.Null(c.Session);
            Assert.Equal(2, notifications);
            Assert.Null(last);
        }
    }

    public class FileSessionStoreTests
    {
        [Fact]
        public void Save_then_Load_round_trips()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggscale-test-" + Guid.NewGuid().ToString("N"), "session.json");
            var store = new FileSessionStore(path);
            var sess = new Session("at", "rt", 99, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

            store.Save(sess);
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal("at", loaded!.AccessToken);
            Assert.Equal("rt", loaded.RefreshToken);
            Assert.Equal(99L, loaded.PlayerId);
            Assert.Equal(sess.ExpiresAt, loaded.ExpiresAt);
            System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path)!, recursive: true);
        }

        [Fact]
        public void Load_returns_null_for_missing_file()
        {
            var store = new FileSessionStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")));
            Assert.Null(store.Load());
        }

        [Fact]
        public void Load_returns_null_for_corrupt_file()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggscale-test-" + Guid.NewGuid().ToString("N") + ".json");
            System.IO.File.WriteAllText(path, "not json");
            var store = new FileSessionStore(path);
            Assert.Null(store.Load());
            System.IO.File.Delete(path);
        }

        [Fact]
        public void Load_returns_null_without_refresh_token()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggscale-test-" + Guid.NewGuid().ToString("N") + ".json");
            var store = new FileSessionStore(path);
            store.Save(new Session("at", "", 1, DateTimeOffset.UtcNow));
            Assert.Null(store.Load());
            System.IO.File.Delete(path);
        }
    }
}
