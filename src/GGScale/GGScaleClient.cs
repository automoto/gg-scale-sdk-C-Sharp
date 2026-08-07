using System;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>Configuration for <see cref="GGScaleClient"/>.</summary>
    public sealed class GGScaleClientOptions
    {
        /// <summary>
        /// The ggscale server base URL (no trailing slash), e.g.
        /// "http://localhost:8080". Required when Transport is null; also
        /// used to derive the realtime WebSocket URL.
        /// </summary>
        public string? BaseUrl { get; set; }

        /// <summary>The tenant API key. Required.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Overrides the default JSON-over-HTTP transport.</summary>
        public ITransport? Transport { get; set; }

        /// <summary>
        /// Called whenever the client installs or rotates a session — after
        /// LoginAsync/SetSession and after each automatic refresh. Useful
        /// for persisting sessions across restarts.
        /// </summary>
        public Action<Session?>? OnSessionUpdate { get; set; }

        /// <summary>
        /// User-Agent header override. Null uses
        /// "ggscale-csharp/&lt;sdk-version&gt;". Ignored when
        /// <see cref="Transport"/> is supplied.
        /// </summary>
        public string? UserAgent { get; set; }

        /// <summary>
        /// Per-attempt HTTP timeout. Null uses 30 seconds. Ignored when
        /// <see cref="Transport"/> is supplied.
        /// </summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>
        /// Deadline for one logical call including every retry attempt and
        /// backoff wait. Null uses 100 seconds.
        /// </summary>
        public TimeSpan? OverallTimeout { get; set; }

        /// <summary>
        /// Maximum response body size in bytes. Null uses 4 MiB. Ignored
        /// when <see cref="Transport"/> is supplied.
        /// </summary>
        public long? MaxResponseBytes { get; set; }

        /// <summary>
        /// Structured observability hook. Null (the default) keeps the SDK
        /// silent.
        /// </summary>
        public IGGScaleLogger? Logger { get; set; }

        /// <summary>Retry policy override. Null uses the defaults (3 attempts, full jitter).</summary>
        public GGRetryPolicy? Retry { get; set; }

        /// <summary>Deterministic time source for tests.</summary>
        internal IGGClock? Clock { get; set; }
    }

    /// <summary>
    /// Entry point for the ggscale SDK. Construct one, optionally call
    /// <see cref="LoginAsync"/> to establish a player session, and use the
    /// service properties to call the API. Safe for concurrent use.
    /// Sessions refresh proactively near expiry, and a 401 triggers exactly
    /// one reactive refresh + retry.
    /// </summary>
    public sealed class GGScaleClient : IDisposable
    {
        private static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(30);

        private readonly object _sessionLock = new object();
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private readonly Action<Session?>? _onSessionUpdate;
        private readonly bool _ownsTransport;
        private readonly IGGClock _clock;
        private readonly ITransport _rawTransport;
        private readonly RetryingTransport _retrying;
        private readonly IGGScaleLogger? _logger;
        private Session? _session;

        /// <summary>Creates a client. Throws ArgumentException when options are incomplete.</summary>
        public GGScaleClient(GGScaleClientOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (string.IsNullOrEmpty(options.ApiKey))
            {
                throw new ArgumentException("ApiKey is required", nameof(options));
            }
            _clock = options.Clock ?? SystemClock.Instance;
            var transport = options.Transport;
            if (transport == null)
            {
                if (string.IsNullOrEmpty(options.BaseUrl))
                {
                    throw new ArgumentException("either Transport or BaseUrl is required", nameof(options));
                }
                var transportOptions = new HttpTransportOptions { UserAgent = options.UserAgent };
                if (options.Timeout != null)
                {
                    transportOptions.Timeout = options.Timeout.Value;
                }
                if (options.MaxResponseBytes != null)
                {
                    transportOptions.MaxResponseBytes = options.MaxResponseBytes.Value;
                }
                transport = new HttpTransport(options.BaseUrl!, null, transportOptions, _clock);
                _ownsTransport = true;
            }

            _rawTransport = transport;
            _retrying = new RetryingTransport(
                transport,
                options.Retry ?? new GGRetryPolicy(),
                options.OverallTimeout ?? TimeSpan.FromSeconds(100),
                _clock,
                options.Logger);
            Transport = _retrying;
            ApiKey = options.ApiKey!;
            BaseUrl = options.BaseUrl;
            _onSessionUpdate = options.OnSessionUpdate;
            _logger = options.Logger;

            Auth = new AuthService(Transport, ApiKey, this);
            Config = new ConfigService(Transport, ApiKey);
            Storage = new StorageService(this);
            Leaderboards = new LeaderboardsService(this);
            Profile = new ProfileService(this);
            Matchmaker = new MatchmakerService(this);
            Relay = new RelayService(this);
            Fleets = new FleetsService(this);
            Friends = new FriendsService(this);
            GameSessions = new GameSessionsService(this);
            Invites = new InvitesService(this);
            Players = new PlayersService(this);
            Presence = new PresenceService(this);
            Account = new AccountService(this);
            Server = new ServerService(Transport, ApiKey);
        }

        /// <summary>Auth operations that are not login strategies (signup, verify, refresh, logout).</summary>
        public AuthService Auth { get; }

        /// <summary>Project remote configuration (readable before login).</summary>
        public ConfigService Config { get; }

        /// <summary>Per-player JSON storage.</summary>
        public StorageService Storage { get; }

        /// <summary>Leaderboards.</summary>
        public LeaderboardsService Leaderboards { get; }

        /// <summary>The calling player's profile.</summary>
        public ProfileService Profile { get; }

        /// <summary>Matchmaking tickets.</summary>
        public MatchmakerService Matchmaker { get; }

        /// <summary>TURN relay credentials.</summary>
        public RelayService Relay { get; }

        /// <summary>Server browser + game-server heartbeats.</summary>
        public FleetsService Fleets { get; }

        /// <summary>Friends and blocks.</summary>
        public FriendsService Friends { get; }

        /// <summary>Player-hosted game sessions.</summary>
        public GameSessionsService GameSessions { get; }

        /// <summary>Game-session invites.</summary>
        public InvitesService Invites { get; }

        /// <summary>Public player directory (by id or friend code).</summary>
        public PlayersService Players { get; }

        /// <summary>Player presence.</summary>
        public PresenceService Presence { get; }

        /// <summary>The calling player's account (remote addresses).</summary>
        public AccountService Account { get; }

        /// <summary>
        /// Server-tier endpoints (player session verification, player
        /// remote addresses) for game-server workloads. Authenticates with
        /// the secret API key only — no player session required.
        /// </summary>
        public ServerService Server { get; }

        /// <summary>
        /// The transport every service call goes through, including the
        /// SDK retry layer. Use it to build authenticators so they share
        /// the same retry and telemetry behavior.
        /// </summary>
        public ITransport Transport { get; }

        internal string ApiKey { get; }

        internal string? BaseUrl { get; }

        /// <summary>The current session, or null when none is installed.</summary>
        public Session? Session
        {
            get
            {
                lock (_sessionLock)
                {
                    return _session;
                }
            }
        }

        /// <summary>
        /// Establishes a session by running the authenticator. Subsequent
        /// protected calls use the resulting session automatically.
        /// </summary>
        public async Task LoginAsync(IAuthenticator authenticator, CancellationToken cancellationToken = default)
        {
            if (authenticator == null)
            {
                throw new ArgumentNullException(nameof(authenticator));
            }
            var session = await authenticator.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            SetSession(session);
        }

        /// <summary>
        /// Installs a session captured earlier (e.g. from persistence).
        /// Pass null to clear. Fires OnSessionUpdate.
        /// </summary>
        public void SetSession(Session? session)
        {
            lock (_sessionLock)
            {
                _session = session;
            }
            _onSessionUpdate?.Invoke(session);
        }

        /// <summary>
        /// Sends a request that requires a player session: attaches the API
        /// key and session token, refreshes proactively inside the 30 s
        /// window, and retries once after a 401-triggered refresh. The
        /// completion telemetry record spans the whole flow: the initial
        /// 401 is deferred while the refresh runs, so one logical call
        /// emits exactly one record.
        /// </summary>
        internal async Task<JsonValue> CallProtectedAsync(GGRequest request, CancellationToken cancellationToken)
        {
            await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            AttachSession(request);
            request.RetryOn401Pending = true;
            try
            {
                try
                {
                    var resp = await Transport.CallAsync(request, cancellationToken).ConfigureAwait(false);
                    return resp.Value;
                }
                catch (GGScaleException ex) when (ex.Status == 401 && request.RetryOn401Pending)
                {
                    request.RetryOn401Pending = false;
                    try
                    {
                        await RefreshLockedAsync(force: true, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _retrying.EmitDeferredCompletion(request, 0, GGFailureKind.Canceled, null);
                        throw;
                    }
                    catch (Exception)
                    {
                        _retrying.EmitDeferredCompletion(request, ex.Status, ex.Kind, ex.Code);
                        throw ex; // surface the original 401
                    }
                    AttachSession(request);
                    var retried = await Transport.CallAsync(request, cancellationToken).ConfigureAwait(false);
                    return retried.Value;
                }
            }
            finally
            {
                request.RetryOn401Pending = false;
            }
        }

        internal Session RequireSession()
        {
            lock (_sessionLock)
            {
                return _session ?? throw new InvalidOperationException("ggscale: no session — call LoginAsync or SetSession first");
            }
        }

        private void AttachSession(GGRequest request)
        {
            var session = RequireSession();
            request.ApiKey = ApiKey;
            request.SessionToken = session.AccessToken;
        }

        internal Task RefreshIfNeededAsync(CancellationToken cancellationToken)
        {
            Session? s;
            lock (_sessionLock)
            {
                s = _session;
            }
            if (s == null || s.RefreshToken.Length == 0 || s.ExpiresAt - _clock.UtcNow >= RefreshWindow)
            {
                return Task.CompletedTask;
            }
            return RefreshLockedAsync(force: false, cancellationToken);
        }

        /// <summary>
        /// Refreshes under a lock so concurrent callers refresh exactly once
        /// per expiry boundary: the slow path re-checks the window after
        /// acquiring the lock. force bypasses the window check (post-401).
        /// </summary>
        private async Task RefreshLockedAsync(bool force, CancellationToken cancellationToken)
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Session? s;
                lock (_sessionLock)
                {
                    s = _session;
                }
                if (s == null || s.RefreshToken.Length == 0)
                {
                    if (force)
                    {
                        throw new InvalidOperationException("ggscale: cannot refresh — no refresh token");
                    }
                    return;
                }
                if (!force && s.ExpiresAt - _clock.UtcNow >= RefreshWindow)
                {
                    return;
                }
                var fresh = await Auth.RefreshAsync(s.RefreshToken, cancellationToken).ConfigureAwait(false);
                lock (_sessionLock)
                {
                    _session = fresh;
                }
                _onSessionUpdate?.Invoke(fresh);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>
        /// Opens a managed WebSocket connection to /v1/ws with default
        /// options. See the options overload for behavior.
        /// </summary>
        public Task<RealtimeClient> DialRealtimeAsync(ISocketAdapter? adapter = null, CancellationToken cancellationToken = default) =>
            DialRealtimeAsync(null, adapter, cancellationToken);

        /// <summary>
        /// Opens a managed WebSocket connection to /v1/ws carrying the API
        /// key and current session token. Requires a player session and a
        /// BaseUrl. The session is refreshed before every (re)connect. The
        /// returned client keeps a continuous read loop (server pings stay
        /// answered), buffers messages in a bounded queue, and — per
        /// options — reconnects with jittered backoff after retryable
        /// drops. A 401 on the initial upgrade is not auto-retried.
        /// </summary>
        public async Task<RealtimeClient> DialRealtimeAsync(RealtimeOptions? options, ISocketAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(BaseUrl))
            {
                throw new InvalidOperationException("ggscale: cannot determine WebSocket URL — set Options.BaseUrl");
            }
            var realtimeOptions = options ?? new RealtimeOptions();

            var wsUrl = BaseUrl!;
            if (wsUrl.StartsWith("https://", StringComparison.Ordinal))
            {
                wsUrl = ReplaceScheme(wsUrl, "https://", "wss://");
            }
            else if (wsUrl.StartsWith("http://", StringComparison.Ordinal))
            {
                wsUrl = ReplaceScheme(wsUrl, "http://", "ws://");
            }
            wsUrl += "/v1/ws";
            var uri = new Uri(wsUrl);

            var socket = adapter ?? new WebSocketAdapter(realtimeOptions.MaxInboundMessageBytes);
            async Task Connect(CancellationToken ct)
            {
                try
                {
                    await RefreshIfNeededAsync(ct).ConfigureAwait(false);
                }
                catch (GGScaleException ex)
                {
                    // Mark refresh failures so the reconnect loop never
                    // replays an ambiguous refresh (the rotating token may
                    // already be consumed server-side).
                    throw new GGScaleException(ex.Kind, GGScaleException.SessionRefreshFailedCode,
                        "session refresh before the WebSocket dial failed", ex);
                }
                var session = RequireSession();
                await socket.ConnectAsync(uri, ApiKey, session.AccessToken, ct).ConfigureAwait(false);
            }

            var client = new RealtimeClient(Connect, socket, realtimeOptions, _clock, _logger);
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }

        private static string ReplaceScheme(string url, string prefix, string replacement)
        {
#if NET8_0_OR_GREATER
            return string.Concat(replacement, url.AsSpan(prefix.Length));
#else
            return replacement + url.Substring(prefix.Length);
#endif
        }

        /// <summary>Releases the refresh lock and any transport the client created itself.</summary>
        public void Dispose()
        {
            _refreshLock.Dispose();
            if (_ownsTransport && _rawTransport is IDisposable owned)
            {
                owned.Dispose();
            }
        }
    }
}
