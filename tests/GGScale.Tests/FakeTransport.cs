using System;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale.Tests
{
    /// <summary>
    /// Test double capturing the last request and returning a staged
    /// response. Throw a GGScaleException from Respond to simulate an
    /// API error. Mirrors the Go SDK's fakeTransport. Thread-safe so
    /// concurrency tests can hammer it.
    /// </summary>
    public sealed class FakeTransport : ITransport
    {
        private readonly object _mu = new object();
        private GGRequest? _lastRequest;
        private int _callCount;

        public GGRequest? LastRequest
        {
            get
            {
                lock (_mu)
                {
                    return _lastRequest;
                }
            }
        }

        public int CallCount
        {
            get
            {
                lock (_mu)
                {
                    return _callCount;
                }
            }
        }

        public Func<GGRequest, JsonValue>? Respond { get; set; }

        /// <summary>Number of calls whose path matched.</summary>
        public int CountForPath(string path)
        {
            lock (_mu)
            {
                return _pathCounts.TryGetValue(path, out var n) ? n : 0;
            }
        }

        private readonly System.Collections.Generic.Dictionary<string, int> _pathCounts =
            new System.Collections.Generic.Dictionary<string, int>();

        public Task<JsonValue> CallAsync(GGRequest request, CancellationToken cancellationToken)
        {
            lock (_mu)
            {
                _callCount++;
                _lastRequest = request;
                _pathCounts.TryGetValue(request.Path, out var n);
                _pathCounts[request.Path] = n + 1;
            }
            var body = Respond?.Invoke(request) ?? JsonValue.Null;
            return Task.FromResult(body);
        }
    }
}
