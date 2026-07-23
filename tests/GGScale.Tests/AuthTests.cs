using System;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    internal static class Canned
    {
        public static JsonValue Session(long playerId = 42, string access = "jwt.access", string refresh = "refresh-hex") =>
            JsonValue.NewObject()
                .Set("access_token", JsonValue.Of(access))
                .Set("refresh_token", JsonValue.Of(refresh))
                .Set("player_id", JsonValue.Of(playerId))
                .Set("expires_at", JsonValue.Of("2030-01-01T00:00:00Z"));

        public static Session Live(long playerId = 9) =>
            new Session("live-jwt", "rt", playerId, DateTimeOffset.UtcNow.AddMinutes(10));
    }

    public class EmailPasswordAuthTests
    {
        [Fact]
        public async Task Authenticate_posts_credentials_to_login()
        {
            var ft = new FakeTransport { Respond = _ => Canned.Session() };
            var auth = new EmailPasswordAuth(ft, "ggs_key", "demo@example.com", "hunter2hunter2");

            var sess = await auth.AuthenticateAsync(CancellationToken.None);

            Assert.Equal("POST", ft.LastRequest!.Method);
            Assert.Equal("/v1/auth/login", ft.LastRequest.Path);
            Assert.Equal("ggs_key", ft.LastRequest.ApiKey);
            Assert.Null(ft.LastRequest.SessionToken);
            Assert.Equal("demo@example.com", ft.LastRequest.Body!.OptString("email"));
            Assert.Equal("hunter2hunter2", ft.LastRequest.Body!.OptString("password"));
            Assert.Equal(42L, sess.PlayerId);
            Assert.Equal("jwt.access", sess.AccessToken);
        }
    }

    public class CustomTokenAuthTests
    {
        [Fact]
        public async Task Authenticate_posts_signed_token()
        {
            var ft = new FakeTransport { Respond = _ => Canned.Session() };
            var auth = new CustomTokenAuth(ft, "k", "tenant-signed-jwt");

            var sess = await auth.AuthenticateAsync(CancellationToken.None);

            Assert.Equal("/v1/auth/custom-token", ft.LastRequest!.Path);
            Assert.Equal("tenant-signed-jwt", ft.LastRequest.Body!.OptString("token"));
            Assert.Equal(42L, sess.PlayerId);
        }
    }

    public class OfflineAuthTests
    {
        [Fact]
        public async Task Authenticate_returns_stable_synthetic_session()
        {
            var auth = new OfflineAuth();

            var s1 = await auth.AuthenticateAsync(CancellationToken.None);
            var s2 = await auth.AuthenticateAsync(CancellationToken.None);

            Assert.True(s1.PlayerId > 0);
            Assert.NotEmpty(s1.AccessToken);
            Assert.Empty(s1.RefreshToken);
            Assert.True(s1.ExpiresAt > DateTimeOffset.UtcNow.AddYears(50));
            Assert.Equal(s1.PlayerId, s2.PlayerId);
            Assert.Equal(s1.AccessToken, s2.AccessToken);
        }

        [Fact]
        public async Task Two_instances_get_distinct_identities()
        {
            var a = await new OfflineAuth().AuthenticateAsync(CancellationToken.None);
            var b = await new OfflineAuth().AuthenticateAsync(CancellationToken.None);
            Assert.NotEqual(a.PlayerId, b.PlayerId);
        }
    }

    public class AnonymousAuthTests
    {
        [Fact]
        public async Task Authenticate_posts_to_anonymous_and_saves()
        {
            var ft = new FakeTransport { Respond = _ => Canned.Session() };
            var store = new MemorySessionStore();
            var auth = new AnonymousAuth(ft, "k", store);

            var sess = await auth.AuthenticateAsync(CancellationToken.None);

            Assert.Equal("/v1/auth/anonymous", ft.LastRequest!.Path);
            Assert.Null(ft.LastRequest.Body);
            Assert.Equal(42L, sess.PlayerId);
            Assert.NotNull(store.Stored);
        }

        [Fact]
        public async Task Authenticate_prefers_persisted_session()
        {
            var ft = new FakeTransport { Respond = _ => Canned.Session() };
            var store = new MemorySessionStore { Stored = Canned.Live(77) };
            var auth = new AnonymousAuth(ft, "k", store);

            var sess = await auth.AuthenticateAsync(CancellationToken.None);

            Assert.Equal(77L, sess.PlayerId);
            Assert.Equal(0, ft.CallCount);
        }

        private sealed class MemorySessionStore : ISessionStore
        {
            public Session? Stored { get; set; }

            public Session? Load() => Stored;

            public void Save(Session session) => Stored = session;
        }
    }

    public class AuthServiceTests
    {
        [Fact]
        public async Task Signup_posts_email_and_password()
        {
            var ft = new FakeTransport();
            var svc = new AuthService(ft, "k");

            await svc.SignupAsync("demo@example.com", "hunter2hunter2", CancellationToken.None);

            Assert.Equal("/v1/auth/signup", ft.LastRequest!.Path);
            Assert.Equal("demo@example.com", ft.LastRequest.Body!.OptString("email"));
        }

        [Fact]
        public async Task Verify_sends_email_and_code()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.NewObject()
                    .Set("player_id", JsonValue.Of(7L))
                    .Set("verified", JsonValue.True),
            };
            var svc = new AuthService(ft, "k");

            var res = await svc.VerifyAsync("demo@example.com", "123456", CancellationToken.None);

            Assert.Equal("/v1/auth/verify", ft.LastRequest!.Path);
            Assert.Equal("123456", ft.LastRequest.Body!.OptString("code"));
            Assert.Equal(7L, res.PlayerId);
            Assert.True(res.Verified);
        }

        [Fact]
        public async Task Refresh_rotates_session()
        {
            var ft = new FakeTransport { Respond = _ => Canned.Session(access: "new.jwt", refresh: "rotated") };
            var svc = new AuthService(ft, "k");

            var sess = await svc.RefreshAsync("old-refresh", CancellationToken.None);

            Assert.Equal("/v1/auth/refresh", ft.LastRequest!.Path);
            Assert.Equal("old-refresh", ft.LastRequest.Body!.OptString("refresh_token"));
            Assert.Equal("new.jwt", sess.AccessToken);
            Assert.Equal("rotated", sess.RefreshToken);
        }

        [Fact]
        public async Task Logout_posts_refresh_token()
        {
            var ft = new FakeTransport();
            var svc = new AuthService(ft, "k");

            await svc.LogoutAsync("refresh", CancellationToken.None);

            Assert.Equal("/v1/auth/logout", ft.LastRequest!.Path);
            Assert.Equal("refresh", ft.LastRequest.Body!.OptString("refresh_token"));
        }
    }
}
