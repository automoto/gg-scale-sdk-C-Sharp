namespace GGScale
{
    /// <summary>
    /// Classifies why an API call failed. Lets callers and the retry layer
    /// branch on the failure class instead of exception message text.
    /// Caller-initiated cancellation is not represented here — it surfaces
    /// as <see cref="System.OperationCanceledException"/>.
    /// </summary>
    public enum GGFailureKind
    {
        /// <summary>The server answered with a non-success HTTP status.</summary>
        HttpError = 0,

        /// <summary>DNS, connect, TLS, or reset failure — the request may not have reached the server.</summary>
        Connection = 1,

        /// <summary>The per-attempt or overall deadline elapsed before a response arrived.</summary>
        Timeout = 2,

        /// <summary>Reserved for telemetry records of caller-cancelled calls.</summary>
        Canceled = 3,

        /// <summary>A success response could not be decoded, or a size limit was exceeded.</summary>
        Decode = 4,

        /// <summary>A WebSocket upgrade was rejected before the connection opened.</summary>
        Handshake = 5,
    }
}
