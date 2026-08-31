using System.Net.Http.Headers;
using System.Net.Http.Json;
using Client.Services.Auth;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Proxy;

namespace Client.Apps.Proxy;

/// <summary>Typed RemoteOS API client. It never receives a Mihomo controller address, secret or response schema.</summary>
public sealed class RemoteProxyRepository(HttpClient http, IAuthSession session) : IProxyRepository
{
    public Task<ProxyOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOverviewDto>(HttpMethod.Get, ProxyApiRoutes.Overview, null, cancellationToken);
    public Task<IReadOnlyList<ProxyProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxyProfileDto>>(HttpMethod.Get, ProxyApiRoutes.Profiles, null, cancellationToken);
    public Task<IReadOnlyList<ProxyGroupDto>> ListGroupsAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxyGroupDto>>(HttpMethod.Get, ProxyApiRoutes.Groups, null, cancellationToken);
    public Task<IReadOnlyList<ProxyConnectionDto>> ListConnectionsAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxyConnectionDto>>(HttpMethod.Get, ProxyApiRoutes.Connections, null, cancellationToken);
    public Task<IReadOnlyList<ProxyLogEntryDto>> ListLogsAsync(int limit = 200, CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxyLogEntryDto>>(HttpMethod.Get, ProxyApiRoutes.Logs + "?limit=" + Math.Clamp(limit, 1, 500), null, cancellationToken);
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyDnsStatusDto>(HttpMethod.Get, ProxyApiRoutes.Dns, null, cancellationToken);
    public Task<ProxyRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyRuntimeDto>(HttpMethod.Get, ProxyApiRoutes.Runtime, null, cancellationToken);
    public Task<ProxyOperationAcceptedDto> LifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.Lifecycle.Replace("{action}", action.ToString().ToLowerInvariant()), new ProxyLifecycleRequest(true), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> InstallRuntimeAsync(string engineId, string? version = null, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.RuntimeInstall, new ProxyRuntimeRequest(engineId, version), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> InstallRuntimeFromServerFileAsync(string engineId, string archivePath, string? version = null, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.RuntimeInstallFromFile, new InstallProxyRuntimeFromFileRequest(engineId, version, archivePath), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> RollbackRuntimeAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.RuntimeRollback, null, cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> UninstallRuntimeAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Delete, ProxyApiRoutes.RuntimeUninstall, null, cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> EnableTunAsync(Guid profileId, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.TunEnable, new ProxyTunRequest(profileId), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> DisableTunAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.TunDisable, null, cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> EmergencyDisableTunAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.TunEmergencyDisable, null, cancellationToken, idempotent: true);
    public Task<ProxyProfileDto> CreateProfileAsync(string name, string engineId, CancellationToken cancellationToken = default) => SendAsync<ProxyProfileDto>(HttpMethod.Post, ProxyApiRoutes.Profiles, new UpsertProxyProfileRequest(name, engineId), cancellationToken);
    public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Delete, ProfileRoute(profileId), null, cancellationToken);
    public Task<ProxyProfileDto> ActivateProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => SendAsync<ProxyProfileDto>(HttpMethod.Post, ProfileRoute(profileId) + "/activate", null, cancellationToken);
    public Task SelectGroupAsync(string groupName, string proxy, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Put, ProxyApiRoutes.Proxy + "/groups/" + Uri.EscapeDataString(groupName) + "/selection", new SelectProxyGroupRequest(proxy), cancellationToken);
    public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Delete, ProxyApiRoutes.Proxy + "/connections/" + Uri.EscapeDataString(connectionId), null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken, bool idempotent = false)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null) throw new InvalidOperationException("RemoteOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RemoteOsJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (idempotent) request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ProxyRequestException("proxy.http_" + (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken) ?? throw new ProxyRequestException("proxy.response_empty");
    }

    private async Task SendNoContentAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null) throw new InvalidOperationException("RemoteOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RemoteOsJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ProxyRequestException("proxy.http_" + (int)response.StatusCode);
    }

    private static string ProfileRoute(Guid profileId) => ProxyApiRoutes.Proxy + "/profiles/" + profileId;
}
public sealed class ProxyRequestException(string problemCode) : Exception(problemCode) { public string ProblemCode { get; } = problemCode; }
