using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>The result of a successful email verification.</summary>
    public sealed class VerifyResult
    {
        internal VerifyResult(long playerId, bool verified)
        {
            PlayerId = playerId;
            Verified = verified;
        }

        /// <summary>The verified player's id.</summary>
        public long PlayerId { get; }

        /// <summary>True once the email is verified.</summary>
        public bool Verified { get; }
    }

    /// <summary>
    /// The /v1/auth operations that are not authentication strategies —
    /// signup, email verification, refresh, and logout. Reach it via
    /// <see cref="GGScaleClient.Auth"/>.
    /// </summary>
    public sealed class AuthService
    {
        private readonly ITransport _transport;
        private readonly string _apiKey;

        internal AuthService(ITransport transport, string apiKey)
        {
            _transport = transport;
            _apiKey = apiKey;
        }

        /// <summary>
        /// Registers a new player. The server mails a verification code and
        /// answers 202; call <see cref="VerifyAsync"/> with that code before
        /// the player can log in.
        /// </summary>
        public Task SignupAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            return _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/signup",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject()
                    .Set("email", JsonValue.Of(email))
                    .Set("password", JsonValue.Of(password)),
            }, cancellationToken);
        }

        /// <summary>Completes email verification with the code mailed at signup.</summary>
        public async Task<VerifyResult> VerifyAsync(string email, string code, CancellationToken cancellationToken = default)
        {
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/verify",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject()
                    .Set("email", JsonValue.Of(email))
                    .Set("code", JsonValue.Of(code)),
            }, cancellationToken).ConfigureAwait(false);
            return new VerifyResult(resp.OptLong("player_id"), resp.OptBool("verified"));
        }

        /// <summary>
        /// Exchanges a refresh token for a new session. The previous refresh
        /// token is revoked server-side.
        /// </summary>
        public async Task<Session> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            var resp = await _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/refresh",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("refresh_token", JsonValue.Of(refreshToken)),
            }, cancellationToken).ConfigureAwait(false);
            return Session.FromJson(resp);
        }

        /// <summary>Revokes the given refresh token.</summary>
        public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            return _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/logout",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("refresh_token", JsonValue.Of(refreshToken)),
            }, cancellationToken);
        }
    }
}
