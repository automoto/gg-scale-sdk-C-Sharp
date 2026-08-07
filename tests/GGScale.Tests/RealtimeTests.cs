using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    /// <summary>
    /// Scripted socket adapter. Push stages messages; PushClose stages a
    /// close (null code = abnormal drop); EndStream stages a normal 1000
    /// close. FailNextConnect makes upcoming ConnectAsync calls throw.
    /// </summary>
    internal sealed class FakeSocketAdapter : ISocketAdapter, IDisposable
    {
        private sealed class Item
        {
            public string? Message;
            public int? CloseCode;
            public string? CloseDescription;
        }

        private readonly BlockingCollection<Item> _items = new BlockingCollection<Item>();
        private readonly ConcurrentQueue<GGScaleException> _connectFailures = new ConcurrentQueue<GGScaleException>();
        private int _hangNextConnects;

        public void Dispose() => _items.Dispose();

        public Uri? ConnectedUri { get; private set; }

        public string? ApiKey { get; private set; }

        public string? SessionToken { get; private set; }

        public bool Closed { get; private set; }

        public int ConnectCount { get; private set; }

        public int? CloseCode { get; private set; }

        public string? CloseDescription { get; private set; }

        public List<string> Events { get; } = new List<string>();

        public List<string> SessionTokens { get; } = new List<string>();

        public void Push(string message) => _items.Add(new Item { Message = message });

        public void PushClose(int? code, string? description = null) =>
            _items.Add(new Item { CloseCode = code, CloseDescription = description });

        public void EndStream() => PushClose(1000);

        public void FailNextConnect(GGScaleException error) => _connectFailures.Enqueue(error);

        public void HangNextConnect() => Interlocked.Increment(ref _hangNextConnects);

        public Task ConnectAsync(Uri uri, string apiKey, string sessionToken, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _hangNextConnects, 0, 0) > 0)
            {
                Interlocked.Decrement(ref _hangNextConnects);
                lock (Events)
                {
                    Events.Add("connect-hang");
                }
                return Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            if (_connectFailures.TryDequeue(out var failure))
            {
                lock (Events)
                {
                    Events.Add("connect-failed");
                }
                throw failure;
            }
            ConnectedUri = uri;
            ApiKey = apiKey;
            SessionToken = sessionToken;
            CloseCode = null;
            CloseDescription = null;
            lock (Events)
            {
                Events.Add("connect");
                ConnectCount++;
                SessionTokens.Add(sessionToken);
            }
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                Item item;
                try
                {
                    item = _items.Take(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    return (string?)null;
                }
                if (item.Message != null)
                {
                    return item.Message;
                }
                CloseCode = item.CloseCode;
                CloseDescription = item.CloseDescription;
                return null;
            }, cancellationToken);
        }

        public Task CloseAsync()
        {
            Closed = true;
            return Task.CompletedTask;
        }
    }

    public class RealtimeClientTests
    {
        [Fact]
        public async Task Dial_requires_session()
        {
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "http://api.test", Transport = new FakeTransport() });

            await Assert.ThrowsAsync<InvalidOperationException>(() => c.DialRealtimeAsync(new FakeSocketAdapter()));
        }

        [Fact]
        public async Task Dial_derives_ws_url_and_sends_credentials()
        {
            var ft = new FakeTransport();
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", BaseUrl = "http://api.test", Transport = ft });
            c.SetSession(Canned.Live());
            var adapter = new FakeSocketAdapter();

            var rc = await c.DialRealtimeAsync(adapter);

            Assert.Equal("ws://api.test/v1/ws", adapter.ConnectedUri!.AbsoluteUri);
            Assert.Equal("pk", adapter.ApiKey);
            Assert.Equal("live-jwt", adapter.SessionToken);
            await rc.DisposeAsync();
            Assert.True(adapter.Closed);
        }

        [Fact]
        public async Task Dial_uses_wss_for_https()
        {
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "https://api.test", Transport = new FakeTransport() });
            c.SetSession(Canned.Live());
            var adapter = new FakeSocketAdapter();

            await c.DialRealtimeAsync(adapter);

            Assert.Equal("wss://api.test/v1/ws", adapter.ConnectedUri!.AbsoluteUri);
        }

        [Fact]
        public async Task ReadMessage_parses_envelope()
        {
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "http://api.test", Transport = new FakeTransport() });
            c.SetSession(Canned.Live());
            var adapter = new FakeSocketAdapter();
            adapter.Push("{\"type\":\"presence\",\"payload\":{\"player_id\":7,\"status\":\"online\"}}");

            var rc = await c.DialRealtimeAsync(adapter);
            var msg = await rc.ReadMessageAsync(CancellationToken.None);

            Assert.NotNull(msg);
            Assert.Equal("presence", msg!.Type);
            Assert.Equal(7L, msg.Payload.OptLong("player_id"));
        }

        [Fact]
        public async Task ReadMessage_returns_null_when_stream_ends()
        {
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "http://api.test", Transport = new FakeTransport() });
            c.SetSession(Canned.Live());
            var adapter = new FakeSocketAdapter();
            adapter.EndStream();

            var rc = await c.DialRealtimeAsync(adapter);
            var msg = await rc.ReadMessageAsync(CancellationToken.None);

            Assert.Null(msg);
        }
    }

    public class ManagedRealtimeTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        private static GGScaleClient NewClient(FakeTransport ft, FakeClock clock)
        {
            var c = new GGScaleClient(new GGScaleClientOptions
            {
                ApiKey = "pk",
                BaseUrl = "http://api.test",
                Transport = ft,
                Clock = clock,
            });
            c.SetSession(new Session("tok-1", "rt", 9, clock.UtcNow.AddHours(1)));
            return c;
        }

        [Fact]
        public async Task Read_loop_skips_malformed_frames_and_continues()
        {
            var adapter = new FakeSocketAdapter();
            var c = NewClient(new FakeTransport(), new FakeClock());
            adapter.Push("this is not json");
            adapter.Push("{\"type\":\"presence\",\"payload\":{}}");

            var rc = await c.DialRealtimeAsync(adapter);
            var msg = await rc.ReadMessageAsync().WaitAsync(TestTimeout);

            Assert.Equal("presence", msg!.Type);
            await rc.CloseAsync();
        }

        [Fact]
        public async Task Abnormal_drop_reconnects_and_fires_isreconnect_signal()
        {
            var adapter = new FakeSocketAdapter();
            var clock = new FakeClock();
            var c = NewClient(new FakeTransport(), clock);

            var rc = await c.DialRealtimeAsync(adapter);
            var reconnected = new TaskCompletionSource<RealtimeStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            rc.StateChanged += (_, change) =>
            {
                if (change.State == RealtimeState.Connected && change.IsReconnect)
                {
                    reconnected.TrySetResult(change);
                }
            };
            adapter.PushClose(null); // abnormal drop, no close frame
            adapter.Push("{\"type\":\"presence\",\"payload\":{}}");

            var msg = await rc.ReadMessageAsync().WaitAsync(TestTimeout);
            var change = await reconnected.Task.WaitAsync(TestTimeout);

            Assert.Equal("presence", msg!.Type);
            Assert.Equal(2, adapter.ConnectCount);
            Assert.Equal(1, change.Attempt);
            Assert.InRange(clock.Delays[0], TimeSpan.Zero, TimeSpan.FromSeconds(5));
            await rc.CloseAsync();
        }

        [Fact]
        public async Task Application_close_code_is_terminal()
        {
            var adapter = new FakeSocketAdapter();
            var c = NewClient(new FakeTransport(), new FakeClock());
            adapter.PushClose(4001, "policy");

            var rc = await c.DialRealtimeAsync(adapter);
            var msg = await rc.ReadMessageAsync().WaitAsync(TestTimeout);

            Assert.Null(msg);
            Assert.Equal(1, adapter.ConnectCount);
            await rc.CloseAsync();
        }

        [Fact]
        public async Task Handshake_503_retry_after_is_minimum_wait()
        {
            var adapter = new FakeSocketAdapter();
            var clock = new FakeClock();
            var c = NewClient(new FakeTransport(), clock);
            var busy = new GGScaleException(GGFailureKind.Handshake, "ws_handshake_failed", "busy")
            {
                Status = 503,
                RetryAfter = TimeSpan.FromSeconds(7),
            };

            var rc = await c.DialRealtimeAsync(adapter);
            adapter.FailNextConnect(busy);
            adapter.PushClose(null);
            adapter.Push("{\"type\":\"presence\",\"payload\":{}}");
            var msg = await rc.ReadMessageAsync().WaitAsync(TestTimeout);

            Assert.Equal("presence", msg!.Type);
            Assert.Equal(2, adapter.ConnectCount);
            Assert.Equal(2, clock.Delays.Count);
            Assert.True(clock.Delays[1] >= TimeSpan.FromSeconds(7));
            await rc.CloseAsync();
        }

        [Fact]
        public async Task Handshake_401_is_terminal_with_error()
        {
            var adapter = new FakeSocketAdapter();
            var c = NewClient(new FakeTransport(), new FakeClock());

            var rc = await c.DialRealtimeAsync(adapter);
            adapter.FailNextConnect(new GGScaleException(GGFailureKind.Handshake, "ws_handshake_failed", "unauthorized")
            {
                Status = 401,
            });
            adapter.PushClose(null);
            var closed = new TaskCompletionSource<RealtimeStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            rc.StateChanged += (_, change) =>
            {
                if (change.State == RealtimeState.Closed)
                {
                    closed.TrySetResult(change);
                }
            };

            var msg = await rc.ReadMessageAsync().WaitAsync(TestTimeout);
            var change = await closed.Task.WaitAsync(TestTimeout);

            Assert.Null(msg);
            Assert.Equal(1, adapter.ConnectCount);
            Assert.IsType<GGScaleException>(change.Error);
        }

        [Fact]
        public async Task Reconnect_dials_with_current_session_token()
        {
            var adapter = new FakeSocketAdapter();
            var clock = new FakeClock();
            var c = NewClient(new FakeTransport(), clock);

            var rc = await c.DialRealtimeAsync(adapter);
            c.SetSession(new Session("tok-2", "rt", 9, clock.UtcNow.AddHours(1)));
            adapter.PushClose(null);
            adapter.Push("{\"type\":\"presence\",\"payload\":{}}");
            await rc.ReadMessageAsync().WaitAsync(TestTimeout);

            Assert.Equal("tok-1,tok-2", string.Join(",", adapter.SessionTokens));
            await rc.CloseAsync();
        }

        [Fact]
        public async Task Overflow_drops_oldest_and_raises_degraded_with_count()
        {
            var adapter = new FakeSocketAdapter();
            var c = NewClient(new FakeTransport(), new FakeClock());
            var rc = await c.DialRealtimeAsync(new RealtimeOptions { QueueCapacity = 1 }, adapter);
            var degradedTwice = new TaskCompletionSource<RealtimeStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            rc.StateChanged += (_, change) =>
            {
                if (change.State == RealtimeState.Degraded && change.DroppedMessages == 2)
                {
                    degradedTwice.TrySetResult(change);
                }
            };

            adapter.Push("{\"type\":\"m1\"}");
            adapter.Push("{\"type\":\"m2\"}");
            adapter.Push("{\"type\":\"m3\"}");
            await degradedTwice.Task.WaitAsync(TestTimeout);

            var kept = await rc.ReadMessageAsync().WaitAsync(TestTimeout);
            Assert.Equal("m3", kept!.Type);
            await rc.CloseAsync();
        }

        [Fact]
        public async Task CloseAsync_stops_reconnect_and_completes_pending_reads()
        {
            var adapter = new FakeSocketAdapter();
            var c = NewClient(new FakeTransport(), new FakeClock());
            var rc = await c.DialRealtimeAsync(adapter);
            var pending = rc.ReadMessageAsync();

            await rc.CloseAsync().WaitAsync(TestTimeout);

            Assert.True(adapter.Closed);
            Assert.Null(await pending.WaitAsync(TestTimeout));
        }

        [Fact]
        public async Task Reconnect_never_replays_a_failed_session_refresh()
        {
            var adapter = new FakeSocketAdapter();
            var ft = new FakeTransport();
            var clock = new FakeClock();
            var c = new GGScaleClient(new GGScaleClientOptions
            {
                ApiKey = "pk",
                BaseUrl = "http://api.test",
                Transport = ft,
                Clock = clock,
            });
            c.SetSession(new Session("tok-1", "rt", 9, clock.UtcNow.AddHours(1)));

            var rc = await c.DialRealtimeAsync(adapter);
            var closed = new TaskCompletionSource<RealtimeStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            rc.StateChanged += (_, change) =>
            {
                if (change.State == RealtimeState.Closed)
                {
                    closed.TrySetResult(change);
                }
            };
            // Force the reconnect into the refresh window and make the
            // refresh fail ambiguously mid-flight.
            c.SetSession(new Session("tok-1", "rt", 9, clock.UtcNow.AddSeconds(10)));
            ft.EnqueueError(new GGScaleException(GGFailureKind.Connection, "connection_error", "reset mid-flight"));
            adapter.PushClose(null);

            var msg = await rc.ReadMessageAsync().WaitAsync(TestTimeout);
            var change = await closed.Task.WaitAsync(TestTimeout);

            Assert.Null(msg);
            Assert.Equal(1, ft.CountForPath("/v1/auth/refresh"));
            Assert.Equal(1, adapter.ConnectCount);
            var error = Assert.IsType<GGScaleException>(change.Error);
            Assert.Equal("session_refresh_failed", error.Code);
        }

        [Fact]
        public async Task Reconnect_timeout_bounds_a_hung_connect()
        {
            var adapter = new FakeSocketAdapter();
            var c = NewClient(new FakeTransport(), new FakeClock());

            var rc = await c.DialRealtimeAsync(new RealtimeOptions
            {
                ReconnectTimeout = TimeSpan.FromMilliseconds(200),
                FirstReconnectMaxDelay = TimeSpan.Zero,
            }, adapter);
            var closed = new TaskCompletionSource<RealtimeStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            rc.StateChanged += (_, change) =>
            {
                if (change.State == RealtimeState.Closed)
                {
                    closed.TrySetResult(change);
                }
            };
            adapter.HangNextConnect();
            adapter.PushClose(null);

            var msg = await rc.ReadMessageAsync().WaitAsync(TestTimeout);
            var change = await closed.Task.WaitAsync(TestTimeout);

            Assert.Null(msg);
            var error = Assert.IsType<GGScaleException>(change.Error);
            Assert.Equal("ws_reconnect_timeout", error.Code);
        }

        [Fact]
        public async Task CloseAsync_and_DisposeAsync_are_idempotent()
        {
            var adapter = new FakeSocketAdapter();
            var c = NewClient(new FakeTransport(), new FakeClock());
            var rc = await c.DialRealtimeAsync(adapter);

            await rc.CloseAsync().WaitAsync(TestTimeout);
            await rc.CloseAsync().WaitAsync(TestTimeout);
            await rc.DisposeAsync();

            Assert.True(adapter.Closed);
        }

        [Fact]
        public async Task AutoReconnect_false_makes_abnormal_drop_terminal()
        {
            var adapter = new FakeSocketAdapter();
            var c = NewClient(new FakeTransport(), new FakeClock());
            adapter.PushClose(null);

            var rc = await c.DialRealtimeAsync(new RealtimeOptions { AutoReconnect = false }, adapter);
            var msg = await rc.ReadMessageAsync().WaitAsync(TestTimeout);

            Assert.Null(msg);
            Assert.Equal(1, adapter.ConnectCount);
            await rc.CloseAsync();
        }
    }

    public class RequestMatchTests
    {
        [Fact]
        public async Task RequestMatch_dials_before_creating_ticket_and_returns_address()
        {
            var adapter = new FakeSocketAdapter();
            var ft = new FakeTransport();
            ft.Respond = req =>
            {
                if (req.Path == "/v1/matchmaker/tickets")
                {
                    lock (adapter.Events)
                    {
                        adapter.Events.Add("create-ticket");
                    }
                    return JsonValue.NewObject()
                        .Set("id", JsonValue.Of(7L))
                        .Set("status", JsonValue.Of("queued"))
                        .Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"));
                }
                return JsonValue.Null;
            };
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "http://api.test", Transport = ft });
            c.SetSession(Canned.Live());

            adapter.Push("{\"type\":\"presence\",\"payload\":{}}");
            adapter.Push("{\"type\":\"matchmaker_matched\",\"payload\":{\"mode\":\"match_only\",\"ticket_id\":7,\"host_player_id\":7,\"users\":[{\"player_id\":7},{\"player_id\":8}]}}");

            var result = await c.Matchmaker.WaitForMatchAsync(new MatchRequest { Mode = MatchMode.MatchOnly, GameMode = "dm" }, adapter);

            Assert.Equal("match_only", result.Mode);
            Assert.Equal(7L, result.TicketId);
            Assert.Equal(7L, result.HostPlayerId);
            Assert.Equal(2, result.Users.Count);
            Assert.Equal("connect,create-ticket", string.Join(",", adapter.Events));
            Assert.True(adapter.Closed, "socket is closed after the match is found");
        }

        [Fact]
        public async Task WaitForMatch_survives_reconnect_and_receives_match()
        {
            var adapter = new FakeSocketAdapter();
            var ft = new FakeTransport();
            ft.Respond = req =>
            {
                if (req.Path == "/v1/matchmaker/tickets")
                {
                    return JsonValue.NewObject()
                        .Set("id", JsonValue.Of(7L))
                        .Set("status", JsonValue.Of("queued"))
                        .Set("created_at", JsonValue.Of("2026-08-07T10:00:00Z"));
                }
                return JsonValue.Null;
            };
            var clock = new FakeClock();
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "http://api.test", Transport = ft, Clock = clock });
            c.SetSession(new Session("live-jwt", "rt", 7, clock.UtcNow.AddHours(1)));

            adapter.PushClose(null); // drop before the push arrives
            adapter.Push("{\"type\":\"matchmaker_matched\",\"payload\":{\"mode\":\"match_only\",\"ticket_id\":7,\"host_player_id\":7,\"users\":[{\"player_id\":7}]}}");

            var result = await c.Matchmaker.WaitForMatchAsync(new MatchRequest { Mode = MatchMode.MatchOnly }, adapter);

            Assert.Equal(7L, result.TicketId);
            Assert.Equal(2, adapter.ConnectCount);
        }

        [Fact]
        public async Task WaitForMatch_cancels_ticket_on_cancellation()
        {
            // No realtime delivery: the stream closes, WaitForMatch falls back
            // to polling, and cancelling the token best-effort cancels the
            // still-queued ticket.
            var adapter = new FakeSocketAdapter();
            var ft = new FakeTransport();
            ft.Respond = req =>
            {
                if (req.Path == "/v1/matchmaker/tickets")
                {
                    return JsonValue.NewObject()
                        .Set("id", JsonValue.Of(9L))
                        .Set("status", JsonValue.Of("queued"))
                        .Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"));
                }
                // GET poll returns a still-queued ticket forever.
                return JsonValue.NewObject()
                    .Set("id", JsonValue.Of(9L))
                    .Set("status", JsonValue.Of("queued"))
                    .Set("created_at", JsonValue.Of("2026-07-06T10:00:00Z"));
            };
            var c = new GGScaleClient(new GGScaleClientOptions { ApiKey = "k", BaseUrl = "http://api.test", Transport = ft });
            c.SetSession(Canned.Live());
            c.Matchmaker.PollInterval = TimeSpan.FromMilliseconds(10);
            adapter.EndStream();

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => c.Matchmaker.WaitForMatchAsync(new MatchRequest(), adapter, cts.Token));

            Assert.Equal("DELETE", ft.LastRequest!.Method);
            Assert.Equal("/v1/matchmaker/tickets/9", ft.LastRequest!.Path);
        }
    }
}
