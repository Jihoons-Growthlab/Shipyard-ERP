using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GrowthLab.Network
{
    /// <summary>JSON convenience calls using <see cref="JsonUtility"/> ([Serializable] types only).</summary>
    public static class RestJsonExtensions
    {
        public static async Task<T> GetJsonAsync<T>(this IRestClient client, string url,
            CancellationToken cancellationToken = default)
        {
            var response = await client.SendAsync(new HttpRequest(HttpMethod.Get, url), cancellationToken);
            return Parse<T>(response, url);
        }

        public static async Task<TResponse> SendJsonAsync<TBody, TResponse>(this IRestClient client, HttpMethod method,
            string url, TBody body, CancellationToken cancellationToken = default)
        {
            var request = new HttpRequest(method, url) { Body = JsonUtility.ToJson(body) };
            var response = await client.SendAsync(request, cancellationToken);
            return Parse<TResponse>(response, url);
        }

        private static T Parse<T>(HttpResponse response, string url)
        {
            try
            {
                return JsonUtility.FromJson<T>(response.Body);
            }
            catch (ArgumentException exception)
            {
                throw new FormatException("Response from " + url + " is not valid JSON for " + typeof(T).Name + ".",
                    exception);
            }
        }
    }
}
