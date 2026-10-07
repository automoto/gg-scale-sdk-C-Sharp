using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    /// <summary>The HTTP retry defaults: only requests that are safe to repeat retry.</summary>
    public class RetryDefaultsTests
    {
        private static RetryingTransport NewRetrying(FakeTransport ft) =>
            new RetryingTransport(ft, new GGRetryPolicy(), TimeSpan.FromSeconds(100), new FakeClock(), null);

        private static GGScaleException Err(int status, string detail = "staged error") =>
            new GGScaleException(status, "", detail);

        private static GGRequest Keyed(string method)
        {
            var req = new GGRequest { Method = method, Path = "/v1/x" };
            req.Headers["Idempotency-Key"] = "k1";
            return req;
        }

        [Theory]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        [InlineData("PATCH")]
        [InlineData("POST")]
        public async Task CallAsync_does_not_retry_a_write_when_it_has_no_key(string method)
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(503));
            var rt = NewRetrying(ft);

            await Assert.ThrowsAsync<GGScaleException>(
                () => rt.CallAsync(new GGRequest { Method = method, Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(1, ft.CallCount);
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("HEAD")]
        public async Task CallAsync_retries_a_read_when_the_status_is_503(string method)
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(503));
            ft.EnqueueResult(JsonValue.Null);
            var rt = NewRetrying(ft);

            await rt.CallAsync(new GGRequest { Method = method, Path = "/v1/x" }, CancellationToken.None);

            Assert.Equal(2, ft.CallCount);
        }

        [Fact]
        public async Task CallAsync_retries_a_post_when_it_has_an_idempotency_key()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(503));
            ft.EnqueueResult(JsonValue.Null);
            var rt = NewRetrying(ft);

            await rt.CallAsync(Keyed("POST"), CancellationToken.None);

            Assert.Equal(2, ft.CallCount);
        }

        [Fact]
        public async Task CallAsync_retries_a_post_when_the_key_header_name_has_other_case()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(502));
            ft.EnqueueResult(JsonValue.Null);
            var rt = NewRetrying(ft);
            var req = new GGRequest { Method = "POST", Path = "/v1/x" };
            req.Headers["idempotency-key"] = "k1";

            await rt.CallAsync(req, CancellationToken.None);

            Assert.Equal(2, ft.CallCount);
        }

        [Fact]
        public async Task CallAsync_does_not_retry_when_party_enqueue_is_disabled()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(503, "party_enqueue_disabled"));
            var rt = NewRetrying(ft);

            await Assert.ThrowsAsync<GGScaleException>(() => rt.CallAsync(Keyed("POST"), CancellationToken.None));

            Assert.Equal(1, ft.CallCount);
        }

        [Fact]
        public async Task CallAsync_does_not_retry_a_keyed_post_when_the_status_is_500()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(500));
            var rt = NewRetrying(ft);

            await Assert.ThrowsAsync<GGScaleException>(() => rt.CallAsync(Keyed("POST"), CancellationToken.None));

            Assert.Equal(1, ft.CallCount);
        }
    }

    public class RequestHeaderTests
    {
        [Fact]
        public async Task CallAsync_sends_extra_headers_when_the_request_has_them()
        {
            var handler = new StubHandler { Body = "{}" };
            using var t = new HttpTransport("http://api.test", new HttpClient(handler));
            var req = new GGRequest { Method = "POST", Path = "/v1/x" };
            req.Headers["Idempotency-Key"] = "abc";

            await t.CallAsync(req, CancellationToken.None);

            Assert.Equal("abc", string.Join(",", handler.LastRequest!.Headers.GetValues("Idempotency-Key")));
        }
    }

    public class ErrorSlugTests
    {
        private static GGScaleException Map(int status, string detail) =>
            HttpTransport.MapError(status, "{\"status\":" + status + ",\"detail\":\"" + detail + "\"}", null);

        [Fact]
        public void IsStaleVersion_is_true_when_409_has_the_slug() =>
            Assert.True(Map(409, "stale_version").IsStaleVersion);

        [Fact]
        public void IsStaleVersion_is_false_when_409_is_a_ticket_conflict() =>
            Assert.False(Map(409, "ticket_already_active").IsStaleVersion);

        [Fact]
        public void IsPartyEnqueueDisabled_is_true_when_503_has_the_slug() =>
            Assert.True(Map(503, "party_enqueue_disabled").IsPartyEnqueueDisabled);

        [Fact]
        public void IsPartyEnqueueDisabled_is_false_when_503_has_no_slug() =>
            Assert.False(Map(503, "unavailable").IsPartyEnqueueDisabled);

        [Fact]
        public void IsCodeCooldown_is_true_when_429_has_the_slug() =>
            Assert.True(Map(429, "code_redemption_cooldown").IsCodeCooldown);

        [Fact]
        public void IsCodeCooldown_is_false_when_429_is_a_rate_limit() =>
            Assert.False(Map(429, "rate limit exceeded").IsCodeCooldown);

        [Fact]
        public void MapError_reads_retry_after_when_the_cooldown_header_is_set()
        {
            var ex = HttpTransport.MapError(429, "{\"detail\":\"code_redemption_cooldown\"}", TimeSpan.FromSeconds(42));

            Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
        }

        [Fact]
        public void IsDeleteRequestedByTeam_is_true_when_403_has_the_slug() =>
            Assert.True(Map(403, "delete_requested_by_team").IsDeleteRequestedByTeam);

        [Fact]
        public void IsDeleteRequestedByTeam_is_false_when_403_is_a_revoked_key() =>
            Assert.False(Map(403, "api key revoked").IsDeleteRequestedByTeam);

        [Fact]
        public void IsDeleteRequestedByTeam_is_false_when_the_slug_comes_with_404() =>
            Assert.False(Map(404, "delete_requested_by_team").IsDeleteRequestedByTeam);
    }
}

