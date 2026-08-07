using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    /// <summary>Handler returning a canned response and capturing the request.</summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string? Body { get; set; }

        public Dictionary<string, string> ResponseHeaders { get; } = new Dictionary<string, string>();

        /// <summary>When set, SendAsync throws this instead of responding.</summary>
        public Exception? Throw { get; set; }

        /// <summary>Delay before responding (for timeout tests).</summary>
        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (Throw != null)
            {
                throw Throw;
            }
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }
            var resp = new HttpResponseMessage(Status);
            if (Body != null)
            {
                resp.Content = new StringContent(Body, Encoding.UTF8, "application/json");
            }
            foreach (var kv in ResponseHeaders)
            {
                resp.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            return resp;
        }
    }

    public class HttpTransportTests
    {
        private static (HttpTransport, StubHandler) NewTransport(HttpTransportOptions? options = null, IGGClock? clock = null)
        {
            var handler = new StubHandler();
            return (new HttpTransport("http://api.test", new HttpClient(handler), options, clock), handler);
        }

        [Fact]
        public async Task CallAsync_sets_method_url_and_auth_headers()
        {
            var (t, h) = NewTransport();
            h.Body = "{}";

            var req = new GGRequest { Method = "POST", Path = "/v1/x", ApiKey = "key", SessionToken = "tok", IfMatch = "3" };
            req.AddQuery("limit", "10");
            req.AddQuery("cursor", "a b");
            await t.CallAsync(req, CancellationToken.None);

            Assert.Equal(HttpMethod.Post, h.LastRequest!.Method);
            Assert.Equal("http://api.test/v1/x?limit=10&cursor=a%20b", h.LastRequest.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer key", h.LastRequest.Headers.Authorization!.ToString());
            Assert.Equal("tok", string.Join(",", h.LastRequest.Headers.GetValues("X-Session-Token")));
            Assert.Equal("3", string.Join(",", h.LastRequest.Headers.GetValues("If-Match")));
        }

        [Fact]
        public async Task CallAsync_serializes_json_body()
        {
            var (t, h) = NewTransport();
            h.Body = "{}";

            var req = new GGRequest
            {
                Method = "POST",
                Path = "/v1/x",
                Body = JsonValue.NewObject().Set("a", JsonValue.Of(1L)),
            };
            await t.CallAsync(req, CancellationToken.None);

            Assert.Equal("{\"a\":1}", h.LastRequestBody);
            Assert.Equal("application/json", h.LastRequest!.Content!.Headers.ContentType!.MediaType);
        }

        [Fact]
        public async Task CallAsync_parses_2xx_json_response()
        {
            var (t, h) = NewTransport();
            h.Body = "{\"v\":7}";

            var got = await t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);

            Assert.Equal(7L, got.Value.OptLong("v"));
        }

        [Fact]
        public async Task CallAsync_returns_null_value_for_empty_204()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.NoContent;

            var got = await t.CallAsync(new GGRequest { Method = "DELETE", Path = "/v1/x" }, CancellationToken.None);

            Assert.Equal(204, got.Status);
            Assert.Equal(JsonKind.Null, got.Value.Kind);
        }

        [Fact]
        public async Task CallAsync_maps_plain_text_error()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.NotFound;
            h.Body = "not found\n";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(404, ex.Status);
            Assert.True(ex.IsNotFound);
            Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CallAsync_maps_json_error_body_with_code_and_retry()
        {
            var (t, h) = NewTransport();
            h.Status = (HttpStatusCode)429;
            h.Body = "{\"error\":\"rate_limit_exceeded\",\"retry_after_seconds\":9}";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "POST", Path = "/v1/x" }, CancellationToken.None));

            Assert.True(ex.IsRateLimited);
            Assert.Equal("rate_limit_exceeded", ex.Code);
            Assert.Equal(TimeSpan.FromSeconds(9), ex.RetryAfter);
        }

        [Fact]
        public async Task CallAsync_maps_problem_details_detail_and_errors()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.Conflict;
            h.Body = "{\"title\":\"Conflict\",\"status\":409,\"detail\":\"ticket_already_active\",\"errors\":[{\"message\":\"already queued\",\"location\":\"active_ticket_id\",\"value\":55}]}";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "POST", Path = "/v1/matchmaker/tickets" }, CancellationToken.None));

            Assert.Equal(409, ex.Status);
            Assert.Equal("ticket_already_active", ex.Detail);
            Assert.True(ex.IsTicketAlreadyActive);
            Assert.Equal(55L, ex.ActiveTicketId);
        }

        [Fact]
        public async Task CallAsync_retry_after_header_wins()
        {
            var (t, h) = NewTransport();
            h.Status = (HttpStatusCode)429;
            h.ResponseHeaders["Retry-After"] = "31";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(TimeSpan.FromSeconds(31), ex.RetryAfter);
        }

        [Fact]
        public async Task CallAsync_maps_conflict_version_on_412()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.PreconditionFailed;
            h.Body = "{\"error\":\"version_conflict\",\"current_version\":5}";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "PUT", Path = "/v1/x" }, CancellationToken.None));

            Assert.True(ex.IsConflict);
            Assert.Equal(5L, ex.ConflictVersion);
        }

        [Fact]
        public async Task CallAsync_honors_cancellation()
        {
            var (t, _) = NewTransport();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, cts.Token));
        }

        [Fact]
        public async Task CallAsync_maps_problem_type_title_instance_and_request_id()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.UnprocessableEntity;
            h.Body = "{\"type\":\"https://ggscale.dev/errors/validation\",\"title\":\"Unprocessable Entity\",\"detail\":\"score is required\",\"instance\":\"/v1/leaderboards/1/scores\"}";
            h.ResponseHeaders["X-Request-Id"] = "req-abc";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "POST", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(GGFailureKind.HttpError, ex.Kind);
            Assert.Equal("https://ggscale.dev/errors/validation", ex.ProblemType);
            Assert.Equal("Unprocessable Entity", ex.Title);
            Assert.Equal("/v1/leaderboards/1/scores", ex.Instance);
            Assert.Equal("req-abc", ex.RequestId);
        }

        [Fact]
        public async Task CallAsync_keeps_bounded_raw_body_on_unparseable_error()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.BadGateway;
            h.Body = "<html>" + new string('x', 4000) + "</html>";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.NotNull(ex.RawBody);
            Assert.StartsWith("<html>", ex.RawBody, StringComparison.Ordinal);
            Assert.Equal(2048, ex.RawBody!.Length);
        }

        [Fact]
        public async Task CallAsync_wraps_connect_failure_as_connection_kind()
        {
            var (t, h) = NewTransport();
            var cause = new HttpRequestException("connection refused");
            h.Throw = cause;

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(GGFailureKind.Connection, ex.Kind);
            Assert.Equal(0, ex.Status);
            Assert.Same(cause, ex.InnerException);
        }

        [Fact]
        public async Task CallAsync_classifies_certificate_failure_as_non_retryable()
        {
            var (t, h) = NewTransport();
            h.Throw = new HttpRequestException(
                "The SSL connection could not be established",
                new System.Security.Authentication.AuthenticationException("certificate rejected"));

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(GGFailureKind.Connection, ex.Kind);
            Assert.Equal("certificate_error", ex.Code);
            Assert.False(ex.IsRetryable);
        }

        [Fact]
        public async Task CallAsync_classifies_attempt_timeout()
        {
            var (t, h) = NewTransport(new HttpTransportOptions { Timeout = TimeSpan.FromMilliseconds(50) });
            h.Delay = TimeSpan.FromSeconds(5);

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(GGFailureKind.Timeout, ex.Kind);
        }

        [Fact]
        public async Task CallAsync_throws_decode_with_raw_body_on_malformed_success_json()
        {
            var (t, h) = NewTransport();
            h.Body = "not json";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(GGFailureKind.Decode, ex.Kind);
            Assert.Equal("not json", ex.RawBody);
        }

        [Fact]
        public async Task CallAsync_enforces_max_response_bytes()
        {
            var (t, h) = NewTransport(new HttpTransportOptions { MaxResponseBytes = 10 });
            h.Body = "{\"a\":\"0123456789012345\"}";

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(GGFailureKind.Decode, ex.Kind);
            Assert.Equal("response_too_large", ex.Code);
        }

        [Fact]
        public async Task CallAsync_parses_retry_after_http_date()
        {
            var clock = new FakeClock();
            var (t, h) = NewTransport(clock: clock);
            h.Status = (HttpStatusCode)503;
            h.ResponseHeaders["Retry-After"] = clock.UtcNow.AddSeconds(90).ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(TimeSpan.FromSeconds(90), ex.RetryAfter);
        }

        [Fact]
        public async Task CallAsync_returns_etag_and_not_modified_on_304_with_if_none_match()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.NotModified;
            h.ResponseHeaders["ETag"] = "\"remote-config-abc\"";

            var got = await t.CallAsync(
                new GGRequest { Method = "GET", Path = "/v1/config", IfNoneMatch = "\"remote-config-abc\"" },
                CancellationToken.None);

            Assert.True(got.NotModified);
            Assert.Equal("\"remote-config-abc\"", got.ETag);
            Assert.Equal(JsonKind.Null, got.Value.Kind);
            Assert.Equal("\"remote-config-abc\"", string.Join(",", h.LastRequest!.Headers.GetValues("If-None-Match")));
        }

        [Fact]
        public async Task CallAsync_304_without_if_none_match_is_an_error()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.NotModified;

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => t.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(304, ex.Status);
        }

        [Fact]
        public async Task CallAsync_sends_request_id_and_configured_user_agent()
        {
            var (t, h) = NewTransport(new HttpTransportOptions { UserAgent = "my-game/1.2" });
            h.Body = "{}";

            var req = new GGRequest { Method = "GET", Path = "/v1/x", RequestId = "rid-1" };
            await t.CallAsync(req, CancellationToken.None);

            Assert.Equal("rid-1", string.Join(",", h.LastRequest!.Headers.GetValues("X-Request-Id")));
            Assert.Equal("my-game/1.2", string.Join(",", h.LastRequest.Headers.GetValues("User-Agent")));
        }
    }
}
