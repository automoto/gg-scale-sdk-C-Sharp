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
            var transport = options.Transport;
            if (transport == null)
            {
                if (string.IsNullOrEmpty(options.BaseUrl))
                {
                    throw new ArgumentException("either Transport or BaseUrl is required", nameof(options));
                }
                transport = new HttpTransport(options.BaseUrl!);
                _ownsTransport = true;
            }

            Transport = transport;
            ApiKey = options.ApiKey!;
            BaseUrl = options.BaseUrl;
            _onSessionUpdate = options.OnSessionUpdate;

            Auth = new AuthService(transport, ApiKey);
            Storage = new StorageService(this);
            Leaderboards = new LeaderboardsService(this);
            Profile = new ProfileService(this);
            Matchmaker = new MatchmakerService(this);
            Relay = new RelayService(this);
            Fleets = new FleetsService(this);
            Friends = new FriendsService(this);
            GameSessions = new GameSessionsService(this);
            Invites = new InvitesService(this);
            Presence = new PresenceService(this);
            Account = new AccountService(this);
            Server = new ServerService(transport, ApiKey);
        }

        /// <summary>Auth operations that are not login strategies (signup, verify, refresh, logout).</summary>
        public AuthService Auth { get; }

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

        /// <summary>The underlying transport (for building authenticators or fakes).</summary>
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
        /// window, and retries once after a 401-triggered refresh.
        /// </summary>
        internal async Task<JsonValue> CallProtectedAsync(GGRequest request, CancellationToken cancellationToken)
        {
            await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            AttachSession(request);
            try
            {
                return await Transport.CallAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (GGScaleException ex) when (ex.Status == 401)
            {
                try
                {
                    await RefreshLockedAsync(force: true, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    throw ex; // surface the original 401
                }
                AttachSession(request);
                return await Transport.CallAsync(request, cancellationToken).ConfigureAwait(false);
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
            if (s == null || s.RefreshToken.Length == 0 || s.ExpiresAt - DateTimeOffset.UtcNow >= RefreshWindow)
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
                if (!force && s.ExpiresAt - DateTimeOffset.UtcNow >= RefreshWindow)
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
        /// Opens a WebSocket connection to /v1/ws carrying the API key and
        /// current session token. Requires a player session and a BaseUrl.
        /// Refreshes the session proactively before dialing; note that
        /// unlike REST calls, a 401 on the upgrade is not auto-retried.
        /// </summary>
        public async Task<RealtimeClient> DialRealtimeAsync(ISocketAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(BaseUrl))
            {
                throw new InvalidOperationException("ggscale: cannot determine WebSocket URL — set Options.BaseUrl");
            }
            await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var session = RequireSession();

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

            var socket = adapter ?? new WebSocketAdapter();
            await socket.ConnectAsync(new Uri(wsUrl), ApiKey, session.AccessToken, cancellationToken).ConfigureAwait(false);
            return new RealtimeClient(socket);
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
            if (_ownsTransport && Transport is IDisposable owned)
            {
                owned.Dispose();
            }
        }
    }
}
