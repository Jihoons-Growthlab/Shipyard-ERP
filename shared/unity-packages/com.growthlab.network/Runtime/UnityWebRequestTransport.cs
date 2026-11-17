using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace GrowthLab.Network
{
    /// <summary>
    /// <see cref="IHttpTransport"/> over <see cref="UnityWebRequest"/>. Call from the main thread.
    /// Cancellation aborts the request.
    /// </summary>
    public sealed class UnityWebRequestTransport : IHttpTransport
    {
        public async Task<HttpResponse> SendAsync(HttpRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (var web = new UnityWebRequest(request.Url, request.Method.ToVerb()))
            {
                web.downloadHandler = new DownloadHandlerBuffer();
                if (request.Body != null)
                {
                    web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body))
                    {
                        contentType = request.ContentType,
                    };
                }

                foreach (var header in request.Headers)
                {
                    web.SetRequestHeader(header.Key, header.Value);
                }

                if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                {
                    web.timeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
                }

                var completion = new TaskCompletionSource<bool>();
                var operation = web.SendWebRequest();
                operation.completed += _ => completion.TrySetResult(true);

                // The registration may fire on a timer thread; only complete the task there and
                // abort after resuming on the main thread.
                using (cancellationToken.Register(() => completion.TrySetCanceled()))
                {
                    try
                    {
                        await completion.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        web.Abort();
                        throw new OperationCanceledException(cancellationToken);
                    }
                }

                return ToResponse(web);
            }
        }

        private static HttpResponse ToResponse(UnityWebRequest web)
        {
            switch (web.result)
            {
                case UnityWebRequest.Result.Success:
                case UnityWebRequest.Result.ProtocolError:
                    return new HttpResponse((int)web.responseCode, web.downloadHandler?.text, web.GetResponseHeaders());
                case UnityWebRequest.Result.ConnectionError:
                    var kind = web.error != null && web.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                        ? NetworkErrorKind.Timeout
                        : NetworkErrorKind.ConnectionFailed;
                    throw new NetworkException(kind, web.error ?? "Connection error.");
                default:
                    if (web.responseCode > 0)
                    {
                        return new HttpResponse((int)web.responseCode, web.downloadHandler?.text, web.GetResponseHeaders());
                    }

                    throw new NetworkException(NetworkErrorKind.ConnectionFailed, web.error ?? "Request failed.");
            }
        }
    }
}
