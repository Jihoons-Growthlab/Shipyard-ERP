using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GrowthLab.Network;
using NUnit.Framework;

namespace GrowthLab.Auth.Tests
{
    public sealed class AuthenticatedRestClientTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Accepts only the bearer tokens in <see cref="Accepted"/>; everything else is 401.</summary>
        private sealed class TokenCheckingClient : IRestClient
        {
            public readonly HashSet<string> Accepted = new HashSet<string>();
            public readonly List<string> SeenAuthorization = new List<string>();

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
            {
                request.Headers.TryGetValue("Authorization", out var header);
                SeenAuthorization.Add(header);
                return Accepted.Contains(header)
                    ? Task.FromResult(new HttpResponse(200, "ok"))
                    : Task.FromException<HttpResponse>(new NetworkException(NetworkErrorKind.Unauthorized, "401",
                        new HttpResponse(401)));
            }
        }

        private sealed class ManualBackend : IAuthBackend
        {
            public int Calls;
            public TaskCompletionSource<AuthTokens> Pending;

            public Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
            {
                Calls++;
                Pending = new TaskCompletionSource<AuthTokens>();
                return Pending.Task;
            }
        }

        private SynchronizationContext _previousContext;

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_previousContext);

        private static AuthSession SignedIn(IAuthBackend backend)
        {
            var session = new AuthSession(backend, utcNow: () => Now);
            session.SignIn(new AuthTokens("a1", "r1", Now.AddHours(1)));
            return session;
        }

        [Test]
        public void AddsBearerHeader_WithoutMutatingCallerRequest()
        {
            var inner = new TokenCheckingClient();
            inner.Accepted.Add("Bearer a1");
            var client = new AuthenticatedRestClient(inner, SignedIn(new ManualBackend()));
            var request = new HttpRequest(HttpMethod.Get, "me");

            Assert.AreEqual("ok", client.SendAsync(request).Result.Body);
            Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
        }

        [Test]
        public void Unauthorized_RefreshesOnce_AndResends()
        {
            var inner = new TokenCheckingClient();
            inner.Accepted.Add("Bearer a2");
            var backend = new ManualBackend();
            var client = new AuthenticatedRestClient(inner, SignedIn(backend));

            var task = client.SendAsync(new HttpRequest(HttpMethod.Post, "orders") { Body = "{}" });
            backend.Pending.SetResult(new AuthTokens("a2", "r2", Now.AddHours(1)));

            Assert.AreEqual("ok", task.Result.Body);
            CollectionAssert.AreEqual(new[] { "Bearer a1", "Bearer a2" }, inner.SeenAuthorization);
            Assert.AreEqual(1, backend.Calls);
        }

        [Test]
        public void SecondUnauthorized_IsThrown_WithoutLooping()
        {
            var inner = new TokenCheckingClient();
            var backend = new ManualBackend();
            var client = new AuthenticatedRestClient(inner, SignedIn(backend));

            var task = client.SendAsync(new HttpRequest(HttpMethod.Get, "me"));
            backend.Pending.SetResult(new AuthTokens("a2", "r2", Now.AddHours(1)));

            Assert.IsTrue(task.IsFaulted);
            Assert.AreEqual(NetworkErrorKind.Unauthorized, ((NetworkException)task.Exception.InnerException).Kind);
            Assert.AreEqual(2, inner.SeenAuthorization.Count);
            Assert.AreEqual(1, backend.Calls);
        }

        [Test]
        public void ConcurrentUnauthorized_WithSameStaleToken_ShareOneRefresh()
        {
            var inner = new TokenCheckingClient();
            inner.Accepted.Add("Bearer a2");
            var backend = new ManualBackend();
            var client = new AuthenticatedRestClient(inner, SignedIn(backend));

            var first = client.SendAsync(new HttpRequest(HttpMethod.Get, "a"));
            var second = client.SendAsync(new HttpRequest(HttpMethod.Get, "b"));
            backend.Pending.SetResult(new AuthTokens("a2", "r2", Now.AddHours(1)));

            Assert.AreEqual("ok", first.Result.Body);
            Assert.AreEqual("ok", second.Result.Body);
            Assert.AreEqual(1, backend.Calls);
        }

        [Test]
        public void OtherErrors_PassThrough_WithoutRefresh()
        {
            var backend = new ManualBackend();
            var inner = new FailingClient(new NetworkException(NetworkErrorKind.Forbidden, "403"));
            var client = new AuthenticatedRestClient(inner, SignedIn(backend));

            var task = client.SendAsync(new HttpRequest(HttpMethod.Get, "admin"));

            Assert.AreEqual(NetworkErrorKind.Forbidden, ((NetworkException)task.Exception.InnerException).Kind);
            Assert.AreEqual(0, backend.Calls);
        }

        private sealed class FailingClient : IRestClient
        {
            private readonly Exception _error;

            public FailingClient(Exception error) => _error = error;

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default) =>
                Task.FromException<HttpResponse>(_error);
        }
    }
}