namespace GGScale.Tests
{
    /// <summary>The realtime reconnect defaults and the dropped-connection error.</summary>
    public class ReconnectDefaultsTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        private static GGScaleClient NewClient(FakeClock clock)
        {
            var c = new GGScaleClient(new GGScaleClientOptions
            {
                ApiKey = "pk",
                BaseUrl = "http://api.test",
                Transport = new FakeTransport(),
                Clock = clock,
            });
            c.SetSession(new Session("tok-1", "rt", 9, clock.UtcNow.AddHours(1)));
            return c;
        }

        private static GGScaleException Busy() =>
            new GGScaleException(GGFailureKind.Handshake, "ws_handshake_failed", "busy") { Status = 503 };

        private static async Task<RealtimeStateChange> ClosedChange(RealtimeClient rc, Action trigger)
        {
            var closed = new TaskCompletionSource<RealtimeStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            rc.StateChanged += (_, change) =>
            {
                if (change.State == RealtimeState.Closed)
                {
                    closed.TrySetResult(change);
                }
            };
            trigger();
            return await closed.Task.WaitAsync(TestTimeout);
        }

        [Fact]
        public void RealtimeOptions_limits_reconnect_to_five_attempts() =>
            Assert.Equal(5, new RealtimeOptions().MaxReconnectAttempts);

        [Fact]
        public void RealtimeOptions_has_reconnect_on() =>
            Assert.True(new RealtimeOptions().AutoReconnect);

        [Fact]
        public async Task Reconnect_stops_after_the_attempt_limit()
        {
            using var adapter = new FakeSocketAdapter();
            using var c = NewClient(new FakeClock());
            var rc = await c.DialRealtimeAsync(adapter);
            for (var i = 0; i < 6; i++)
            {
                adapter.FailNextConnect(Busy());
            }

            await ClosedChange(rc, () => adapter.PushClose(null));

            Assert.Equal(5, adapter.Events.FindAll(e => e == "connect-failed").Count);
        }

        [Fact]
        public async Task Reconnect_gives_a_connection_closed_error_when_the_attempts_run_out()
        {
            using var adapter = new FakeSocketAdapter();
            using var c = NewClient(new FakeClock());
            var rc = await c.DialRealtimeAsync(adapter);
            for (var i = 0; i < 5; i++)
            {
                adapter.FailNextConnect(Busy());
            }

            var change = await ClosedChange(rc, () => adapter.PushClose(null));

            Assert.Equal(GGFailureKind.ConnectionClosed, ((GGScaleException)change.Error!).Kind);
        }

