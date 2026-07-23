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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
        private static (HttpTransport, StubHandler) NewTransport()
        {
            var handler = new StubHandler();
            return (new HttpTransport("http://api.test", new HttpClient(handler)), handler);
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

            Assert.Equal(7L, got.OptLong("v"));
        }

        [Fact]
        public async Task CallAsync_returns_null_value_for_empty_204()
        {
            var (t, h) = NewTransport();
            h.Status = HttpStatusCode.NoContent;

            var got = await t.CallAsync(new GGRequest { Method = "DELETE", Path = "/v1/x" }, CancellationToken.None);

            Assert.Equal(JsonKind.Null, got.Kind);
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
    }
}
