using System;
using System.Threading;
using System.Threading.Tasks;

namespace GGScale
{
    /// <summary>
    /// Retry configuration. The SDK retries only requests that are safe to
    /// replay (GET/HEAD/PUT/DELETE, or POST/PATCH explicitly marked
    /// idempotent) and only for connection failures, timeouts, and HTTP
    /// 408/429/502/503/504. All attempts and backoff waits stay inside
    /// <see cref="GGScaleClientOptions.OverallTimeout"/>.
    /// </summary>
    public sealed class GGRetryPolicy
    {
        /// <summary>Total attempts including the first (default 3). 1 disables retries.</summary>
        public int MaxAttempts { get; set; } = 3;

        /// <summary>Base backoff unit for full jitter (default 250 ms).</summary>
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>Upper bound on one backoff wait (default 10 s).</summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>Full-jitter backoff math, shared by HTTP retries and WebSocket reconnects.</summary>
    internal static class Backoff
    {
        /// <summary>
        /// random(0, min(maxDelay, baseDelay × 2^attempt)) with
        /// <paramref name="unitRandom"/> in [0, 1).
        /// </summary>
        internal static TimeSpan FullJitter(double unitRandom, TimeSpan baseDelay, TimeSpan maxDelay, int attempt)
        {
            var factor = Math.Pow(2, Math.Min(attempt, 30));
            var capMs = Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * factor);
            return TimeSpan.FromMilliseconds(capMs * unitRandom);
        }
    }

    /// <summary>
    /// Decorates an <see cref="ITransport"/> with the SDK retry policy:
    /// full-jitter backoff, Retry-After as a minimum wait, an overall
    /// deadline across attempts, one stable X-Request-Id per logical call,
    /// and telemetry records.
    /// </summary>
    internal sealed class RetryingTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly GGRetryPolicy _policy;
        private readonly TimeSpan _overallTimeout;
        private readonly IGGClock _clock;
        private readonly IGGScaleLogger? _logger;
        private readonly object _rngLock = new object();
        private readonly Random _rng = new Random();

        internal RetryingTransport(ITransport inner, GGRetryPolicy policy, TimeSpan overallTimeout, IGGClock clock, IGGScaleLogger? logger)
        {
            _inner = inner;
            _policy = policy;
            _overallTimeout = overallTimeout;
            _clock = clock;
            _logger = logger;
        }

        /// <summary>The undecorated transport.</summary>
        internal ITransport Inner => _inner;

        public async Task<GGResponse> CallAsync(GGRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            // Assigned once, so the id stays stable across retry attempts
            // and across the client's 401 refresh-and-retry re-invocation.
            if (request.RequestId == null)
            {
                request.RequestId = Guid.NewGuid().ToString("N");
            }
            if (request.TelemetryStart == null)
            {
                request.TelemetryStart = _clock.UtcNow;
            }
            var start = request.TelemetryStart.Value;
            var deadline = _overallTimeout >= DateTimeOffset.MaxValue - start
                ? DateTimeOffset.MaxValue
                : start + _overallTimeout;
            var attempts = request.TelemetryAttempts;
            while (true)
            {
                var remaining = deadline - _clock.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    var expired = DeadlineExceeded(null);
                    EmitCompletion(request, 0, expired.Kind, expired.Code, attempts, start);
                    throw expired;
                }
                attempts++;
                request.TelemetryAttempts = attempts;
                // The attempt itself is bounded by the remaining overall
                // budget, so a stalled transport cannot outlive the
                // advertised deadline.
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (remaining.TotalMilliseconds < int.MaxValue)
                {
                    attemptCts.CancelAfter(remaining);
                }
                try
                {
                    var resp = await _inner.CallAsync(request, attemptCts.Token).ConfigureAwait(false);
                    EmitCompletion(request, resp.Status, null, null, attempts, start);
                    return resp;
                }
                catch (GGScaleException ex)
                {
                    if (!ShouldRetry(request, ex) || attempts >= _policy.MaxAttempts)
                    {
                        if (!(ex.Status == 401 && request.RetryOn401Pending))
                        {
                            EmitCompletion(request, ex.Status, ex.Kind, ex.Code, attempts, start);
                        }
                        throw;
                    }
                    var wait = Backoff.FullJitter(NextUnitRandom(), _policy.BaseDelay, _policy.MaxDelay, attempts);
                    if (ex.RetryAfter != null && ex.RetryAfter.Value > wait)
                    {
                        wait = ex.RetryAfter.Value;
                    }
                    if (_clock.UtcNow + wait > deadline)
                    {
                        EmitCompletion(request, ex.Status, ex.Kind, ex.Code, attempts, start);
                        throw;
                    }
                    EmitRetry(request, attempts, RetryReason(ex), wait);
                    try
                    {
                        await _clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        EmitCompletion(request, 0, GGFailureKind.Canceled, null, attempts, start);
                        throw;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    EmitCompletion(request, 0, GGFailureKind.Canceled, null, attempts, start);
                    throw;
                }
                catch (OperationCanceledException oce)
                {
                    // Only the deadline token fired: the overall budget is
                    // spent mid-attempt.
                    var expired = DeadlineExceeded(oce);
                    EmitCompletion(request, 0, expired.Kind, expired.Code, attempts, start);
                    throw expired;
                }
            }
        }

