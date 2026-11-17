using System;
using System.Threading;
using System.Threading.Tasks;
using GrowthLab.Network;

namespace GrowthLab.Auth
{
    /// <summary>
    /// Adds <c>Authorization: Bearer</c> from an <see cref="AuthSession"/>. On 401 it refreshes once (shared
    /// with concurrent callers that saw the same stale token) and resends once. 401 means the server did
    /// not accept the request, so resending is allowed for every method; a second 401 is thrown.
    /// </summary>
    public sealed class AuthenticatedRestClient : IRestClient
    {
        private readonly IRestClient _inner;
        private readonly AuthSession _session;

        public AuthenticatedRestClient(IRestClient inner, AuthSession session)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
            try
            {
                return await _inner.SendAsync(WithToken(request, accessToken), cancellationToken);
            }
            catch (NetworkException exception) when (exception.Kind == NetworkErrorKind.Unauthorized)
            {
                var refreshed = await _session.RefreshAsync(accessToken, cancellationToken);
                return await _inner.SendAsync(WithToken(request, refreshed.AccessToken), cancellationToken);
            }
        }

        private static HttpRequest WithToken(HttpRequest request, string accessToken)
        {
            var copy = request.Clone();
            copy.Headers["Authorization"] = "Bearer " + accessToken;
            return copy;
        }
    }
}
