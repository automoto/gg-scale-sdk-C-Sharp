using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// The wire envelope pushed by the server over the realtime WebSocket.
    /// Type discriminates payloads: match_ready, game_invite, presence, …
    /// </summary>
    public sealed class RealtimeMessage
    {
        internal RealtimeMessage(string type, JsonValue payload)
        {
            Type = type;
            Payload = payload;
        }

        /// <summary>Message type discriminator.</summary>
        public string Type { get; }

        /// <summary>Opaque JSON payload; JsonValue.Null when absent.</summary>
        public JsonValue Payload { get; }
    }

    /// <summary>
    /// A raw WebSocket connection used by <see cref="RealtimeClient"/>.
    /// The default is <see cref="WebSocketAdapter"/>; platforms where
    /// ClientWebSocket is unavailable (e.g. WebGL) plug their own.
    /// </summary>
    public interface ISocketAdapter
    {
        /// <summary>Opens the connection, sending the API key and session token as headers.</summary>
        Task ConnectAsync(Uri uri, string apiKey, string sessionToken, CancellationToken cancellationToken);

        /// <summary>
        /// Blocks until a complete text message arrives. Returns null when
        /// the peer closed or dropped the connection.
        /// </summary>
        Task<string?> ReceiveAsync(CancellationToken cancellationToken);

        /// <summary>Closes the connection; safe to call more than once.</summary>
        Task CloseAsync();
    }

    /// <summary>Default <see cref="ISocketAdapter"/> over ClientWebSocket.</summary>
    public sealed class WebSocketAdapter : ISocketAdapter
    {
        private ClientWebSocket? _socket;

        /// <inheritdoc />
        public async Task ConnectAsync(Uri uri, string apiKey, string sessionToken, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
            socket.Options.SetRequestHeader("X-Session-Token", sessionToken);
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            _socket = socket;
        }

        /// <inheritdoc />
        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var socket = _socket;
            if (socket == null)
            {
                return null;
            }
            var buffer = new byte[8192];
            using var assembled = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                assembled.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(assembled.ToArray());
                }
            }
        }

        /// <inheritdoc />
        public async Task CloseAsync()
        {
            var socket = Interlocked.Exchange(ref _socket, null);
            if (socket == null)
            {
                return;
            }
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Peer already gone; disposing is enough.
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            socket.Dispose();
        }
    }

    /// <summary>
    /// A connection to the server's /v1/ws push channel. Construct via
    /// <see cref="GGScaleClient.DialRealtimeAsync"/>. One reader at a time.
    /// </summary>
    public sealed class RealtimeClient : IAsyncDisposable
    {
        private readonly ISocketAdapter _adapter;

        internal RealtimeClient(ISocketAdapter adapter) => _adapter = adapter;

        /// <summary>
        /// Waits for the next server push. Returns null once the connection
        /// is closed or dropped.
        /// </summary>
        public async Task<RealtimeMessage?> ReadMessageAsync(CancellationToken cancellationToken = default)
        {
            var raw = await _adapter.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (raw == null)
            {
                return null;
            }
            var v = JsonValue.Parse(raw);
            return new RealtimeMessage(v.OptString("type") ?? string.Empty, v.Opt("payload") ?? JsonValue.Null);
        }

        /// <summary>Cleanly closes the connection.</summary>
        public Task CloseAsync() => _adapter.CloseAsync();

        /// <inheritdoc />
        public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
    }
}
