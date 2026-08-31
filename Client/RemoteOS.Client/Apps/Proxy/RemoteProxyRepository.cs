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
    public Task<ProxyOperationAcceptedDto> LifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.Lifecycle.Replace("{action}", action.ToString().ToLowerInvariant()), new ProxyLifecycleRequest(true), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> EmergencyDisableTunAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.TunEmergencyDisable, null, cancellationToken, idempotent: true);

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
}
public sealed class ProxyRequestException(string problemCode) : Exception(problemCode) { public string ProblemCode { get; } = problemCode; }
