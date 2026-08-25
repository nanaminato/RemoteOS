using System.Net.Http.Json;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Tunnels;

namespace Client.Apps.Tunnels;

/// <summary>Safe client facade: it has no API that reads a profile token or generated TOML.</summary>
public sealed class RemoteTunnelClient(HttpClient http) : IRemoteTunnelClient
{
    public async Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<TunnelServerProfileDto>>(TunnelApiRoutes.Profiles, RemoteOsJsonOptions.Default, ct) ?? [];
    public async Task<IReadOnlyList<TunnelDefinitionDto>> ListAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<TunnelDefinitionDto>>(TunnelApiRoutes.Tunnels, RemoteOsJsonOptions.Default, ct) ?? [];
    public async Task<TunnelRuntimeDto> GetRuntimeAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<TunnelRuntimeDto>(TunnelApiRoutes.Runtime, RemoteOsJsonOptions.Default, ct) ?? throw new HttpRequestException("Tunnel runtime response was empty.");
    public Task<TunnelOperationResultDto> ApplyAsync(Guid profileId, CancellationToken ct = default) => PostAsync(TunnelApiRoutes.ApplyProfile.Replace("{profileId}", profileId.ToString("D"), StringComparison.Ordinal), ct);
    public Task<TunnelOperationResultDto> StopAsync(Guid profileId, CancellationToken ct = default) => PostAsync(TunnelApiRoutes.StopProfile.Replace("{profileId}", profileId.ToString("D"), StringComparison.Ordinal), ct);
    public async Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(Guid profileId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<TunnelLogEntryDto>>(TunnelApiRoutes.ProfileLogs.Replace("{profileId}", profileId.ToString("D"), StringComparison.Ordinal), RemoteOsJsonOptions.Default, ct) ?? [];
    public Task<TunnelOperationResultDto> InstallManagedRuntimeAsync(string version, CancellationToken ct = default) => PostJsonAsync(TunnelApiRoutes.RuntimeInstall, new InstallManagedTunnelRuntimeRequest(true, version), ct);
    public Task<TunnelOperationResultDto> RollbackManagedRuntimeAsync(CancellationToken ct = default) => PostAsync(TunnelApiRoutes.RuntimeRollback, ct);
    private async Task<TunnelOperationResultDto> PostAsync(string path, CancellationToken ct)
    {
        using var response = await http.PostAsync(path, content: null, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TunnelOperationResultDto>(RemoteOsJsonOptions.Default, ct) ?? throw new HttpRequestException("Tunnel operation response was empty.");
    }
    private async Task<TunnelOperationResultDto> PostJsonAsync<T>(string path, T payload, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, payload, RemoteOsJsonOptions.Default, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TunnelOperationResultDto>(RemoteOsJsonOptions.Default, ct) ?? throw new HttpRequestException("Tunnel operation response was empty.");
    }
}
