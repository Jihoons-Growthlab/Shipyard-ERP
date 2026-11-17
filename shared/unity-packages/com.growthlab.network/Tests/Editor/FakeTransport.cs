using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthLab.Network.Tests
{
    /// <summary>Scripted transport: each call consumes the next step. No sockets are opened.</summary>
    internal sealed class FakeTransport : IHttpTransport
    {
        private readonly Queue<Func<HttpRequest, CancellationToken, Task<HttpResponse>>> _steps =
            new Queue<Func<HttpRequest, CancellationToken, Task<HttpResponse>>>();

        public readonly List<HttpRequest> Requests = new List<HttpRequest>();

        public FakeTransport Respond(int status, string body = null, IDictionary<string, string> headers = null)
        {
            _steps.Enqueue((_, __) => Task.FromResult(new HttpResponse(status, body, headers)));
            return this;
        }

        public FakeTransport Fail(NetworkErrorKind kind)
        {
            _steps.Enqueue((_, __) => Task.FromException<HttpResponse>(new NetworkException(kind, "fake " + kind)));
            return this;
        }

        /// <summary>Never responds; completes as cancelled when the token fires.</summary>
        public FakeTransport Hang()
        {
            _steps.Enqueue((_, token) =>
            {
                var completion = new TaskCompletionSource<HttpResponse>();
                token.Register(() => completion.TrySetCanceled(token));
                return completion.Task;
            });
            return this;
        }

        public FakeTransport Then(Func<HttpRequest, CancellationToken, Task<HttpResponse>> step)
        {
            _steps.Enqueue(step);
            return this;
        }

        public Task<HttpResponse> SendAsync(HttpRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("FakeTransport has no scripted response for " + request.Url);
            }

            return _steps.Dequeue()(request, cancellationToken);
        }
    }
}
