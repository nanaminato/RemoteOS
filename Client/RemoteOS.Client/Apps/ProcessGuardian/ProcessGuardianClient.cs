using System.Net.Http.Headers;
using System.Net.Http.Json;
using Client.Services.Auth;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.ProcessGuardian;

namespace Client.Apps.ProcessGuardian;

public sealed class ProcessGuardianClient(HttpClient http, IAuthSession session) : IProcessGuardianClient
{
    public Task<GuardianStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => SendAsync<GuardianStatusDto>(ProcessGuardianApiRoutes.Status, cancellationToken);
    public Task<IReadOnlyList<GuardianWorkloadDto>> ListWorkloadsAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<GuardianWorkloadDto>>(ProcessGuardianApiRoutes.Workloads, cancellationToken);
    public Task<GuardianAgentResponse> GetDefinitionAsync(string id, CancellationToken cancellationToken = default) => SendAsync<GuardianAgentResponse>(ProcessGuardianApiRoutes.Workload.Replace("{id}", Uri.EscapeDataString(id)), cancellationToken);
    public Task<GuardianAgentResponse> UpsertAsync(ProcessDefinitionDto definition, CancellationToken cancellationToken = default) => SendAsync<GuardianAgentResponse>(HttpMethod.Post, ProcessGuardianApiRoutes.Workloads, definition, cancellationToken);
    public Task<GuardianAgentResponse> DeleteAsync(string id, CancellationToken cancellationToken = default) => SendAsync<GuardianAgentResponse>(HttpMethod.Delete, ProcessGuardianApiRoutes.Workload.Replace("{id}", Uri.EscapeDataString(id)), null, cancellationToken);
    public Task<GuardianAgentResponse> ApplyActionAsync(string id, string action, CancellationToken cancellationToken = default) => SendAsync<GuardianAgentResponse>(HttpMethod.Post, ProcessGuardianApiRoutes.WorkloadAction.Replace("{id}", Uri.EscapeDataString(id)).Replace("{action}", action), null, cancellationToken);
    public Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string id, CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<GuardianLogEntryDto>>(ProcessGuardianApiRoutes.WorkloadLogs.Replace("{id}", Uri.EscapeDataString(id)), cancellationToken);
    public Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<GuardianAuditEntryDto>>(ProcessGuardianApiRoutes.Audit, cancellationToken);
    private Task<T> SendAsync<T>(string route, CancellationToken cancellationToken) => SendAsync<T>(HttpMethod.Get, route, null, cancellationToken);
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null) throw new InvalidOperationException("RemoteOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body, options: RemoteOsJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken) ?? throw new InvalidOperationException("RemoteOS returned an empty response.");
    }
}
