using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GrowthLab.Network;
using NUnit.Framework;

namespace GrowthLab.Auth.Tests
{
    /// <summary>
    /// Race tests drive the backend by hand with TaskCompletionSource. The synchronization context is
    /// cleared so continuations run inline when a step completes, which makes every interleaving explicit.
    /// </summary>
    public sealed class AuthSessionTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private sealed class ManualBackend : IAuthBackend
        {
            public readonly List<(string RefreshToken, CancellationToken Token, TaskCompletionSource<AuthTokens> Completion)>
                Calls = new List<(string, CancellationToken, TaskCompletionSource<AuthTokens>)>();

            public Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
            {
                var completion = new TaskCompletionSource<AuthTokens>();
                Calls.Add((refreshToken, cancellationToken, completion));
                return completion.Task;
            }

            public TaskCompletionSource<AuthTokens> Last => Calls[Calls.Count - 1].Completion;
        }

        private SynchronizationContext _previousContext;
        private ManualBackend _backend;
        private List<AuthChange> _changes;

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _backend = new ManualBackend();
            _changes = new List<AuthChange>();
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_previousContext);

        private AuthSession Session(AuthTokens tokens)
        {
            var session = new AuthSession(_backend, TimeSpan.FromSeconds(30), () => Now);
            session.SignIn(tokens);
            session.Changed += _changes.Add;
            return session;
        }

        private static AuthTokens Expired(string access = "a1", string refresh = "r1") =>
            new AuthTokens(access, refresh, Now.AddSeconds(10)); // inside the 30s skew

        private static AuthTokens Fresh(string access, string refresh = "r2") =>
            new AuthTokens(access, refresh, Now.AddHours(1));

        private static AuthException AuthError(Task task)
        {
            Assert.IsTrue(task.IsFaulted, "Expected a faulted task, was " + task.Status);
            return (AuthException)task.Exception.InnerException;
        }

        [Test]
        public void ValidToken_IsReturned_WithoutRefresh()
        {
            var session = Session(Fresh("a1"));
            Assert.AreEqual("a1", session.GetAccessTokenAsync().Result);
            Assert.AreEqual(0, _backend.Calls.Count);
        }

        [Test]
        public void TokenWithoutExpiry_NeverRefreshes()
        {
            var session = Session(new AuthTokens("a1", "r1", null));
            Assert.AreEqual("a1", session.GetAccessTokenAsync().Result);
            Assert.AreEqual(0, _backend.Calls.Count);
        }

        [Test]
        public void ConcurrentCallers_ShareOneRefresh()
        {
            var session = Session(Expired());

            var first = session.GetAccessTokenAsync();
            var second = session.GetAccessTokenAsync();
            var third = session.RefreshAsync();

            Assert.AreEqual(1, _backend.Calls.Count);
            Assert.AreEqual("r1", _backend.Calls[0].RefreshToken);

            _backend.Last.SetResult(Fresh("a2"));

            Assert.AreEqual("a2", first.Result);
            Assert.AreEqual("a2", second.Result);
            Assert.AreEqual("a2", third.Result.AccessToken);
            CollectionAssert.AreEqual(new[] { AuthChange.Refreshed }, _changes);
        }

        [Test]
        public void StaleAccessToken_AfterAnotherRefresh_DoesNotRefreshAgain()
        {
            var session = Session(Expired());
            session.RefreshAsync("a1");
            _backend.Last.SetResult(Fresh("a2"));

            var result = session.RefreshAsync("a1").Result;

            Assert.AreEqual("a2", result.AccessToken);
            Assert.AreEqual(1, _backend.Calls.Count);
        }

        [Test]
        public void SignOutDuringRefresh_FailsWaiters_CancelsBackend_AndDiscardsLateResult()
        {
            var session = Session(Expired());
            var waiter = session.GetAccessTokenAsync();

            session.SignOut();

            Assert.AreEqual(AuthErrorKind.SessionEnded, AuthError(waiter).Kind);
            Assert.IsTrue(_backend.Calls[0].Token.IsCancellationRequested);

            _backend.Last.SetResult(Fresh("a2"));   // server answers after sign-out

            Assert.IsFalse(session.IsSignedIn);
            Assert.IsNull(session.Tokens);
            CollectionAssert.AreEqual(new[] { AuthChange.SignedOut }, _changes);
        }

        [Test]
        public void SignInDuringRefresh_KeepsNewTokens()
        {
            var session = Session(Expired());
            var waiter = session.RefreshAsync();

            session.SignIn(Fresh("b1", "rb"));
            _backend.Last.SetResult(Fresh("a2"));

            Assert.AreEqual(AuthErrorKind.SessionEnded, AuthError(waiter).Kind);
            Assert.AreEqual("b1", session.Tokens.AccessToken);
        }

        [Test]
        public void RejectedRefresh_SignsOut_AndRaisesExpired()
        {
            var session = Session(Expired());
            var waiter = session.GetAccessTokenAsync();

            _backend.Last.SetException(new AuthException(AuthErrorKind.RefreshRejected, "revoked"));

            Assert.AreEqual(AuthErrorKind.RefreshRejected, AuthError(waiter).Kind);
            Assert.IsFalse(session.IsSignedIn);
            CollectionAssert.AreEqual(new[] { AuthChange.Expired }, _changes);
        }

        [Test]
        public void ChangedEvent_IsRaisedBeforeWaitersResume_WhenRefreshIsRejected()
        {
            var session = Session(Expired());
            var waiter = session.GetAccessTokenAsync();
            // A caller that reacts to the failure by signing in again, inline.
            waiter.ContinueWith(_ => session.SignIn(Fresh("b1", "rb")), TaskContinuationOptions.ExecuteSynchronously);

            _backend.Last.SetException(new AuthException(AuthErrorKind.RefreshRejected, "revoked"));

            CollectionAssert.AreEqual(new[] { AuthChange.Expired, AuthChange.SignedIn }, _changes);
            Assert.IsTrue(session.IsSignedIn);
        }

        [Test]
        public void ChangedEvent_IsRaisedBeforeWaitersResume_WhenRefreshSucceeds()
        {
            var session = Session(Expired());
            var waiter = session.GetAccessTokenAsync();
            waiter.ContinueWith(_ => session.SignOut(), TaskContinuationOptions.ExecuteSynchronously);

            _backend.Last.SetResult(Fresh("a2"));

            CollectionAssert.AreEqual(new[] { AuthChange.Refreshed, AuthChange.SignedOut }, _changes);
            Assert.IsFalse(session.IsSignedIn);
        }

        [Test]
        public void ChangedEvent_IsRaisedBeforeWaitersResume_OnSignOut()
        {
            var session = Session(Expired());
            var waiter = session.GetAccessTokenAsync();
            waiter.ContinueWith(_ => session.SignIn(Fresh("b1", "rb")), TaskContinuationOptions.ExecuteSynchronously);

            session.SignOut();

            CollectionAssert.AreEqual(new[] { AuthChange.SignedOut, AuthChange.SignedIn }, _changes);
            Assert.IsTrue(session.IsSignedIn);
        }

        [Test]
        public void TransientRefreshFailure_KeepsSession_AndNextCallRetries()
        {
            var session = Session(Expired());
            var waiter = session.GetAccessTokenAsync();

            _backend.Last.SetException(new NetworkException(NetworkErrorKind.ConnectionFailed, "offline"));

            Assert.IsTrue(waiter.IsFaulted);
            Assert.IsInstanceOf<NetworkException>(waiter.Exception.InnerException);
            Assert.IsTrue(session.IsSignedIn);

            session.GetAccessTokenAsync();
            Assert.AreEqual(2, _backend.Calls.Count);
        }

        [Test]
        public void CallerCancellation_OnlyStopsThatCaller()
        {
            var session = Session(Expired());
            using (var cts = new CancellationTokenSource())
            {
                var cancelled = session.GetAccessTokenAsync(cts.Token);
                var other = session.GetAccessTokenAsync();

                cts.Cancel();

                Assert.IsTrue(cancelled.IsCanceled || cancelled.Exception?.InnerException is OperationCanceledException);
                Assert.IsFalse(_backend.Calls[0].Token.IsCancellationRequested);

                _backend.Last.SetResult(Fresh("a2"));
                Assert.AreEqual("a2", other.Result);
            }
        }

        [Test]
        public void MissingRefreshTokenInResponse_KeepsPreviousRefreshToken()
        {
            var session = Session(Expired());
            session.RefreshAsync();
            _backend.Last.SetResult(new AuthTokens("a2", null, Now.AddHours(1)));

            Assert.AreEqual("r1", session.Tokens.RefreshToken);
        }

        [Test]
        public void NotSignedIn_Throws()
        {
            var session = new AuthSession(_backend, utcNow: () => Now);
            Assert.AreEqual(AuthErrorKind.NotSignedIn, AuthError(session.GetAccessTokenAsync()).Kind);
            Assert.AreEqual(AuthErrorKind.NotSignedIn, AuthError(session.RefreshAsync()).Kind);
        }

        [Test]
        public void NoRefreshToken_RefreshIsRejectedWithoutBackendCall()
        {
            var session = Session(new AuthTokens("a1", null, Now));
            Assert.AreEqual(AuthErrorKind.RefreshRejected, AuthError(session.GetAccessTokenAsync()).Kind);
            Assert.AreEqual(0, _backend.Calls.Count);
        }
    }
}
