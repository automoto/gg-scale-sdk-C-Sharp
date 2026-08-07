using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    /// <summary>
    /// Cross-cutting conformance checks from the SDK client guide:
    /// success codes, unknown-field/enum tolerance, Retry-After forms
    /// end-to-end, and redaction.
    /// </summary>
    public class ConformanceTests
    {
        [Theory]
        [InlineData(200)]
        [InlineData(201)]
        [InlineData(202)]
        [InlineData(204)]
        public async Task Every_documented_success_code_returns_normally(int status)
        {
            var handler = new StubHandler { Status = (HttpStatusCode)status };
            if (status != 204)
            {
                handler.Body = "{}";
            }
            var transport = new HttpTransport("http://api.test", new HttpClient(handler));

            var resp = await transport.CallAsync(new GGRequest { Method = "POST", Path = "/v1/x" }, CancellationToken.None);

            Assert.Equal(status, resp.Status);
        }

        [Fact]
        public async Task Unknown_json_fields_are_ignored_when_decoding()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"id\":1,\"external_id\":\"x\",\"created_at\":\"2026-08-07T09:00:00Z\"," +
                    "\"brand_new_field\":{\"nested\":true},\"another\":[1,2,3]}"),
            };
            using var client = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft });
            client.SetSession(new Session("t", "r", 1, DateTimeOffset.UtcNow.AddHours(1)));

            var profile = await client.Profile.GetAsync();

            Assert.Equal(1L, profile.Id);
            Assert.Equal("x", profile.ExternalId);
        }

        [Fact]
        public async Task Unknown_enum_values_pass_through_as_strings()
        {
            var ft = new FakeTransport
            {
                Respond = _ => JsonValue.Parse(
                    "{\"leaderboards\":[{\"id\":1,\"name\":\"n\",\"sort_order\":\"sideways\"," +
                    "\"score_operator\":\"future_op\",\"reset_schedule\":\"hourly\",\"current_period\":1}]}"),
            };
            using var client = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft });
            client.SetSession(new Session("t", "r", 1, DateTimeOffset.UtcNow.AddHours(1)));

            var boards = await client.Leaderboards.ListAsync();

            Assert.Equal("sideways", boards[0].SortOrder);
            Assert.Equal("future_op", boards[0].ScoreOperator);
            Assert.Equal("hourly", boards[0].ResetSchedule);
        }

        [Fact]
        public async Task Unknown_realtime_event_types_are_surfaced_not_dropped()
        {
            var adapter = new FakeSocketAdapter();
            var c = new GGScaleClient(new GGScaleClientOptions
            {
                ApiKey = "pk",
                BaseUrl = "http://api.test",
                Transport = new FakeTransport(),
                Clock = new FakeClock(),
            });
            c.SetSession(new Session("t", "r", 1, new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero)));
            adapter.Push("{\"type\":\"totally_new_event\",\"payload\":{\"x\":1}}");

            var rc = await c.DialRealtimeAsync(adapter);
            var msg = await rc.ReadMessageAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("totally_new_event", msg!.Type);
            Assert.Equal(1L, msg.Payload.OptLong("x"));
            await rc.CloseAsync();
        }

        [Fact]
        public async Task Retry_after_http_date_flows_through_the_retry_engine()
        {
            var clock = new FakeClock();
            var handler = new StubHandler { Status = (HttpStatusCode)503 };
            handler.ResponseHeaders["Retry-After"] =
                clock.UtcNow.AddSeconds(12).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var transport = new HttpTransport("http://api.test", new HttpClient(handler), null, clock);
            var retrying = new RetryingTransport(transport, new GGRetryPolicy(), TimeSpan.FromSeconds(100), clock, null);

            await Assert.ThrowsAsync<GGScaleException>(
                () => retrying.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(2, clock.Delays.Count);
            Assert.True(clock.Delays[0] >= TimeSpan.FromSeconds(12));
        }

        [Fact]
        public async Task Delta_seconds_retry_after_flows_through_the_retry_engine()
        {
            var clock = new FakeClock();
            var handler = new StubHandler { Status = (HttpStatusCode)429 };
            handler.ResponseHeaders["Retry-After"] = "4";
            var transport = new HttpTransport("http://api.test", new HttpClient(handler), null, clock);
            var retrying = new RetryingTransport(transport, new GGRetryPolicy(), TimeSpan.FromSeconds(100), clock, null);

            await Assert.ThrowsAsync<GGScaleException>(
                () => retrying.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.True(clock.Delays[0] >= TimeSpan.FromSeconds(4));
        }

        [Fact]
        public async Task No_telemetry_record_contains_secrets_or_raw_paths()
        {
            var logger = new RecordingLogger();
            var ft = new FakeTransport();
            ft.EnqueueError(new GGScaleException(503, "busy", "try later"));
            ft.EnqueueResult(JsonValue.Null);
            var retrying = new RetryingTransport(ft, new GGRetryPolicy(), TimeSpan.FromSeconds(100), new FakeClock(), logger);

            await retrying.CallAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/storage/objects/player-secret-save",
                Operation = "GET /v1/storage/objects/{key}",
                ApiKey = "sk_super_secret",
                SessionToken = "session-jwt-value",
            }, CancellationToken.None);

            foreach (var record in logger.Calls)
            {
                AssertClean(record.Operation);
                AssertClean(record.ErrorCode ?? string.Empty);
                AssertClean(record.RequestId);
            }
            foreach (var record in logger.Retries)
            {
                AssertClean(record.Operation);
                AssertClean(record.Reason);
            }
        }

        private static void AssertClean(string value)
        {
            Assert.DoesNotContain("sk_super_secret", value, StringComparison.Ordinal);
            Assert.DoesNotContain("session-jwt-value", value, StringComparison.Ordinal);
            Assert.DoesNotContain("player-secret-save", value, StringComparison.Ordinal);
        }
    }
}
