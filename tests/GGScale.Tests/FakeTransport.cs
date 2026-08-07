using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale.Tests
{
    /// <summary>
    /// Test double capturing requests and returning staged responses.
    /// Two modes: set Respond for a single canned answer (throw a
    /// GGScaleException from it to simulate an error), or Enqueue* steps
    /// for per-call scripted sequences. Scripted steps win while any are
    /// queued. Mirrors the Go SDK's fakeTransport. Thread-safe so
    /// concurrency tests can hammer it.
    /// </summary>
    public sealed class FakeTransport : ITransport
    {
        private readonly object _mu = new object();
        private readonly List<GGRequest> _requests = new List<GGRequest>();
        private readonly Queue<Func<GGRequest, CancellationToken, Task<GGResponse>>> _script =
            new Queue<Func<GGRequest, CancellationToken, Task<GGResponse>>>();
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

        /// <summary>Every request, in call order.</summary>
        public IReadOnlyList<GGRequest> Requests
        {
            get
            {
                lock (_mu)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Func<GGRequest, JsonValue>? Respond { get; set; }

        /// <summary>Stages one success response for the next unscripted call.</summary>
        public void EnqueueResult(JsonValue body, int status = 200, string? etag = null)
        {
            EnqueueStep((_, _) => Task.FromResult(new GGResponse(status, body, etag)));
        }

        /// <summary>Stages one error for the next unscripted call.</summary>
        public void EnqueueError(GGScaleException error)
        {
            EnqueueStep((_, _) => Task.FromException<GGResponse>(error));
        }

        /// <summary>Stages one arbitrary step (delays, cancellation, inspection).</summary>
        public void EnqueueStep(Func<GGRequest, CancellationToken, Task<GGResponse>> step)
        {
            lock (_mu)
            {
                _script.Enqueue(step);
            }
        }

        /// <summary>Number of calls whose path matched.</summary>
        public int CountForPath(string path)
        {
            lock (_mu)
            {
                return _pathCounts.TryGetValue(path, out var n) ? n : 0;
            }
        }

        private readonly Dictionary<string, int> _pathCounts = new Dictionary<string, int>();

        public Task<GGResponse> CallAsync(GGRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Func<GGRequest, CancellationToken, Task<GGResponse>>? step = null;
            lock (_mu)
            {
                _callCount++;
                _lastRequest = request;
                _requests.Add(request);
                _pathCounts.TryGetValue(request.Path, out var n);
                _pathCounts[request.Path] = n + 1;
                if (_script.Count > 0)
                {
                    step = _script.Dequeue();
                }
            }
            if (step != null)
            {
                return step(request, cancellationToken);
            }
            var body = Respond?.Invoke(request) ?? JsonValue.Null;
            return Task.FromResult(new GGResponse(200, body));
        }
    }
}
