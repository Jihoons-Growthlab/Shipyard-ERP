using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthLab.Network
{
    /// <summary>
    /// Sends one HTTP exchange. Returns a response for every HTTP status code.
    /// Throws <see cref="NetworkException"/> (ConnectionFailed or Timeout) when no response arrives, and
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public interface IHttpTransport
    {
        Task<HttpResponse> SendAsync(HttpRequest request, TimeSpan timeout, CancellationToken cancellationToken);
    }

    public sealed class RestClientOptions
    {
        /// <summary>Prefix for relative request URLs. Ignored for absolute URLs.</summary>
        public string BaseUrl { get; set; }

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Extra attempts for safe methods after transient failures. 0 disables retries.</summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>First retry delay; doubles on each attempt up to <see cref="MaxRetryDelay"/>.</summary>
        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Waits between retries. Replace in tests. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; }
    }

    public interface IRestClient
    {
        /// <summary>Returns the 2xx response or throws <see cref="NetworkException"/> / <see cref="OperationCanceledException"/>.</summary>
        Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class RestClient : IRestClient
    {
        private readonly IHttpTransport _transport;
        private readonly RestClientOptions _options;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public RestClient(IHttpTransport transport, RestClientOptions options = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _options = options ?? new RestClientOptions();
            _delay = _options.Delay ?? DefaultDelay;
        }

        public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var resolved = request.Clone(ResolveUrl(request.Url));
            var timeout = request.Timeout ?? _options.Timeout;
            var maxRetries = request.Method.IsSafe() ? Math.Max(0, _options.MaxRetries) : 0;

            for (var attempt = 1;; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                HttpResponse response = null;
                NetworkErrorKind kind;
                Exception inner = null;
                try
                {
                    response = await SendOnce(resolved, timeout, cancellationToken);
                    if (response.IsSuccess)
                    {
                        return response;
                    }

                    kind = NetworkErrors.FromStatus(response.StatusCode);
                }
                catch (NetworkException exception)
                {
                    kind = exception.Kind;
                    inner = exception;
                }

                var status = response?.StatusCode ?? 0;
                if (attempt > maxRetries || !NetworkErrors.IsTransient(kind, status))
                {
                    throw new NetworkException(kind,
                        resolved.Method.ToVerb() + " " + resolved.Url + " failed: " + kind +
                        (status != 0 ? " (" + status + ")" : string.Empty), response, attempt, inner);
                }

                await _delay(RetryDelay(attempt, response), cancellationToken);
            }
        }

        private async Task<HttpResponse> SendOnce(HttpRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (timeout > TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
                {
                    attemptCancellation.CancelAfter(timeout);
                }

                try
                {
                    var response = await _transport.SendAsync(request, timeout, attemptCancellation.Token);
                    return response ?? throw new NetworkException(NetworkErrorKind.ConnectionFailed,
                        "Transport returned no response.");
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new NetworkException(NetworkErrorKind.Timeout,
                        "No response within " + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s.",
                        null, 1, exception);
                }
            }
        }

        /// <summary>Uses Retry-After (seconds) when present, otherwise exponential backoff.</summary>
        private TimeSpan RetryDelay(int attempt, HttpResponse response)
        {
            if (response != null && response.Headers.TryGetValue("Retry-After", out var value) &&
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                var requested = TimeSpan.FromSeconds(seconds);
                return requested < _options.MaxRetryDelay ? requested : _options.MaxRetryDelay;
            }

            var factor = Math.Pow(2, attempt - 1);
            var backoff = TimeSpan.FromMilliseconds(_options.RetryBaseDelay.TotalMilliseconds * factor);
            return backoff < _options.MaxRetryDelay ? backoff : _options.MaxRetryDelay;
        }

        private string ResolveUrl(string url)
        {
            // Scheme check instead of Uri.TryCreate: on Unix, "/path" parses as an absolute file:// URI.
            var absolute = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (absolute || string.IsNullOrEmpty(_options.BaseUrl))
            {
                return url;
            }

            return _options.BaseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
        }

        private static Task DefaultDelay(TimeSpan delay, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // No thread-pool timers on WebGL; wait on the player loop instead.
            return WaitOnPlayerLoop(delay, cancellationToken);
#else
            return Task.Delay(delay, cancellationToken);
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private static async Task WaitOnPlayerLoop(TimeSpan delay, CancellationToken cancellationToken) =>
            await UnityEngine.Awaitable.WaitForSecondsAsync((float)delay.TotalSeconds, cancellationToken);
#endif
    }
}
