using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    /// <summary>Scripted socket adapter: messages are staged; null ends the stream.</summary>
    internal sealed class FakeSocketAdapter : ISocketAdapter, IDisposable
    {
        private readonly BlockingCollection<string> _messages = new BlockingCollection<string>();

        public void Dispose() => _messages.Dispose();

        public Uri? ConnectedUri { get; private set; }

        public string? ApiKey { get; private set; }

        public string? SessionToken { get; private set; }

        public bool Closed { get; private set; }

        public List<string> Events { get; } = new List<string>();

        public void Push(string message) => _messages.Add(message);

        public void EndStream() => _messages.CompleteAdding();

        public Task ConnectAsync(Uri uri, string apiKey, string sessionToken, CancellationToken cancellationToken)
        {
            ConnectedUri = uri;
            ApiKey = apiKey;
            SessionToken = sessionToken;
            lock (Events)
            {
                Events.Add("connect");
            }
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                try
                {
                    return (string?)_messages.Take(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    return null; // stream ended
                }
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