        /// <summary>
        /// Emits the completion record the retry layer deferred while the
        /// client attempted a 401 refresh that did not lead to a retried
        /// call.
        /// </summary>
        internal void EmitDeferredCompletion(GGRequest request, int status, GGFailureKind? kind, string? code)
        {
            EmitCompletion(request, status, kind, code, request.TelemetryAttempts, request.TelemetryStart ?? _clock.UtcNow);
        }

        private GGScaleException DeadlineExceeded(Exception? inner) =>
            new GGScaleException(
                GGFailureKind.Timeout,
                "deadline_exceeded",
                FormattableString.Invariant($"overall deadline of {_overallTimeout.TotalSeconds:0.###} s exceeded"),
                inner);

        private static bool ShouldRetry(GGRequest request, GGScaleException ex)
        {
            if (!ex.IsRetryable)
            {
                return false;
            }
            var m = request.Method;
            var safeMethod =
                string.Equals(m, "GET", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "HEAD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "PUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, "DELETE", StringComparison.OrdinalIgnoreCase);
            return safeMethod || request.Idempotent;
        }

        private static string RetryReason(GGScaleException ex)
        {
            switch (ex.Kind)
            {
                case GGFailureKind.Connection:
                    return "connection";
                case GGFailureKind.Timeout:
                    return "timeout";
                default:
                    return "http_" + ex.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Backoff jitter is not security-sensitive.")]
        private double NextUnitRandom()
        {
            lock (_rngLock)
            {
                return _rng.NextDouble();
            }
        }

        private void EmitCompletion(GGRequest request, int status, GGFailureKind? kind, string? code, int attempts, DateTimeOffset start)
        {
            if (_logger == null)
            {
                return;
            }
            try
            {
                _logger.OnCallCompleted(new GGCallRecord(
                    request.Operation,
                    request.Method,
                    status,
                    kind,
                    string.IsNullOrEmpty(code) ? null : code,
                    _clock.UtcNow - start,
                    attempts,
                    request.RequestId ?? string.Empty));
            }
            catch (Exception)
            {
                // Observability hooks must never break calls.
            }
        }

        private void EmitRetry(GGRequest request, int attempt, string reason, TimeSpan delay)
        {
            if (_logger == null)
            {
                return;
            }
            try
            {
                _logger.OnRetry(new GGRetryRecord(request.Operation, attempt, reason, delay, request.RequestId ?? string.Empty));
            }
            catch (Exception)
            {
                // Observability hooks must never break calls.
            }
        }
    }
}
