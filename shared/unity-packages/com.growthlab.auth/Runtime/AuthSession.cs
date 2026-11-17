using System;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthLab.Auth
{
    public sealed class AuthTokens
    {
        public AuthTokens(string accessToken, string refreshToken, DateTime? expiresAtUtc)
        {
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new ArgumentException("Access token is required.", nameof(accessToken));
            }

            AccessToken = accessToken;
            RefreshToken = refreshToken;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string AccessToken { get; }

        /// <summary>Null when the backend does not issue refresh tokens; refresh is then impossible.</summary>
        public string RefreshToken { get; }

        /// <summary>Null means the access token does not expire on a known schedule.</summary>
        public DateTime? ExpiresAtUtc { get; }
    }

    /// <summary>Exchanges a refresh token for new tokens. Implemented by the app for its own auth server.</summary>
    public interface IAuthBackend
    {
        /// <summary>
        /// Throw <see cref="AuthException"/> with <see cref="AuthErrorKind.RefreshRejected"/> when the server
        /// rejects the refresh token; the session then signs out. Any other exception keeps the session.
        /// If the returned tokens have no refresh token, the previous refresh token is kept.
        /// </summary>
        Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    }

    public enum AuthErrorKind
    {
        NotSignedIn,
        /// <summary>The session signed out (or signed in again) while the operation was running.</summary>
        SessionEnded,
        RefreshRejected,
    }

    public sealed class AuthException : Exception
    {
        public AuthException(AuthErrorKind kind, string message, Exception inner = null) : base(message, inner)
        {
            Kind = kind;
        }

        public AuthErrorKind Kind { get; }
    }

    public enum AuthChange
    {
        SignedIn,
        Refreshed,
        SignedOut,
        /// <summary>Signed out because the server rejected the refresh token.</summary>
        Expired,
    }

    /// <summary>
    /// Holds tokens and refreshes them. Concurrent refresh requests share one backend call. Signing out
    /// while a refresh runs cancels it, fails its waiters with <see cref="AuthErrorKind.SessionEnded"/>,
    /// and the late result is discarded so a signed-out session is never revived.
    /// </summary>
    public sealed class AuthSession
    {
        private readonly object _gate = new object();
        private readonly IAuthBackend _backend;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _expirySkew;

        private AuthTokens _tokens;
        private int _generation;
        private CancellationTokenSource _sessionCancellation = new CancellationTokenSource();
        private TaskCompletionSource<AuthTokens> _refresh;

        /// <param name="expirySkew">Tokens expiring within this window are treated as expired. Default 30s.</param>
        /// <param name="utcNow">Clock; replace in tests. Defaults to <see cref="DateTime.UtcNow"/>.</param>
        public AuthSession(IAuthBackend backend, TimeSpan? expirySkew = null, Func<DateTime> utcNow = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _expirySkew = expirySkew ?? TimeSpan.FromSeconds(30);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Raised outside internal locks, on the thread that caused the change, after the state has changed
        /// and before waiting callers resume. Listeners therefore see changes in the order they happened even
        /// when a waiter reacts inline (for example by signing in again).
        /// </summary>
        public event Action<AuthChange> Changed;

        public bool IsSignedIn
        {
            get
            {
                lock (_gate)
                {
                    return _tokens != null;
                }
            }
        }

        public AuthTokens Tokens
        {
            get
            {
                lock (_gate)
                {
                    return _tokens;
                }
            }
        }

        public void SignIn(AuthTokens tokens)
        {
            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }

            EndedGeneration ended;
            lock (_gate)
            {
                ended = EndGenerationLocked();
                _tokens = tokens;
            }

            Changed?.Invoke(AuthChange.SignedIn);
            ended.Finish(new AuthException(AuthErrorKind.SessionEnded, "Signed in again during refresh."));
        }

        public void SignOut()
        {
            EndedGeneration ended;
            lock (_gate)
            {
                if (_tokens == null)
                {
                    return;
                }

                ended = EndGenerationLocked();
                _tokens = null;
            }

            Changed?.Invoke(AuthChange.SignedOut);
            ended.Finish(new AuthException(AuthErrorKind.SessionEnded, "Signed out during refresh."));
        }

        public bool IsExpiring(AuthTokens tokens) =>
            tokens.ExpiresAtUtc.HasValue && tokens.ExpiresAtUtc.Value - _expirySkew <= _utcNow();

        /// <summary>Returns a non-expiring access token, refreshing first if needed.</summary>
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var tokens = Tokens ?? throw new AuthException(AuthErrorKind.NotSignedIn, "Not signed in.");
            if (!IsExpiring(tokens))
            {
                return tokens.AccessToken;
            }

            var refreshed = await RefreshAsync(tokens.AccessToken, cancellationToken);
            return refreshed.AccessToken;
        }

        /// <summary>
        /// Refreshes the tokens. If <paramref name="staleAccessToken"/> is given and the current access token
        /// already differs from it, another caller refreshed in the meantime and the current tokens are
        /// returned without a backend call. Cancelling <paramref name="cancellationToken"/> stops only this
        /// caller's wait; the shared refresh continues for other waiters.
        /// </summary>
        public Task<AuthTokens> RefreshAsync(string staleAccessToken = null, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<AuthTokens> started = null;
            Task<AuthTokens> shared;
            string refreshToken = null;
            int generation;
            CancellationToken sessionToken;
            lock (_gate)
            {
                if (_tokens == null)
                {
                    return Task.FromException<AuthTokens>(new AuthException(AuthErrorKind.NotSignedIn, "Not signed in."));
                }

                if (staleAccessToken != null && _tokens.AccessToken != staleAccessToken)
                {
                    return Task.FromResult(_tokens);
                }

                if (_refresh == null)
                {
                    if (string.IsNullOrEmpty(_tokens.RefreshToken))
                    {
                        return Task.FromException<AuthTokens>(
                            new AuthException(AuthErrorKind.RefreshRejected, "No refresh token available."));
                    }

                    started = _refresh = new TaskCompletionSource<AuthTokens>();
                    refreshToken = _tokens.RefreshToken;
                }

                shared = _refresh.Task;
                generation = _generation;
                sessionToken = _sessionCancellation.Token;
            }

            if (started != null)
            {
                // Started outside the lock so backend code never runs while holding it.
                RunRefresh(started, refreshToken, generation, sessionToken);
            }

            return WaitAsync(shared, cancellationToken);
        }

        private async void RunRefresh(TaskCompletionSource<AuthTokens> completion, string refreshToken, int generation,
            CancellationToken sessionToken)
        {
            AuthTokens result;
            try
            {
                var task = _backend.RefreshAsync(refreshToken, sessionToken) ??
                           throw new InvalidOperationException("Auth backend returned no task.");
                result = await task;
                if (result == null)
                {
                    throw new InvalidOperationException("Auth backend returned no tokens.");
                }
            }
            catch (AuthException exception) when (exception.Kind == AuthErrorKind.RefreshRejected)
            {
                var expired = false;
                var ended = default(EndedGeneration);
                lock (_gate)
                {
                    if (generation == _generation)
                    {
                        ended = EndGenerationLocked();
                        _tokens = null;
                        expired = true;
                    }
                }

                if (expired)
                {
                    Changed?.Invoke(AuthChange.Expired);
                }

                ended.Finish(exception);
                completion.TrySetException(exception);
                return;
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    if (_refresh == completion)
                    {
                        _refresh = null;
                    }
                }

                completion.TrySetException(sessionToken.IsCancellationRequested
                    ? new AuthException(AuthErrorKind.SessionEnded, "Session ended during refresh.", exception)
                    : exception);
                return;
            }

            if (result.RefreshToken == null)
            {
                result = new AuthTokens(result.AccessToken, refreshToken, result.ExpiresAtUtc);
            }

            lock (_gate)
            {
                if (generation != _generation)
                {
                    result = null;
                }
                else
                {
                    _tokens = result;
                    _refresh = null;
                }
            }

            if (result == null)
            {
                completion.TrySetException(new AuthException(AuthErrorKind.SessionEnded, "Session ended during refresh."));
                return;
            }

            Changed?.Invoke(AuthChange.Refreshed);
            completion.TrySetResult(result);
        }

        /// <summary>
        /// Starts a new generation. The caller must call <see cref="EndedGeneration.Finish"/> after leaving
        /// the lock, so waiter continuations and backend cancellation callbacks never run under it.
        /// </summary>
        private EndedGeneration EndGenerationLocked()
        {
            _generation++;
            var ended = new EndedGeneration(_refresh, _sessionCancellation);
            _sessionCancellation = new CancellationTokenSource();
            _refresh = null;
            return ended;
        }

        private readonly struct EndedGeneration
        {
            private readonly TaskCompletionSource<AuthTokens> _refresh;
            private readonly CancellationTokenSource _cancellation;

            public EndedGeneration(TaskCompletionSource<AuthTokens> refresh, CancellationTokenSource cancellation)
            {
                _refresh = refresh;
                _cancellation = cancellation;
            }

            /// <summary>Fails waiters first so they see <paramref name="reason"/>, then cancels the backend call.</summary>
            public void Finish(Exception reason)
            {
                _refresh?.TrySetException(reason);
                // Not disposed: a backend may still register on the token, which is safe once cancelled.
                _cancellation?.Cancel();
            }
        }

        private static async Task<AuthTokens> WaitAsync(Task<AuthTokens> shared, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled || shared.IsCompleted)
            {
                return await shared;
            }

            var cancelled = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(shared, cancelled.Task) != shared)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return await shared;
        }
    }
}
