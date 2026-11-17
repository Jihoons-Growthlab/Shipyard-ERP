using System;

namespace GrowthLab.Network
{
    public enum NetworkErrorKind
    {
        /// <summary>No response: DNS, refused connection, TLS failure, offline.</summary>
        ConnectionFailed,
        Timeout,
        BadRequest,
        Unauthorized,
        Forbidden,
        NotFound,
        Conflict,
        RateLimited,
        /// <summary>Other 4xx.</summary>
        ClientError,
        /// <summary>5xx.</summary>
        ServerError,
        /// <summary>Non-2xx outside the ranges above (for example 1xx or 3xx not followed).</summary>
        Unexpected,
    }

    public sealed class NetworkException : Exception
    {
        public NetworkException(NetworkErrorKind kind, string message, HttpResponse response = null, int attempts = 1,
            Exception inner = null) : base(message, inner)
        {
            Kind = kind;
            Response = response;
            Attempts = attempts;
        }

        public NetworkErrorKind Kind { get; }

        /// <summary>Null for <see cref="NetworkErrorKind.ConnectionFailed"/> and transport timeouts.</summary>
        public HttpResponse Response { get; }

        public int StatusCode => Response?.StatusCode ?? 0;

        /// <summary>How many times the request was sent, including retries.</summary>
        public int Attempts { get; }
    }

    public static class NetworkErrors
    {
        public static NetworkErrorKind FromStatus(int status)
        {
            switch (status)
            {
                case 400:
                case 422:
                    return NetworkErrorKind.BadRequest;
                case 401: return NetworkErrorKind.Unauthorized;
                case 403: return NetworkErrorKind.Forbidden;
                case 404: return NetworkErrorKind.NotFound;
                case 408: return NetworkErrorKind.Timeout;
                case 409: return NetworkErrorKind.Conflict;
                case 429: return NetworkErrorKind.RateLimited;
            }

            if (status >= 400 && status < 500)
            {
                return NetworkErrorKind.ClientError;
            }

            return status >= 500 && status < 600 ? NetworkErrorKind.ServerError : NetworkErrorKind.Unexpected;
        }

        /// <summary>
        /// Transient failures worth retrying for safe methods: no response, timeouts, 429, 502, 503, 504.
        /// 500 is not retried because it usually indicates a deterministic server bug.
        /// </summary>
        public static bool IsTransient(NetworkErrorKind kind, int status)
        {
            switch (kind)
            {
                case NetworkErrorKind.ConnectionFailed:
                case NetworkErrorKind.Timeout:
                case NetworkErrorKind.RateLimited:
                    return true;
                case NetworkErrorKind.ServerError:
                    return status == 502 || status == 503 || status == 504;
                default:
                    return false;
            }
        }
    }
}
