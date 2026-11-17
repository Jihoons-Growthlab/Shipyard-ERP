using System;
using System.Collections.Generic;

namespace GrowthLab.Network
{
    public enum HttpMethod
    {
        Get,
        Head,
        Post,
        Put,
        Patch,
        Delete,
    }

    public static class HttpMethodExtensions
    {
        /// <summary>Safe methods (RFC 9110) have no side effects and are the only ones retried automatically.</summary>
        public static bool IsSafe(this HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head;

        public static string ToVerb(this HttpMethod method)
        {
            switch (method)
            {
                case HttpMethod.Get: return "GET";
                case HttpMethod.Head: return "HEAD";
                case HttpMethod.Post: return "POST";
                case HttpMethod.Put: return "PUT";
                case HttpMethod.Patch: return "PATCH";
                case HttpMethod.Delete: return "DELETE";
                default: throw new ArgumentOutOfRangeException(nameof(method), method, null);
            }
        }
    }

    public sealed class HttpRequest
    {
        public HttpRequest(HttpMethod method, string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("URL is required.", nameof(url));
            }

            Method = method;
            Url = url;
        }

        public HttpMethod Method { get; }

        /// <summary>Absolute URL, or a path relative to <see cref="RestClientOptions.BaseUrl"/>.</summary>
        public string Url { get; }

        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Body { get; set; }

        public string ContentType { get; set; } = "application/json";

        /// <summary>Overrides <see cref="RestClientOptions.Timeout"/> for this request.</summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>Copy with the same method, URL, body and headers; used by wrappers that add headers.</summary>
        public HttpRequest Clone(string url = null)
        {
            var copy = new HttpRequest(Method, url ?? Url) { Body = Body, ContentType = ContentType, Timeout = Timeout };
            foreach (var header in Headers)
            {
                copy.Headers[header.Key] = header.Value;
            }

            return copy;
        }
    }

    public sealed class HttpResponse
    {
        public HttpResponse(int statusCode, string body = null, IDictionary<string, string> headers = null)
        {
            StatusCode = statusCode;
            Body = body ?? string.Empty;
            Headers = headers != null
                ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public int StatusCode { get; }

        public string Body { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }

        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    }
}
