using System;

namespace GGScale
{
    /// <summary>
    /// Structured observability hook. The SDK is silent by default; supply
    /// an implementation via <see cref="GGScaleClientOptions.Logger"/> to
    /// receive one completion record per call plus retry and WebSocket
    /// lifecycle records. Records never contain URLs, headers, bodies, or
    /// tokens. Implementations must not block; exceptions they throw are
    /// swallowed.
    /// </summary>
    public interface IGGScaleLogger
    {
        /// <summary>One record per finished call (success or failure), after all attempts.</summary>
        void OnCallCompleted(GGCallRecord record);

        /// <summary>Emitted before each retry backoff sleep.</summary>
        void OnRetry(GGRetryRecord record);

        /// <summary>Emitted on WebSocket state transitions and anomalies.</summary>
        void OnWsEvent(GGWsEventRecord record);
    }

    /// <summary>The completion record for one logical API call.</summary>
    public sealed class GGCallRecord
    {
        /// <summary>Creates a completion record.</summary>
        public GGCallRecord(string operation, string method, int status, GGFailureKind? failureKind, string? errorCode, TimeSpan duration, int attempts, string requestId)
        {
            Operation = operation ?? string.Empty;
            Method = method ?? string.Empty;
            Status = status;
            FailureKind = failureKind;
            ErrorCode = errorCode;
            Duration = duration;
            Attempts = attempts;
            RequestId = requestId ?? string.Empty;
        }

        /// <summary>The route template (e.g. "GET /v1/storage/objects/{key}"), never a raw URL.</summary>
        public string Operation { get; }

        /// <summary>The HTTP method.</summary>
        public string Method { get; }

        /// <summary>The final HTTP status, or 0 when no response arrived.</summary>
        public int Status { get; }

        /// <summary>The failure class, or null on success.</summary>
        public GGFailureKind? FailureKind { get; }

        /// <summary>The machine-readable server error code, when one was provided.</summary>
        public string? ErrorCode { get; }

        /// <summary>Total call duration including retries and backoff.</summary>
        public TimeSpan Duration { get; }

        /// <summary>How many attempts were made (1 = no retries).</summary>
        public int Attempts { get; }

        /// <summary>The X-Request-Id shared by every attempt of this call.</summary>
        public string RequestId { get; }

        /// <summary>The SDK version that produced this record.</summary>
        public string SdkVersion { get; } = GGScale.SdkVersion.Value;
    }

    /// <summary>Describes one upcoming retry of a call.</summary>
    public sealed class GGRetryRecord
    {
        /// <summary>Creates a retry record.</summary>
        public GGRetryRecord(string operation, int attempt, string reason, TimeSpan delay, string requestId)
        {
            Operation = operation ?? string.Empty;
            Attempt = attempt;
            Reason = reason ?? string.Empty;
            Delay = delay;
            RequestId = requestId ?? string.Empty;
        }

        /// <summary>The route template of the call being retried.</summary>
        public string Operation { get; }

        /// <summary>The attempt number that just failed (1-based).</summary>
        public int Attempt { get; }

        /// <summary>Why the attempt is retried (e.g. "http_503", "connection", "timeout").</summary>
        public string Reason { get; }

        /// <summary>How long the SDK waits before the next attempt.</summary>
        public TimeSpan Delay { get; }

        /// <summary>The X-Request-Id shared by every attempt of this call.</summary>
        public string RequestId { get; }
    }

    /// <summary>Describes a WebSocket lifecycle event.</summary>
    public sealed class GGWsEventRecord
    {
        /// <summary>Creates a WebSocket event record.</summary>
        public GGWsEventRecord(string eventName, int? closeCode = null, int? handshakeStatus = null, int attempt = 0, TimeSpan? delay = null, TimeSpan? connectionDuration = null, int droppedMessages = 0)
        {
            EventName = eventName ?? string.Empty;
            CloseCode = closeCode;
            HandshakeStatus = handshakeStatus;
            Attempt = attempt;
            Delay = delay;
            ConnectionDuration = connectionDuration;
            DroppedMessages = droppedMessages;
        }

        /// <summary>The event (e.g. "connected", "reconnecting", "closed", "message_dropped").</summary>
        public string EventName { get; }

        /// <summary>The WebSocket close code, when the event carries one.</summary>
        public int? CloseCode { get; }

        /// <summary>The HTTP status of a failed upgrade, when known.</summary>
        public int? HandshakeStatus { get; }

        /// <summary>The reconnect attempt number, when reconnecting.</summary>
        public int Attempt { get; }

        /// <summary>The backoff delay before the next reconnect, when reconnecting.</summary>
        public TimeSpan? Delay { get; }

        /// <summary>How long the connection was up, on close events.</summary>
        public TimeSpan? ConnectionDuration { get; }

        /// <summary>How many queued messages were dropped, on overflow events.</summary>
        public int DroppedMessages { get; }
    }
}
