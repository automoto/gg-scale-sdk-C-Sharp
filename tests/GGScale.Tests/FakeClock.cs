using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GGScale.Tests
{
    /// <summary>
    /// Deterministic clock: DelayAsync records the requested wait, advances
    /// virtual time by it, and completes synchronously.
    /// </summary>
    public sealed class FakeClock : IGGClock
    {
        private readonly object _mu = new object();
        private DateTimeOffset _now;
        private readonly List<TimeSpan> _delays = new List<TimeSpan>();

        public FakeClock(DateTimeOffset? start = null)
        {
            _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_mu)
                {
                    return _now;
                }
            }
        }

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_mu)
                {
                    return _delays.ToArray();
                }
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (_mu)
            {
                _now += by;
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_mu)
            {
                _delays.Add(delay);
                _now += delay;
            }
            return Task.CompletedTask;
        }
    }
}
