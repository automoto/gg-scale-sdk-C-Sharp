using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GGScale.Tests
{
    public class AsyncBoundedQueueTests
    {
        [Fact]
        public async Task Queue_delivers_fifo_and_blocks_reader_until_write()
        {
            var q = new AsyncBoundedQueue<string>(4);
            var pending = q.ReadAsync(CancellationToken.None);
            Assert.False(pending.IsCompleted);

            q.TryWrite("a", out _);
            q.TryWrite("b", out _);

            Assert.Equal("a", await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("b", await q.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task Queue_drops_oldest_when_full()
        {
            var q = new AsyncBoundedQueue<string>(2);
            q.TryWrite("a", out var d1);
            q.TryWrite("b", out var d2);
            q.TryWrite("c", out var d3);

            Assert.Null(d1);
            Assert.Null(d2);
            Assert.Equal("a", d3);
            Assert.Equal("b", await q.ReadAsync(CancellationToken.None));
            Assert.Equal("c", await q.ReadAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Queue_read_returns_null_after_complete_and_drain()
        {
            var q = new AsyncBoundedQueue<string>(2);
            q.TryWrite("a", out _);
            q.Complete();

            Assert.Equal("a", await q.ReadAsync(CancellationToken.None));
            Assert.Null(await q.ReadAsync(CancellationToken.None));
            Assert.False(q.TryWrite("late", out _));
        }

        [Fact]
        public async Task Queue_read_observes_cancellation()
        {
            var q = new AsyncBoundedQueue<string>(2);
            using var cts = new CancellationTokenSource();
            var pending = q.ReadAsync(cts.Token);

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}
