using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>Authenticates via POST /v1/auth/login (email + password).</summary>
    public sealed class EmailPasswordAuth : IAuthenticator
    {
        private readonly ITransport _transport;
        private readonly string _apiKey;
        private readonly string _email;
        private readonly string _password;

        /// <summary>Creates an authenticator exchanging credentials for a session.</summary>
        public EmailPasswordAuth(ITransport transport, string apiKey, string email, string password)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _apiKey = apiKey;
            _email = email;
            _password = password;
        }

        /// <inheritdoc />
        public async Task<Session> AuthenticateAsync(CancellationToken cancellationToken)
        {
            var body = JsonValue.NewObject()
                .Set("email", JsonValue.Of(_email))
                .Set("password", JsonValue.Of(_password));
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/login",
                ApiKey = _apiKey,
                Body = body,
            }, cancellationToken).ConfigureAwait(false);
            return Session.FromJson(resp);
        }
    }

    /// <summary>
    /// Authenticates via POST /v1/auth/custom-token. The token is an
    /// HS256-signed JWT minted by the tenant carrying an external_id
    /// claim; ggscale verifies it and issues its own session.
    /// </summary>
    public sealed class CustomTokenAuth : IAuthenticator
    {
        private readonly ITransport _transport;
        private readonly string _apiKey;
        private readonly string _token;

        /// <summary>Creates an authenticator exchanging a tenant-signed JWT for a session.</summary>
        public CustomTokenAuth(ITransport transport, string apiKey, string signedToken)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _apiKey = apiKey;
            _token = signedToken;
        }

        /// <inheritdoc />
        public async Task<Session> AuthenticateAsync(CancellationToken cancellationToken)
        {
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/custom-token",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("token", JsonValue.Of(_token)),
            }, cancellationToken).ConfigureAwait(false);
            return Session.FromJson(resp);
        }
    }

    /// <summary>
    /// Authenticates via POST /v1/auth/anonymous. The server creates a
    /// player with a random external_id on first call. With a session
    /// store, the session persists across runs so the same game binary
    /// resumes the same identity. Wire the client's OnSessionUpdate to
    /// the store's Save so rotated refresh tokens are re-persisted.
    /// </summary>
    public sealed class AnonymousAuth : IAuthenticator
    {
        private readonly ITransport _transport;
        private readonly string _apiKey;
        private readonly ISessionStore? _store;

        /// <summary>Creates an anonymous authenticator; pass a null store for ephemeral use.</summary>
        public AnonymousAuth(ITransport transport, string apiKey, ISessionStore? store = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _apiKey = apiKey;
            _store = store;
        }

        /// <inheritdoc />
        public async Task<Session> AuthenticateAsync(CancellationToken cancellationToken)
        {
            var persisted = _store?.Load();
            if (persisted != null)
            {
                return persisted;
            }
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/anonymous",
                ApiKey = _apiKey,
            }, cancellationToken).ConfigureAwait(false);
            var session = Session.FromJson(resp);
            _store?.Save(session);
            return session;
        }
    }

    /// <summary>
    /// Returns a synthetic local session and never calls the API. For
    /// LAN parties and self-hosted installs without a central directory.
    /// The PlayerId is a per-process random positive int64.
    /// </summary>
    public sealed class OfflineAuth : IAuthenticator
    {
        private readonly Session _session;

        /// <summary>Creates the synthetic identity for this instance.</summary>
        public OfflineAuth()
        {
            var idBytes = new byte[8];
            var tokenBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(idBytes);
                rng.GetBytes(tokenBytes);
            }
            var id = (long)(BitConverter.ToUInt64(idBytes, 0) & 0x7fffffffffffffff);
            _session = new Session(
                "offline-" + ToHex(tokenBytes),
                string.Empty,
                id,
                DateTimeOffset.UtcNow.AddYears(100));
        }

        /// <inheritdoc />
        public Task<Session> AuthenticateAsync(CancellationToken cancellationToken) => Task.FromResult(_session);

        private static string ToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
