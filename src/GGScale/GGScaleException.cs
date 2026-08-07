using System;
using System.Collections.Generic;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// One entry from a problem-details <c>errors</c> array — a validation
    /// failure or a structured extension. <see cref="Value"/> is the raw JSON
    /// of the offending/related value.
    /// </summary>
    public sealed class GGErrorDetail
    {
        /// <summary>Creates a detail entry.</summary>
        public GGErrorDetail(string message, string location, JsonValue value)
        {
            Message = message ?? string.Empty;
            Location = location ?? string.Empty;
            Value = value ?? JsonValue.Null;
        }

        /// <summary>Human-readable detail message.</summary>
        public string Message { get; }

        /// <summary>Where the problem is (e.g. "active_ticket_id").</summary>
        public string Location { get; }

        /// <summary>The related value as raw JSON.</summary>
        public JsonValue Value { get; }

        internal static GGErrorDetail FromJson(JsonValue v) =>
            new GGErrorDetail(
                v.OptString("message") ?? string.Empty,
                v.OptString("location") ?? string.Empty,
                v.Opt("value") ?? JsonValue.Null);
    }

    /// <summary>
    /// The error thrown for any non-2xx API response. Carries the HTTP
    /// status plus whatever structured detail the server provided. Branch
    /// on the convenience properties (IsNotFound, IsConflict, …) instead
    /// of matching message text.
    /// </summary>
    public sealed class GGScaleException : Exception
    {
        /// <summary>
        /// The code marking a TLS/certificate validation failure — never
        /// retried automatically (retries cannot repair a bad certificate).
        /// </summary>
        internal const string CertificateErrorCode = "certificate_error";

        /// <summary>
        /// The code marking a failed session refresh before a WebSocket
        /// (re)connect — never replayed automatically (an ambiguous refresh
        /// may have consumed the rotating token).
        /// </summary>
        internal const string SessionRefreshFailedCode = "session_refresh_failed";

        private static readonly IReadOnlyList<GGErrorDetail> NoDetails = Array.Empty<GGErrorDetail>();

        /// <summary>Creates an exception for a failed API call.</summary>
        public GGScaleException(int status, string code, string message, TimeSpan? retryAfter = null, long conflictVersion = 0, IReadOnlyList<GGErrorDetail>? details = null)
            : base(FormatMessage(status, code, message))
        {
            Kind = GGFailureKind.HttpError;
            Status = status;
            Code = code ?? string.Empty;
            Detail = message ?? string.Empty;
            RetryAfter = retryAfter;
            ConflictVersion = conflictVersion;
            Details = details ?? NoDetails;
        }

        /// <summary>
        /// Creates an exception for a transport-level failure (no HTTP
        /// response): connection, timeout, decode, or WebSocket handshake.
        /// Status is 0 and the underlying cause is preserved as the inner
        /// exception.
        /// </summary>
        public GGScaleException(GGFailureKind kind, string code, string message, Exception? innerException = null)
            : base(FormatTransportMessage(code, message), innerException)
        {
            Kind = kind;
            Status = 0;
            Code = code ?? string.Empty;
            Detail = message ?? string.Empty;
            Details = NoDetails;
        }

        /// <summary>What class of failure this is. HttpError when a response arrived.</summary>
        public GGFailureKind Kind { get; }

        /// <summary>
        /// HTTP status code of the response, or 0 for transport failures
        /// (including WebSocket handshake rejections whose status the
        /// platform cannot observe).
        /// </summary>
        public int Status { get; internal set; }

        /// <summary>Machine-readable server error code, when provided (e.g. "rate_limit_exceeded").</summary>
        public string Code { get; }

        /// <summary>Raw server-provided error message, when provided.</summary>
        public string Detail { get; }

        /// <summary>Server-suggested wait before retrying (429s), when provided.</summary>
        public TimeSpan? RetryAfter { get; internal set; }

        /// <summary>The object's current version on a 412 storage conflict; 0 otherwise.</summary>
        public long ConflictVersion { get; }

        /// <summary>The problem-details <c>type</c> URI; empty when not provided.</summary>
        public string ProblemType { get; internal set; } = string.Empty;

        /// <summary>The problem-details <c>title</c>; empty when not provided.</summary>
        public string Title { get; internal set; } = string.Empty;

        /// <summary>The problem-details <c>instance</c>; empty when not provided.</summary>
        public string Instance { get; internal set; } = string.Empty;

        /// <summary>The X-Request-Id correlating this call, when known.</summary>
        public string? RequestId { get; internal set; }

        /// <summary>
        /// A bounded (2 KiB) copy of the raw response body, kept when the
        /// body could not be parsed as JSON. Null otherwise.
        /// </summary>
        public string? RawBody { get; internal set; }

        /// <summary>
        /// True when the failure class permits an automatic retry for a
        /// replayable request: connection failures, timeouts, and HTTP
        /// 408/429/502/503/504. TLS/certificate validation failures are
        /// never retryable. Method safety is judged separately.
        /// </summary>
        public bool IsRetryable =>
            Code != CertificateErrorCode &&
            (Kind == GGFailureKind.Connection ||
             Kind == GGFailureKind.Timeout ||
             (Kind == GGFailureKind.HttpError &&
                 (Status == 408 || Status == 429 || Status == 502 || Status == 503 || Status == 504)));

        /// <summary>
        /// True when the exception chain contains a TLS/certificate
        /// validation failure.
        /// </summary>
        internal static bool HasCertificateFailure(Exception? exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is System.Security.Authentication.AuthenticationException)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True for 401 Unauthorized (bad/missing credential or invalid session).</summary>
        public bool IsUnauthorized => Status == 401;

        /// <summary>True for 403 Forbidden (key type/scope, or linked-account required).</summary>
        public bool IsForbidden => Status == 403;

        /// <summary>True for 404 Not Found.</summary>
        public bool IsNotFound => Status == 404;

        /// <summary>True for 409 Conflict and 412 Precondition Failed.</summary>
        public bool IsConflict => Status == 409 || Status == 412;

        /// <summary>True for 429 Too Many Requests.</summary>
        public bool IsRateLimited => Status == 429;

        /// <summary>True for 400 Bad Request.</summary>
        public bool IsBadRequest => Status == 400;

        /// <summary>
        /// True for 422 Unprocessable Entity — the server-side request
        /// validation failure (missing/short/malformed fields). Field
        /// details are in <see cref="Details"/>.
        /// </summary>
        public bool IsValidationError => Status == 422;

        /// <summary>The problem-details <c>errors</c> entries, if any.</summary>
        public IReadOnlyList<GGErrorDetail> Details { get; }

        /// <summary>
        /// True for the 409 returned by matchmaker CreateTicket when the
        /// player already has an active ticket. Read <see cref="ActiveTicketId"/>
        /// for the id to cancel.
        /// </summary>
        public bool IsTicketAlreadyActive => Status == 409 && Detail == "ticket_already_active";

        /// <summary>
        /// The id of the ticket already queued when this is a
        /// ticket_already_active conflict, or 0 otherwise.
        /// </summary>
        public long ActiveTicketId
        {
            get
            {
                foreach (var d in Details)
                {
                    if (d.Location == "active_ticket_id" && d.Value.Kind == JsonKind.Number)
                    {
                        return d.Value.AsLong();
                    }
                }
                return 0;
            }
        }

        private static string FormatTransportMessage(string code, string message)
        {
            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(message))
            {
                return $"ggscale: {code}: {message}";
            }
            if (!string.IsNullOrEmpty(message))
            {
                return $"ggscale: {message}";
            }
            return $"ggscale: {code}";
        }

        private static string FormatMessage(int status, string code, string message)
        {
            if (!string.IsNullOrEmpty(code))
            {
                return $"ggscale: {status} {code}: {message}";
            }
            if (!string.IsNullOrEmpty(message))
            {
                return $"ggscale: {status}: {message}";
            }
            return $"ggscale: {status}";
        }
    }
}
