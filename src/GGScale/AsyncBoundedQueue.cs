using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GGScale
{
    /// <summary>
    /// A bounded FIFO for one async reader and any number of writers.
    /// When full, the oldest item is dropped so writers never block —
    /// blocking the WebSocket read loop would stop control-frame handling.
    /// Hand-rolled because System.Threading.Channels is not in the
    /// netstandard2.1 BCL and the SDK takes no dependencies.
    /// </summary>
    internal sealed class AsyncBoundedQueue<T> where T : class
    {
        private readonly object _mu = new object();
        private readonly Queue<T> _items;
        private readonly int _capacity;
        private TaskCompletionSource<bool>? _signal;
        private bool _completed;

        internal AsyncBoundedQueue(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _capacity = capacity;
            _items = new Queue<T>(capacity);
        }

        /// <summary>
        /// Enqueues an item, dropping the oldest when full (returned via
        /// <paramref name="dropped"/>). Returns false after Complete.
        /// </summary>
        internal bool TryWrite(T item, out T? dropped)
        {
            dropped = null;
            TaskCompletionSource<bool>? signal;
            lock (_mu)
            {
                if (_completed)
                {
                    return false;
                }
                if (_items.Count >= _capacity)
                {
                    dropped = _items.Dequeue();
                }
                _items.Enqueue(item);
                signal = _signal;
                _signal = null;
            }
            signal?.TrySetResult(true);
            return true;
        }

        /// <summary>
        /// Dequeues the next item, waiting when empty. Returns null once
        /// the queue is completed and drained.
        /// </summary>
        internal async Task<T?> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                TaskCompletionSource<bool> wait;
                lock (_mu)
                {
                    if (_items.Count > 0)
                    {
                        return _items.Dequeue();
                    }
                    if (_completed)
                    {
                        return null;
                    }
                    _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _signal;
                }
                using (cancellationToken.Register(() => wait.TrySetCanceled(cancellationToken)))
                {
                    await wait.Task.ConfigureAwait(false);
                }
            }
        }

        /// <summary>Marks the queue finished; readers drain then get null.</summary>
        internal void Complete()
        {
            TaskCompletionSource<bool>? signal;
            lock (_mu)
            {
                _completed = true;
                signal = _signal;
                _signal = null;
            }
            signal?.TrySetResult(true);
        }
    }
}
