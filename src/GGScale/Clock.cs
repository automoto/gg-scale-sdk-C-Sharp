using System;
using System.Threading;
using System.Threading.Tasks;

namespace GGScale
{
    /// <summary>
    /// Time source for expiry checks and backoff sleeps. Internal so tests
    /// can substitute a deterministic clock; production always uses
    /// <see cref="SystemClock"/>.
    /// </summary>
    internal interface IGGClock
    {
        /// <summary>The current UTC time.</summary>
        DateTimeOffset UtcNow { get; }

        /// <summary>Waits for <paramref name="delay"/>, observing cancellation.</summary>
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    /// <summary>Wall-clock implementation of <see cref="IGGClock"/>.</summary>
    internal sealed class SystemClock : IGGClock
    {
        internal static readonly SystemClock Instance = new SystemClock();

        private SystemClock()
        {
        }

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}
