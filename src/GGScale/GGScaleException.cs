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
        private static readonly IReadOnlyList<GGErrorDetail> NoDetails = Array.Empty<GGErrorDetail>();

        /// <summary>Creates an exception for a failed API call.</summary>
        public GGScaleException(int status, string code, string message, TimeSpan? retryAfter = null, long conflictVersion = 0, IReadOnlyList<GGErrorDetail>? details = null)
            : base(FormatMessage(status, code, message))
        {
            Status = status;
            Code = code ?? string.Empty;
            Detail = message ?? string.Empty;
            RetryAfter = retryAfter;
            ConflictVersion = conflictVersion;
            Details = details ?? NoDetails;
        }

        /// <summary>HTTP status code of the response.</summary>
        public int Status { get; }

        /// <summary>Machine-readable server error code, when provided (e.g. "rate_limit_exceeded").</summary>
        public string Code { get; }

        /// <summary>Raw server-provided error message, when provided.</summary>
        public string Detail { get; }

        /// <summary>Server-suggested wait before retrying (429s), when provided.</summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>The object's current version on a 412 storage conflict; 0 otherwise.</summary>
        public long ConflictVersion { get; }

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
