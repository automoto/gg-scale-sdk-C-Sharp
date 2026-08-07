using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    /// <summary>Collects telemetry records for assertions.</summary>
    public sealed class RecordingLogger : IGGScaleLogger
    {
        private readonly object _mu = new object();
        private readonly List<GGCallRecord> _calls = new List<GGCallRecord>();
        private readonly List<GGRetryRecord> _retries = new List<GGRetryRecord>();
        private readonly List<GGWsEventRecord> _wsEvents = new List<GGWsEventRecord>();

        public IReadOnlyList<GGCallRecord> Calls
        {
            get
            {
                lock (_mu)
                {
                    return _calls.ToArray();
                }
            }
        }

        public IReadOnlyList<GGRetryRecord> Retries
        {
            get
            {
                lock (_mu)
                {
                    return _retries.ToArray();
                }
            }
        }

        public IReadOnlyList<GGWsEventRecord> WsEvents
        {
            get
            {
                lock (_mu)
                {
                    return _wsEvents.ToArray();
                }
            }
        }

        public void OnCallCompleted(GGCallRecord record)
        {
            lock (_mu)
            {
                _calls.Add(record);
            }
        }

        public void OnRetry(GGRetryRecord record)
        {
            lock (_mu)
            {
                _retries.Add(record);
            }
        }

        public void OnWsEvent(GGWsEventRecord record)
        {
            lock (_mu)
            {
                _wsEvents.Add(record);
            }
        }
    }

    public class RetryTests
    {
        private static RetryingTransport NewRetrying(
            FakeTransport ft,
            FakeClock clock,
            GGRetryPolicy? policy = null,
            TimeSpan? overall = null,
            IGGScaleLogger? logger = null)
        {
            return new RetryingTransport(ft, policy ?? new GGRetryPolicy(), overall ?? TimeSpan.FromSeconds(100), clock, logger);
        }

        private static GGScaleException Err(int status, TimeSpan? retryAfter = null) =>
            new GGScaleException(status, "", "staged error", retryAfter);

        [Fact]
        public async Task Get_retries_on_503_then_succeeds_with_jittered_delays()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(503));
            ft.EnqueueError(Err(503));
            ft.EnqueueResult(JsonValue.Parse("{\"ok\":true}"));
            var clock = new FakeClock();
            var rt = NewRetrying(ft, clock);

            var resp = await rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);

            Assert.True(resp.Value.OptBool("ok"));
            Assert.Equal(3, ft.CallCount);
            Assert.Equal(2, clock.Delays.Count);
            Assert.InRange(clock.Delays[0], TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
            Assert.InRange(clock.Delays[1], TimeSpan.Zero, TimeSpan.FromMilliseconds(1000));
        }

        [Fact]
        public async Task Post_is_never_retried_by_default()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(503));
            var rt = NewRetrying(ft, new FakeClock());

            await Assert.ThrowsAsync<GGScaleException>(
                () => rt.CallAsync(new GGRequest { Method = "POST", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(1, ft.CallCount);
        }

        [Fact]
        public async Task Post_with_idempotent_flag_is_retried()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(503));
            ft.EnqueueResult(JsonValue.Null);
            var rt = NewRetrying(ft, new FakeClock());

            await rt.CallAsync(new GGRequest { Method = "POST", Path = "/v1/x", Idempotent = true }, CancellationToken.None);

            Assert.Equal(2, ft.CallCount);
        }

        [Fact]
        public async Task Retry_stops_at_max_attempts_and_throws_last_error()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(502));
            ft.EnqueueError(Err(503));
            ft.EnqueueError(Err(504));
            var rt = NewRetrying(ft, new FakeClock());

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(504, ex.Status);
            Assert.Equal(3, ft.CallCount);
        }

        [Fact]
        public async Task Retry_honors_retry_after_as_minimum_wait()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(429, TimeSpan.FromSeconds(5)));
            ft.EnqueueResult(JsonValue.Null);
            var clock = new FakeClock();
            var rt = NewRetrying(ft, clock);

            await rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);

            Assert.True(clock.Delays[0] >= TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task Retry_gives_up_when_retry_after_exceeds_deadline()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(429, TimeSpan.FromSeconds(60)));
            var clock = new FakeClock();
            var rt = NewRetrying(ft, clock, overall: TimeSpan.FromSeconds(10));

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(429, ex.Status);
            Assert.Equal(1, ft.CallCount);
            Assert.Empty(clock.Delays);
        }

        [Fact]
        public async Task Cancellation_during_backoff_surfaces_canceled()
        {
            var ft = new FakeTransport();
            using var cts = new CancellationTokenSource();
            ft.EnqueueStep((_, _) =>
            {
                cts.Cancel();
                return Task.FromException<GGResponse>(Err(503));
            });
            var rt = NewRetrying(ft, new FakeClock());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, cts.Token));

            Assert.Equal(1, ft.CallCount);
        }

        [Theory]
        [InlineData(400)]
        [InlineData(401)]
        [InlineData(403)]
        [InlineData(404)]
        [InlineData(500)]
        public async Task Non_retryable_statuses_surface_immediately(int status)
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(status));
            var rt = NewRetrying(ft, new FakeClock());

            await Assert.ThrowsAsync<GGScaleException>(
                () => rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.Equal(1, ft.CallCount);
        }

        [Fact]
        public async Task Refresh_endpoint_is_never_auto_retried()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(new GGScaleException(GGFailureKind.Connection, "connection_error", "reset mid-flight"));
            var rt = NewRetrying(ft, new FakeClock());

            await Assert.ThrowsAsync<GGScaleException>(
                () => rt.CallAsync(new GGRequest { Method = "POST", Path = "/v1/auth/refresh" }, CancellationToken.None));

            Assert.Equal(1, ft.CallCount);
        }

        [Fact]
        public async Task Connection_and_timeout_kinds_are_retried_for_get()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(new GGScaleException(GGFailureKind.Connection, "connection_error", "refused"));
            ft.EnqueueError(new GGScaleException(GGFailureKind.Timeout, "timeout", "slow"));
            ft.EnqueueResult(JsonValue.Null);
            var rt = NewRetrying(ft, new FakeClock());

            await rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);

            Assert.Equal(3, ft.CallCount);
        }

        [Fact]
        public async Task Request_id_is_generated_once_and_stable_across_attempts()
        {
            var ft = new FakeTransport();
            var seen = new List<string?>();
            ft.EnqueueStep((r, _) =>
            {
                seen.Add(r.RequestId);
                return Task.FromException<GGResponse>(Err(503));
            });
            ft.EnqueueStep((r, _) =>
            {
                seen.Add(r.RequestId);
                return Task.FromResult(new GGResponse(200, JsonValue.Null));
            });
            var rt = NewRetrying(ft, new FakeClock());

            await rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);

            Assert.Equal(2, seen.Count);
            Assert.False(string.IsNullOrEmpty(seen[0]));
            Assert.Equal(seen[0], seen[1]);
        }

        [Fact]
        public async Task Logger_receives_one_completion_record_and_per_retry_records()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(Err(503));
            ft.EnqueueError(Err(503));
            ft.EnqueueResult(JsonValue.Null);
            var logger = new RecordingLogger();
            var rt = NewRetrying(ft, new FakeClock(), logger: logger);

            await rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x", Operation = "GET /v1/x" }, CancellationToken.None);

            var call = Assert.Single(logger.Calls);
            Assert.Equal(3, call.Attempts);
            Assert.Equal(200, call.Status);
            Assert.Null(call.FailureKind);
            Assert.Equal(2, logger.Retries.Count);
            Assert.Equal("http_503", logger.Retries[0].Reason);
        }

        [Fact]
        public async Task Logger_records_carry_route_template_not_raw_path_or_secrets()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Null);
            var logger = new RecordingLogger();
            var rt = NewRetrying(ft, new FakeClock(), logger: logger);

            await rt.CallAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/storage/objects/super-secret-key-name",
                Operation = "GET /v1/storage/objects/{key}",
                ApiKey = "sk_secret_value",
                SessionToken = "session-token-value",
            }, CancellationToken.None);

            var call = Assert.Single(logger.Calls);
            Assert.Equal("GET /v1/storage/objects/{key}", call.Operation);
            Assert.DoesNotContain("super-secret-key-name", call.Operation, StringComparison.Ordinal);
            Assert.DoesNotContain("sk_secret_value", call.RequestId ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Certificate_failures_are_never_retried()
        {
            var ft = new FakeTransport();
            ft.EnqueueError(new GGScaleException(GGFailureKind.Connection, "certificate_error", "TLS validation failed"));
            var rt = NewRetrying(ft, new FakeClock());

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            Assert.False(ex.IsRetryable);
            Assert.Equal(1, ft.CallCount);
        }

        [Fact]
        public async Task Overall_deadline_bounds_a_stalled_attempt()
        {
            var ft = new FakeTransport();
            ft.EnqueueStep(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new GGResponse(200, JsonValue.Null);
            });
            var rt = new RetryingTransport(
                ft, new GGRetryPolicy(), TimeSpan.FromMilliseconds(100), SystemClock.Instance, null);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var ex = await Assert.ThrowsAsync<GGScaleException>(
                () => rt.CallAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None));

            sw.Stop();
            Assert.Equal(GGFailureKind.Timeout, ex.Kind);
            Assert.Equal("deadline_exceeded", ex.Code);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        }

        [Fact]
        public async Task Protected_call_emits_one_completion_record_across_401_refresh()
        {
            var ft = new FakeTransport();
            var clock = new FakeClock();
            var logger = new RecordingLogger();
            using var client = new GGScaleClient(new GGScaleClientOptions
            {
                ApiKey = "pk",
                Transport = ft,
                Clock = clock,
                Logger = logger,
            });
            client.SetSession(new Session("acc", "ref", 1, clock.UtcNow.AddHours(1)));
            ft.EnqueueError(Err(401));
            ft.EnqueueResult(JsonValue.Parse(
                "{\"access_token\":\"acc2\",\"refresh_token\":\"ref2\",\"player_id\":1,\"expires_at\":\"2026-01-01T02:00:00Z\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"ok\":true}"));

            await client.CallProtectedAsync(
                new GGRequest { Method = "GET", Path = "/v1/x", Operation = "GET /v1/x" }, CancellationToken.None);

            var forCall = new List<GGCallRecord>();
            foreach (var r in logger.Calls)
            {
                if (r.Operation == "GET /v1/x")
                {
                    forCall.Add(r);
                }
            }
            var record = Assert.Single(forCall);
            Assert.Equal(200, record.Status);
            Assert.Equal(2, record.Attempts);
        }

        [Fact]
        public async Task Failed_refresh_emits_the_deferred_401_completion_once()
        {
            var ft = new FakeTransport();
            var clock = new FakeClock();
            var logger = new RecordingLogger();
            using var client = new GGScaleClient(new GGScaleClientOptions
            {
                ApiKey = "pk",
                Transport = ft,
                Clock = clock,
                Logger = logger,
            });
            client.SetSession(new Session("acc", "ref", 1, clock.UtcNow.AddHours(1)));
            ft.EnqueueError(Err(401));
            ft.EnqueueError(Err(500)); // the refresh attempt fails

            var ex = await Assert.ThrowsAsync<GGScaleException>(() => client.CallProtectedAsync(
                new GGRequest { Method = "GET", Path = "/v1/x", Operation = "GET /v1/x" }, CancellationToken.None));

            Assert.Equal(401, ex.Status);
            var forCall = new List<GGCallRecord>();
            foreach (var r in logger.Calls)
            {
                if (r.Operation == "GET /v1/x")
                {
                    forCall.Add(r);
                }
            }
            var record = Assert.Single(forCall);
            Assert.Equal(401, record.Status);
        }

        [Fact]
        public async Task Client_composes_retry_then_401_refresh_then_success_without_double_retry()
        {
            var ft = new FakeTransport();
            var clock = new FakeClock();
            using var client = new GGScaleClient(new GGScaleClientOptions
            {
                ApiKey = "pk",
                Transport = ft,
                Clock = clock,
            });
            client.SetSession(new Session("acc", "ref", 1, clock.UtcNow.AddHours(1)));

            var rids = new List<string?>();
            ft.EnqueueStep((r, _) =>
            {
                rids.Add(r.RequestId);
                return Task.FromException<GGResponse>(Err(503));
            });
            ft.EnqueueStep((r, _) =>
            {
                rids.Add(r.RequestId);
                return Task.FromException<GGResponse>(Err(401));
            });
            ft.EnqueueResult(JsonValue.Parse(
                "{\"access_token\":\"acc2\",\"refresh_token\":\"ref2\",\"player_id\":1,\"expires_at\":\"2026-01-01T02:00:00Z\"}"));
            ft.EnqueueStep((r, _) =>
            {
                rids.Add(r.RequestId);
                return Task.FromResult(new GGResponse(200, JsonValue.Parse("{\"ok\":true}")));
            });

            var got = await client.CallProtectedAsync(new GGRequest { Method = "GET", Path = "/v1/x" }, CancellationToken.None);

            Assert.True(got.OptBool("ok"));
            Assert.Equal(4, ft.CallCount);
            Assert.Equal("/v1/auth/refresh", ft.Requests[2].Path);
            Assert.Equal(3, rids.Count);
            Assert.Equal(rids[0], rids[1]);
            Assert.Equal(rids[1], rids[2]);
        }
    }
}
