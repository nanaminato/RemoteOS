using System.Net.Http.Headers;
using System.Net.Http.Json;
using Client.Services.Auth;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Registry;

namespace Client.Apps.Registry;

public sealed class RegistryClient(HttpClient http, IAuthSession session) : IRegistryClient
{
    public Task<IReadOnlyList<RegistryEntryDto>> ListAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<RegistryEntryDto>>(RegistryApiRoutes.Entries, cancellationToken);
    public Task<RegistrySummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<RegistrySummaryDto>(RegistryApiRoutes.Summary, cancellationToken);

    private async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("Sign in to browse the configuration registry.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("The registry server returned an empty response.");
    }
}