        [Fact]
        public async Task Terminal_close_gives_a_connection_closed_error_with_the_close_code()
        {
            using var adapter = new FakeSocketAdapter();
            using var c = NewClient(new FakeClock());
            var rc = await c.DialRealtimeAsync(adapter);

            var change = await ClosedChange(rc, () => adapter.PushClose(4001, "policy"));

            var ex = (GGScaleException)change.Error!;
            Assert.Equal("ConnectionClosed 4001", ex.Kind + " " + ex.CloseCode);
        }

        [Fact]
        public async Task Closed_by_the_caller_has_no_error()
        {
            using var adapter = new FakeSocketAdapter();
            using var c = NewClient(new FakeClock());
            var rc = await c.DialRealtimeAsync(adapter);

            var change = await ClosedChange(rc, () => _ = rc.CloseAsync());

            Assert.Null(change.Error);
        }

        [Fact]
        public async Task A_failed_first_dial_throws_a_handshake_error()
        {
            using var adapter = new FakeSocketAdapter();
            using var c = NewClient(new FakeClock());
            adapter.FailNextConnect(Busy());

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => c.DialRealtimeAsync(adapter));

            Assert.Equal(GGFailureKind.Handshake, ex.Kind);
        }
    }
}

namespace GGScale.Tests
{
    /// <summary>A failure before the request was sent is retried for any method.</summary>
    public class NotSentRetryTests
    {
        private sealed class ThrowingHandler : HttpMessageHandler
        {
            private readonly Func<Exception> _error;

            public ThrowingHandler(Func<Exception> error) => _error = error;

            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                return Calls == 1
                    ? Task.FromException<HttpResponseMessage>(_error())
                    : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }
        }

        private static async Task<int> PostCalls(Func<Exception> error)
        {
            var handler = new ThrowingHandler(error);
            using var http = new HttpTransport("http://api.test", new HttpClient(handler));
            var rt = new RetryingTransport(http, new GGRetryPolicy(), TimeSpan.FromSeconds(100), new FakeClock(), null);
            try
            {
                await rt.CallAsync(new GGRequest { Method = "POST", Path = "/v1/x" }, CancellationToken.None);
            }
            catch (GGScaleException)
            {
            }
            return handler.Calls;
        }

        private static HttpRequestException Socket(System.Net.Sockets.SocketError error) =>
            new HttpRequestException("connect failed", new System.Net.Sockets.SocketException((int)error));

        [Fact]
        public async Task Post_is_retried_when_the_connection_is_refused() =>
            Assert.Equal(2, await PostCalls(() => Socket(System.Net.Sockets.SocketError.ConnectionRefused)));

        [Fact]
        public async Task Post_is_retried_when_name_resolution_fails_for_now() =>
            Assert.Equal(2, await PostCalls(() => Socket(System.Net.Sockets.SocketError.TryAgain)));

        // A retry cannot make a host exist, so a permanent DNS failure is not
        // retried for any method, the same as the Go SDK.
        [Fact]
        public async Task Post_is_not_retried_when_the_host_does_not_exist() =>
            Assert.Equal(1, await PostCalls(() => Socket(System.Net.Sockets.SocketError.HostNotFound)));

        [Fact]
        public void Host_not_found_failure_is_not_retryable() =>
            Assert.False(HttpTransport.ConnectionFailure(Socket(System.Net.Sockets.SocketError.HostNotFound)).IsRetryable);

        [Fact]
        public async Task Post_is_retried_when_there_is_no_route_to_the_host() =>
            Assert.Equal(2, await PostCalls(() => Socket(System.Net.Sockets.SocketError.HostUnreachable)));

        [Fact]
        public async Task Post_is_not_retried_when_the_connection_resets_after_it_opened() =>
            Assert.Equal(1, await PostCalls(() => new HttpRequestException("reset",
                new System.IO.IOException("reset", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)))));

        [Fact]
        public async Task Post_is_not_retried_when_the_failure_is_a_certificate_error() =>
            Assert.Equal(1, await PostCalls(() => new HttpRequestException("tls",
                new System.Security.Authentication.AuthenticationException("bad cert"))));

        [Fact]
        public void ConnectionFailure_marks_a_refused_connect_as_not_sent() =>
            Assert.Equal(GGScaleException.NotSentCode,
                HttpTransport.ConnectionFailure(Socket(System.Net.Sockets.SocketError.ConnectionRefused)).Code);
    }
}
