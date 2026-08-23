using System.Net;
using System.Net.Http.Headers;

namespace Client.Services.Auth;

/// <summary>
/// Adds the current bearer token to protected API requests, refreshes near-expiry tokens, and
/// retries a server-rejected request once after a coordinated refresh. It deliberately does not
/// retry transport failures, because a non-idempotent operation may already have reached server code.
/// </summary>
public sealed class AuthenticatedHttpHandler(IAuthSession session) : DelegatingHandler
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await session.GetAccessTokenAsync(RenewBefore, ct: cancellationToken)
            .ConfigureAwait(false);
        if (accessToken is null)
            throw session.State == AuthSessionState.Authenticated
                ? new HttpRequestException("Unable to refresh the RemoteOS session. Check the network connection.")
                : new InvalidOperationException("The RemoteOS session has expired. Sign in again.");

        using var retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        SetAuthorization(request, accessToken);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        var refreshedToken = await session.GetAccessTokenAsync(TimeSpan.Zero, accessToken, cancellationToken)
            .ConfigureAwait(false);
        if (refreshedToken is null)
            return response;

        response.Dispose();
        SetAuthorization(retry, refreshedToken);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static void SetAuthorization(HttpRequestMessage request, string accessToken)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
        };
        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (source.Content is null)
            return clone;

        var bytes = await source.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        clone.Content = new ByteArrayContent(bytes);
        foreach (var header in source.Content.Headers)
            clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
