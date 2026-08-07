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
    /// Type discriminates payloads: matchmaker_matched, game_invite,
    /// presence, … The set is open — pass unknown types through.
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
        /// <summary>
        /// The close code the peer sent, once ReceiveAsync returned null.
        /// Null while connected, and null after an abnormal drop with no
        /// Close frame.
        /// </summary>
        int? CloseCode { get; }

        /// <summary>The close reason text, when the peer sent one.</summary>
        string? CloseDescription { get; }

        /// <summary>
        /// Opens (or re-opens) the connection, sending the API key and
        /// session token as headers. A rejected upgrade throws
        /// <see cref="GGScaleException"/> with Kind Handshake; Status and
        /// RetryAfter are filled when the platform exposes them, else
        /// Status is 0.
        /// </summary>
        Task ConnectAsync(Uri uri, string apiKey, string sessionToken, CancellationToken cancellationToken);

        /// <summary>
        /// Blocks until a complete text message arrives. Returns null when
        /// the peer closed or dropped the connection (see
        /// <see cref="CloseCode"/>). Throws <see cref="GGScaleException"/>
        /// with Kind Decode when a message exceeds the inbound size cap.
        /// </summary>
        Task<string?> ReceiveAsync(CancellationToken cancellationToken);

        /// <summary>Closes the connection; safe to call more than once.</summary>
        Task CloseAsync();
    }

    /// <summary>Default <see cref="ISocketAdapter"/> over ClientWebSocket.</summary>
    public sealed class WebSocketAdapter : ISocketAdapter
    {
        private readonly int _maxInboundMessageBytes;
        private ClientWebSocket? _socket;

        /// <summary>Creates an adapter with the default 1 MiB inbound message cap.</summary>
        public WebSocketAdapter()
            : this(1024 * 1024)
        {
        }

        /// <summary>Creates an adapter capping inbound messages at <paramref name="maxInboundMessageBytes"/>.</summary>
        public WebSocketAdapter(int maxInboundMessageBytes)
        {
            if (maxInboundMessageBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxInboundMessageBytes));
            }
            _maxInboundMessageBytes = maxInboundMessageBytes;
        }

        /// <inheritdoc />
        public int? CloseCode { get; private set; }

        /// <inheritdoc />
        public string? CloseDescription { get; private set; }

        /// <inheritdoc />
        public async Task ConnectAsync(Uri uri, string apiKey, string sessionToken, CancellationToken cancellationToken)
        {
            CloseCode = null;
            CloseDescription = null;
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
            socket.Options.SetRequestHeader("X-Session-Token", sessionToken);
#if NET8_0_OR_GREATER
            socket.Options.CollectHttpResponseDetails = true;
#endif
            try
            {
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                var code = GGScaleException.HasCertificateFailure(ex)
                    ? GGScaleException.CertificateErrorCode
                    : "ws_handshake_failed";
                var handshake = new GGScaleException(GGFailureKind.Handshake, code, ex.Message, ex);
#if NET8_0_OR_GREATER
                // netstandard2.1 cannot observe the upgrade status; net8 can.
                if (socket.HttpStatusCode != 0)
                {
                    handshake.Status = (int)socket.HttpStatusCode;
                }
                var retryAfter = socket.HttpResponseHeaders != null &&
                    socket.HttpResponseHeaders.TryGetValue("Retry-After", out var values)
                        ? System.Linq.Enumerable.FirstOrDefault(values)
                        : null;
                if (retryAfter != null && int.TryParse(retryAfter, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var secs))
                {
                    handshake.RetryAfter = TimeSpan.FromSeconds(secs);
                }
#endif
                socket.Dispose();
                throw handshake;
            }
            var previous = Interlocked.Exchange(ref _socket, socket);
            previous?.Dispose();
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
                    CloseCode = socket.CloseStatus != null ? (int)socket.CloseStatus.Value : (int?)null;
                    CloseDescription = socket.CloseStatusDescription;
                    return null;
                }
                if (assembled.Length + result.Count > _maxInboundMessageBytes)
                {
                    throw new GGScaleException(
                        GGFailureKind.Decode,
                        "ws_message_too_large",
                        FormattableString.Invariant($"inbound message exceeds the {_maxInboundMessageBytes}-byte cap"));
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

    /// <summary>The lifecycle states a <see cref="RealtimeClient"/> reports.</summary>
    public enum RealtimeState
    {
        /// <summary>The connection is up and messages flow.</summary>
        Connected = 0,

        /// <summary>The connection dropped; a jittered reconnect is pending.</summary>
        Reconnecting = 1,

        /// <summary>Messages were dropped (queue overflow) — resync REST state.</summary>
        Degraded = 2,

        /// <summary>Terminal: closed by the caller, the server, or a non-retryable failure.</summary>
        Closed = 3,
    }

    /// <summary>One state transition of a <see cref="RealtimeClient"/>.</summary>
    public sealed class RealtimeStateChange
    {
        internal RealtimeStateChange(RealtimeState state, bool isReconnect, int attempt, int? closeCode, TimeSpan? retryDelay, int droppedMessages, Exception? error)
        {
            State = state;
            IsReconnect = isReconnect;
            Attempt = attempt;
            CloseCode = closeCode;
            RetryDelay = retryDelay;
            DroppedMessages = droppedMessages;
            Error = error;
        }

        /// <summary>The new state.</summary>
        public RealtimeState State { get; }

        /// <summary>
        /// True on a Connected transition after an outage. Delivery is
        /// best-effort: re-read authoritative REST state (pending invites,
        /// active matchmaker tickets, friend presence) when you see this.
        /// </summary>
        public bool IsReconnect { get; }

        /// <summary>The reconnect attempt number, when reconnecting.</summary>
        public int Attempt { get; }

        /// <summary>The close code that ended the previous connection, when known.</summary>
        public int? CloseCode { get; }

        /// <summary>The wait before the next reconnect attempt, when reconnecting.</summary>
        public TimeSpan? RetryDelay { get; }

        /// <summary>How many queued messages have been dropped in total.</summary>
        public int DroppedMessages { get; }

        /// <summary>The failure that ended the connection, when one did.</summary>
        public Exception? Error { get; }
    }

    /// <summary>Configuration for <see cref="RealtimeClient"/>.</summary>
    public sealed class RealtimeOptions
    {
        /// <summary>Reconnect automatically after retryable drops. Default true.</summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>
        /// Buffered message cap. When the caller reads too slowly the
        /// oldest message is dropped and a Degraded state change fires.
        /// Default 256.
        /// </summary>
        public int QueueCapacity { get; set; } = 256;

        /// <summary>
        /// Inbound message size cap for the default adapter; matches the
        /// server's 1 MiB limit.
        /// </summary>
        public int MaxInboundMessageBytes { get; set; } = 1024 * 1024;

        /// <summary>The first reconnect waits random(0, this). Default 5 s.</summary>
        public TimeSpan FirstReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Upper bound on one reconnect backoff wait. Default 30 s.</summary>
        public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The backoff attempt counter resets only after a connection
        /// stayed up this long. Default 30 s.
        /// </summary>
        public TimeSpan StableConnectionThreshold { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Cap on one outage's total reconnect time; null (the default)
        /// keeps trying until CloseAsync.
        /// </summary>
        public TimeSpan? ReconnectTimeout { get; set; }
    }

    /// <summary>
    /// A managed connection to the server's /v1/ws push channel. Construct
    /// via <see cref="GGScaleClient.DialRealtimeAsync(RealtimeOptions?, ISocketAdapter?, System.Threading.CancellationToken)"/>.
    /// A background loop keeps a receive pending (so server pings are
    /// answered), buffers messages in a bounded queue, and reconnects with
    /// jittered backoff after retryable drops — refreshing the session
    /// token first. One ReadMessageAsync reader at a time.
    /// </summary>
    public sealed class RealtimeClient : IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task> _connect;
        private readonly ISocketAdapter _adapter;
        private readonly RealtimeOptions _options;
        private readonly IGGClock _clock;
        private readonly IGGScaleLogger? _logger;
        private readonly AsyncBoundedQueue<RealtimeMessage> _queue;
        private readonly CancellationTokenSource _lifecycle = new CancellationTokenSource();
        private readonly object _rngLock = new object();
        private readonly Random _rng = new Random();
        private readonly object _closeLock = new object();
        private Task _loop = Task.CompletedTask;
        private Task? _closeTask;
        private int _droppedTotal;
        private int _closed;

        internal RealtimeClient(Func<CancellationToken, Task> connect, ISocketAdapter adapter, RealtimeOptions options, IGGClock clock, IGGScaleLogger? logger)
        {
            _connect = connect;
            _adapter = adapter;
            _options = options;
            _clock = clock;
            _logger = logger;
            _queue = new AsyncBoundedQueue<RealtimeMessage>(options.QueueCapacity);
        }

        /// <summary>
        /// Fires on lifecycle transitions. A Connected change with
        /// IsReconnect true is the resync signal. Handlers run on the read
        /// loop — keep them fast and never block.
        /// </summary>
        public event EventHandler<RealtimeStateChange>? StateChanged;

        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            await _connect(cancellationToken).ConfigureAwait(false);
            EmitWs("connected");
            _loop = Task.Run(() => RunAsync(_lifecycle.Token), CancellationToken.None);
        }

        /// <summary>
        /// Waits for the next server push from the buffered queue. Returns
        /// null only once the connection is terminally closed.
        /// </summary>
        public Task<RealtimeMessage?> ReadMessageAsync(CancellationToken cancellationToken = default) =>
            _queue.ReadAsync(cancellationToken);

        /// <summary>
        /// Cleanly closes the connection: no further reconnects, a Close
        /// frame is sent, and pending readers drain then get null. Safe to
        /// call any number of times; repeated calls await the same close.
        /// </summary>
        public Task CloseAsync()
        {
            lock (_closeLock)
            {
                _closeTask ??= CloseCoreAsync();
                return _closeTask;
            }
        }

        private async Task CloseCoreAsync()
        {
            _lifecycle.Cancel();
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            await _adapter.CloseAsync().ConfigureAwait(false);
            _queue.Complete();
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Notify(new RealtimeStateChange(RealtimeState.Closed, false, 0, _adapter.CloseCode, null, _droppedTotal, null));
                EmitWs("closed", closeCode: _adapter.CloseCode);
            }
            _lifecycle.Dispose();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

        private async Task RunAsync(CancellationToken ct)
        {
            var attempt = 0;
            var connectedAt = _clock.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                string? raw;
                try
                {
                    raw = await _adapter.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (GGScaleException ex) when (ex.Kind == GGFailureKind.Decode)
                {
                    // Oversized or corrupt framing: drop the connection and
                    // recover through the reconnect path.
                    EmitWs("message_too_large");
                    await _adapter.CloseAsync().ConfigureAwait(false);
                    raw = null;
                }

                if (raw == null)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    var closeCode = _adapter.CloseCode;
                    var duration = _clock.UtcNow - connectedAt;
                    EmitWs("disconnected", closeCode: closeCode, connectionDuration: duration);
                    if (!_options.AutoReconnect || !ShouldReconnect(closeCode))
                    {
                        Terminal(closeCode, null);
                        return;
                    }
                    if (duration >= _options.StableConnectionThreshold)
                    {
                        attempt = 0;
                    }
                    var outageStart = _clock.UtcNow;
                    TimeSpan? retryAfterHint = null;
                    while (true)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }
                        attempt++;
                        var delay = attempt == 1
                            ? TimeSpan.FromTicks((long)(_options.FirstReconnectMaxDelay.Ticks * NextUnitRandom()))
                            : Backoff.FullJitter(NextUnitRandom(), TimeSpan.FromMilliseconds(250), _options.MaxReconnectDelay, attempt);
                        if (retryAfterHint != null && retryAfterHint.Value > delay)
                        {
                            delay = retryAfterHint.Value;
                        }
                        retryAfterHint = null;
                        if (_options.ReconnectTimeout != null &&
                            _clock.UtcNow + delay - outageStart > _options.ReconnectTimeout.Value)
                        {
                            Terminal(closeCode, null);
                            return;
                        }
                        Notify(new RealtimeStateChange(RealtimeState.Reconnecting, true, attempt, closeCode, delay, _droppedTotal, null));
                        EmitWs("reconnecting", closeCode: closeCode, attempt: attempt, delay: delay);
                        try
                        {
                            await _clock.DelayAsync(delay, ct).ConfigureAwait(false);
                            if (_options.ReconnectTimeout == null)
                            {
                                await _connect(ct).ConfigureAwait(false);
                            }
                            else
                            {
                                // The connect attempt itself is bounded by the
                                // remaining outage budget; a hung or late
                                // handshake cannot outlive ReconnectTimeout.
                                var remaining = outageStart + _options.ReconnectTimeout.Value - _clock.UtcNow;
                                if (remaining <= TimeSpan.Zero)
                                {
                                    Terminal(closeCode, ReconnectTimeoutError(null));
                                    return;
                                }
                                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                if (remaining.TotalMilliseconds < int.MaxValue)
                                {
                                    bounded.CancelAfter(remaining);
                                }
                                try
                                {
                                    await _connect(bounded.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                                {
                                    Terminal(closeCode, ReconnectTimeoutError(ex));
                                    return;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (GGScaleException ex)
                        {
                            EmitWs("handshake_failed", handshakeStatus: ex.Status, attempt: attempt);
                            if (!RetryableHandshake(ex))
                            {
                                Terminal(closeCode, ex);
                                return;
                            }
                            retryAfterHint = ex.RetryAfter;
                            continue;
                        }
                        catch (Exception ex)
                        {
                            // e.g. the session was cleared mid-outage.
                            Terminal(closeCode, ex);
                            return;
                        }
                        break;
                    }
                    connectedAt = _clock.UtcNow;
                    Notify(new RealtimeStateChange(RealtimeState.Connected, true, attempt, closeCode, null, _droppedTotal, null));
                    EmitWs("connected", attempt: attempt);
                    continue;
                }

                RealtimeMessage msg;
                try
                {
                    var v = JsonValue.Parse(raw);
                    msg = new RealtimeMessage(v.OptString("type") ?? string.Empty, v.Opt("payload") ?? JsonValue.Null);
                }
                catch (FormatException)
                {
                    EmitWs("malformed_frame");
                    continue;
                }
                _queue.TryWrite(msg, out var dropped);
                if (dropped != null)
                {
                    var total = Interlocked.Increment(ref _droppedTotal);
                    Notify(new RealtimeStateChange(RealtimeState.Degraded, false, 0, null, null, total, null));
                    EmitWs("message_dropped");
                }
            }
        }

        private static GGScaleException ReconnectTimeoutError(Exception? inner) =>
            new GGScaleException(GGFailureKind.Timeout, "ws_reconnect_timeout",
                "the reconnect budget (ReconnectTimeout) was exhausted", inner);

        private void Terminal(int? closeCode, Exception? error)
        {
            _queue.Complete();
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Notify(new RealtimeStateChange(RealtimeState.Closed, false, 0, closeCode, null, _droppedTotal, error));
                EmitWs("closed", closeCode: closeCode);
            }
        }

        /// <summary>
        /// Reconnect only after abnormal closure (no close frame / 1006),
        /// going-away (1001), or server-side transient failures
        /// (1011–1013). Normal closure, policy, protocol, and application
        /// (4xxx) closes are terminal.
        /// </summary>
        private static bool ShouldReconnect(int? closeCode)
        {
            if (closeCode == null || closeCode == 1006)
            {
                return true;
            }
            return closeCode == 1001 || (closeCode >= 1011 && closeCode <= 1013);
        }

        private static bool RetryableHandshake(GGScaleException ex)
        {
            // A failed pre-dial session refresh must never be replayed
            // automatically: an ambiguous refresh may already have consumed
            // the rotating token. Certificate failures cannot be repaired
            // by retrying.
            if (ex.Code == GGScaleException.SessionRefreshFailedCode ||
                ex.Code == GGScaleException.CertificateErrorCode)
            {
                return false;
            }
            if (ex.Kind == GGFailureKind.Connection || ex.Kind == GGFailureKind.Timeout)
            {
                return true;
            }
            if (ex.Kind != GGFailureKind.Handshake)
            {
                return false;
            }
            // Status 0 = platform hid the upgrade status; assume transient.
            return ex.Status == 0 || ex.Status == 408 || ex.Status == 429 ||
                ex.Status == 502 || ex.Status == 503 || ex.Status == 504;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Backoff jitter is not security-sensitive.")]
        private double NextUnitRandom()
        {
            lock (_rngLock)
            {
                return _rng.NextDouble();
            }
        }

        private void Notify(RealtimeStateChange change)
        {
            try
            {
                StateChanged?.Invoke(this, change);
            }
            catch (Exception)
            {
                // Subscriber faults must not break the read loop.
            }
        }

        private void EmitWs(string eventName, int? closeCode = null, int? handshakeStatus = null, int attempt = 0, TimeSpan? delay = null, TimeSpan? connectionDuration = null)
        {
            if (_logger == null)
            {
                return;
            }
            try
            {
                _logger.OnWsEvent(new GGWsEventRecord(eventName, closeCode, handshakeStatus, attempt, delay, connectionDuration, _droppedTotal));
            }
            catch (Exception)
            {
                // Observability hooks must never break the loop.
            }
        }
    }
}
