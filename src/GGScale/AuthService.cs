using System;
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
    /// signup, verification, refresh, logout, account linking, password
    /// management, and self-disable. Reach it via
    /// <see cref="GGScaleClient.Auth"/>.
    /// </summary>
    public sealed class AuthService
    {
        private readonly ITransport _transport;
        private readonly string _apiKey;
        private readonly GGScaleClient? _client;

        internal AuthService(ITransport transport, string apiKey, GGScaleClient? client = null)
        {
            _transport = transport;
            _apiKey = apiKey;
            _client = client;
        }

        private GGScaleClient Client =>
            _client ?? throw new InvalidOperationException("ggscale: this operation requires the full client");

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
                Operation = "POST /v1/auth/signup",
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
                Operation = "POST /v1/auth/verify",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject()
                    .Set("email", JsonValue.Of(email))
                    .Set("code", JsonValue.Of(code)),
            }, cancellationToken).ConfigureAwait(false);
            return new VerifyResult(resp.Value.OptLong("player_id"), resp.Value.OptBool("verified"));
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
                Operation = "POST /v1/auth/refresh",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("refresh_token", JsonValue.Of(refreshToken)),
            }, cancellationToken).ConfigureAwait(false);
            return Session.FromJson(resp.Value);
        }

        /// <summary>Revokes the given refresh token.</summary>
        public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            return _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/logout",
                Operation = "POST /v1/auth/logout",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("refresh_token", JsonValue.Of(refreshToken)),
            }, cancellationToken);
        }

        /// <summary>
        /// Attaches email/password credentials to the current (anonymous)
        /// player so the identity survives device changes. The server
        /// mails a verification code and answers 202. IsConflict when the
        /// player already has credentials or the email is taken.
        /// Requires a player session.
        /// </summary>
        public Task LinkEmailAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            return Client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/link",
                Operation = "POST /v1/auth/link",
                Body = JsonValue.NewObject()
                    .Set("email", JsonValue.Of(email))
                    .Set("password", JsonValue.Of(password)),
            }, cancellationToken);
        }

        /// <summary>
        /// Attaches a Steam identity (hex-encoded session ticket) to the
        /// current player. IsConflict when the player's identity cannot be
        /// replaced or the Steam account is already linked elsewhere.
        /// Requires a player session.
        /// </summary>
        public Task LinkSteamAsync(string ticket, CancellationToken cancellationToken = default)
        {
            return Client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/link/steam",
                Operation = "POST /v1/auth/link/steam",
                Body = JsonValue.NewObject().Set("ticket", JsonValue.Of(ticket)),
            }, cancellationToken);
        }

        /// <summary>
        /// Changes the calling player's password. Requires a player
        /// session and the current password.
        /// </summary>
        public Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
        {
            return Client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/password",
                Operation = "POST /v1/auth/password",
                Body = JsonValue.NewObject()
                    .Set("current_password", JsonValue.Of(currentPassword))
                    .Set("new_password", JsonValue.Of(newPassword)),
            }, cancellationToken);
        }

        /// <summary>
        /// Disables the calling player's account and revokes every
        /// session. Irreversible from the client — only tenant support can
        /// re-enable. Requires a player session. Credentialed accounts must
        /// re-authenticate with their password (400 when omitted, 403 when
        /// wrong); passwordless players (e.g. anonymous) disable on the
        /// session alone — pass null.
        /// </summary>
        public Task DisableAsync(string? password = null, CancellationToken cancellationToken = default)
        {
            var body = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(password))
            {
                body.Set("password", JsonValue.Of(password!));
            }
            return Client.CallProtectedAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/disable",
                Operation = "POST /v1/auth/disable",
                Body = body,
            }, cancellationToken);
        }

        /// <summary>
        /// Starts an in-client password reset: the server mails a reset
        /// code and always answers 202, revealing nothing about account
        /// existence. No player session needed.
        /// </summary>
        public Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
        {
            return _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/password-reset",
                Operation = "POST /v1/auth/password-reset",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("email", JsonValue.Of(email)),
            }, cancellationToken);
        }

        /// <summary>
        /// Completes a password reset with the mailed code. No player
        /// session needed.
        /// </summary>
        public Task ConfirmPasswordResetAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default)
        {
            return _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/password-reset/confirm",
                Operation = "POST /v1/auth/password-reset/confirm",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject()
                    .Set("email", JsonValue.Of(email))
                    .Set("code", JsonValue.Of(code))
                    .Set("new_password", JsonValue.Of(newPassword)),
            }, cancellationToken);
        }

        /// <summary>
        /// Re-sends the signup verification mail; always answers 202. No
        /// player session needed.
        /// </summary>
        public Task ResendVerificationAsync(string email, CancellationToken cancellationToken = default)
        {
            return _transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/auth/verify/resend",
                Operation = "POST /v1/auth/verify/resend",
                ApiKey = _apiKey,
                Body = JsonValue.NewObject().Set("email", JsonValue.Of(email)),
            }, cancellationToken);
        }
    }
}
