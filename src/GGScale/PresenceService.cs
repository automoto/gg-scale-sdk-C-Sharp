using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// PUT /v1/presence. Reach it via <see cref="GGScaleClient.Presence"/>.
    /// </summary>
    public sealed class PresenceService
    {
        private readonly GGScaleClient _client;

        internal PresenceService(GGScaleClient client) => _client = client;

        /// <summary>
        /// Publishes the calling player's presence: a free-form status of
        /// 1–32 characters (e.g. "online", "in_match") and optionally the
        /// game session they are in. Accepted friends connected to the
        /// realtime WebSocket receive it as a "presence" message.
        /// </summary>
        public Task SetAsync(string status, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            var body = JsonValue.NewObject()
                .Set("status", JsonValue.Of(status))
                .Set("session_id", sessionId == null ? JsonValue.Null : JsonValue.Of(sessionId));
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "PUT",
                Path = "/v1/presence",
                Operation = "PUT /v1/presence",
                Body = body,
            }, cancellationToken);
        }
    }
}
