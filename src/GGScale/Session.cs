using System;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// The result of a successful authentication. AccessToken is sent on
    /// protected requests as X-Session-Token; RefreshToken mints a new
    /// AccessToken when the old one nears expiry (empty for OfflineAuth,
    /// which never refreshes). Immutable.
    /// </summary>
    public sealed class Session
    {
        /// <summary>Creates a session (normally done by an authenticator).</summary>
        public Session(string accessToken, string refreshToken, long playerId, DateTimeOffset expiresAt)
        {
            AccessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
            RefreshToken = refreshToken ?? throw new ArgumentNullException(nameof(refreshToken));
            PlayerId = playerId;
            ExpiresAt = expiresAt;
        }

        /// <summary>The session JWT sent as X-Session-Token.</summary>
        public string AccessToken { get; }

        /// <summary>The rotating refresh token; empty when the session cannot refresh.</summary>
        public string RefreshToken { get; }

        /// <summary>The authenticated player's id in this project.</summary>
        public long PlayerId { get; }

        /// <summary>When the access token expires.</summary>
        public DateTimeOffset ExpiresAt { get; }

        internal static Session FromJson(JsonValue v) =>
            new Session(
                v.OptString("access_token") ?? string.Empty,
                v.OptString("refresh_token") ?? string.Empty,
                v.OptLong("player_id"),
                v.OptTime("expires_at") ?? DateTimeOffset.MinValue);
    }

    /// <summary>
    /// Establishes a session with ggscale. Implementations either call the
    /// /v1/auth endpoints (EmailPasswordAuth, CustomTokenAuth,
    /// AnonymousAuth) or mint a synthetic local session (OfflineAuth).
    /// </summary>
    public interface IAuthenticator
    {
        /// <summary>Performs the authentication flow and returns the session.</summary>
        Task<Session> AuthenticateAsync(CancellationToken cancellationToken);
    }
}
