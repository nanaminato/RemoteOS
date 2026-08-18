using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Client.Services.Auth;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.WebServers;

namespace Client.Apps.WebServers;

/// <summary>
/// HTTP implementation of <see cref="IRemoteWebServerClient"/>. Mutating endpoints require an
/// Idempotency-Key header (server-enforced); each call generates a fresh key so retries never
/// duplicate an operation. 202 Accepted responses carry the operation dto in the body.
/// </summary>
public sealed class RemoteWebServerClient(HttpClient http, IAuthSession session) : IRemoteWebServerClient
{
    public Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<WebServerDto>>(HttpMethod.Post, WebServerApiRoutes.DiscoverPattern, null, null, cancellationToken);

    public Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<WebServerDto>>(HttpMethod.Get, WebServerApiRoutes.CollectionPattern, null, null, cancellationToken);

    public Task<WebServerStatusDto?> GetStatusAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<WebServerStatusDto?>(HttpMethod.Get, WebServerApiRoutes.StatusPattern.Replace("{id}", WebUtility.UrlEncode(id)), null, null, cancellationToken);

    public Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<WebServerConfigTestResultDto?>(HttpMethod.Post, WebServerApiRoutes.TestConfigurationPattern.Replace("{id}", WebUtility.UrlEncode(id)), null, null, cancellationToken);

    public Task<WebServerOperationDto?> InstallManagedAsync(string providerId, InstallManagedWebServerRequest request, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.ManagedInstallPattern.Replace("{providerId}", WebUtility.UrlEncode(providerId)), request, NewKey(), cancellationToken);

    public Task<WebServerOperationDto?> IntegrateAsync(string id, IntegrateWebServerRequest request, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.IntegratePattern.Replace("{id}", WebUtility.UrlEncode(id)), request, NewKey(), cancellationToken);

    public Task<WebServerOperationDto?> ApplyLifecycleAsync(string id, WebServerLifecycleAction action, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.LifecyclePattern.Replace("{id}", WebUtility.UrlEncode(id)).Replace("{action}", action.ToString().ToLowerInvariant()), null, NewKey(), cancellationToken);

    public Task<WebServerOperationDto?> UninstallManagedAsync(string id, UninstallManagedWebServerRequest request, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.ManagedUninstallPattern.Replace("{id}", WebUtility.UrlEncode(id)), request, NewKey(), cancellationToken);

    public Task<WebServerOperationDto?> ReloadAsync(string id, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.ReloadPattern.Replace("{id}", WebUtility.UrlEncode(id)), null, NewKey(), cancellationToken);

    public Task<WebServerOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Get, WebServerApiRoutes.OperationsPattern.Replace("{operationId:guid}", operationId.ToString("N")), null, null, cancellationToken);

    public Task<WebServerOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => SendAsync<WebServerOperationDto?>(HttpMethod.Post, WebServerApiRoutes.CancelOperationPattern.Replace("{operationId:guid}", operationId.ToString("N")), null, null, cancellationToken);

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("RemoteOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RemoteOsJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound && default(T) is null)
            return default!;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("RemoteOS returned an empty response.");
    }
}
