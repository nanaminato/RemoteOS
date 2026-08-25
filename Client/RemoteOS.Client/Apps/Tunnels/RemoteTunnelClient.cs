using System.Net;
using System.Net.Http.Json;
using Client.Services.Auth;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Tunnels;

namespace Client.Apps.Tunnels;

/// <summary>Safe client facade: it has no API that reads a profile token or generated TOML.</summary>
public sealed class RemoteTunnelClient(HttpClient http, IAuthSession session) : IRemoteTunnelClient
{
    public async Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(CancellationToken ct = default) =>
        await SendAsync<IReadOnlyList<TunnelServerProfileDto>>(HttpMethod.Get, TunnelApiRoutes.Profiles, null, ct) ?? [];
    public async Task<IReadOnlyList<TunnelDefinitionDto>> ListAsync(CancellationToken ct = default) =>
        await SendAsync<IReadOnlyList<TunnelDefinitionDto>>(HttpMethod.Get, TunnelApiRoutes.Tunnels, null, ct) ?? [];
    public async Task<TunnelRuntimeDto> GetRuntimeAsync(CancellationToken ct = default) =>
        await SendAsync<TunnelRuntimeDto>(HttpMethod.Get, TunnelApiRoutes.Runtime, null, ct) ?? throw new HttpRequestException("Tunnel runtime response was empty.");
    public async Task<TunnelRuntimeInstallationDto> GetRuntimeInstallationStatusAsync(CancellationToken ct = default) =>
        await SendAsync<TunnelRuntimeInstallationDto>(HttpMethod.Get, TunnelApiRoutes.RuntimeInstallationStatus, null, ct) ?? throw new HttpRequestException("Tunnel runtime installation status response was empty.");
    public Task<TunnelServerProfileDto> CreateProfileAsync(UpsertTunnelServerProfileRequest request, CancellationToken ct = default) => SendRequiredAsync<TunnelServerProfileDto>(HttpMethod.Post, TunnelApiRoutes.Profiles, request, ct);
    public Task<TunnelServerProfileDto> UpdateProfileAsync(Guid id, UpsertTunnelServerProfileRequest request, CancellationToken ct = default) => SendRequiredAsync<TunnelServerProfileDto>(HttpMethod.Put, ProfileRoute(id), request, ct);
    public Task DeleteProfileAsync(Guid id, CancellationToken ct = default) => SendNoContentAsync(HttpMethod.Delete, ProfileRoute(id), ct: ct);
    public Task SetProfileTokenAsync(Guid id, string token, CancellationToken ct = default) => SendNoContentAsync(HttpMethod.Put, SecretRoute(id), new SetTunnelProfileTokenRequest(token), ct);
    public Task<TunnelDefinitionDto> CreateTunnelAsync(UpsertTunnelDefinitionRequest request, CancellationToken ct = default) => SendRequiredAsync<TunnelDefinitionDto>(HttpMethod.Post, TunnelApiRoutes.Tunnels, request, ct);
    public Task<TunnelDefinitionDto> UpdateTunnelAsync(Guid id, UpsertTunnelDefinitionRequest request, CancellationToken ct = default) => SendRequiredAsync<TunnelDefinitionDto>(HttpMethod.Put, TunnelRoute(id), request, ct);
    public Task DeleteTunnelAsync(Guid id, CancellationToken ct = default) => SendNoContentAsync(HttpMethod.Delete, TunnelRoute(id), ct: ct);
    public Task<TunnelOperationResultDto> ApplyAsync(Guid profileId, CancellationToken ct = default) => SendOperationAsync(HttpMethod.Post, TunnelApiRoutes.ApplyProfile.Replace("{profileId}", profileId.ToString("D"), StringComparison.Ordinal), null, ct);
    public Task<TunnelOperationResultDto> StopAsync(Guid profileId, CancellationToken ct = default) => SendOperationAsync(HttpMethod.Post, TunnelApiRoutes.StopProfile.Replace("{profileId}", profileId.ToString("D"), StringComparison.Ordinal), null, ct);
    public async Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(Guid profileId, CancellationToken ct = default) =>
        await SendAsync<IReadOnlyList<TunnelLogEntryDto>>(HttpMethod.Get, TunnelApiRoutes.ProfileLogs.Replace("{profileId}", profileId.ToString("D"), StringComparison.Ordinal), null, ct) ?? [];
    public Task<TunnelOperationResultDto> InstallManagedRuntimeAsync(string version, CancellationToken ct = default) => SendOperationAsync(HttpMethod.Post, TunnelApiRoutes.RuntimeInstall, new InstallManagedTunnelRuntimeRequest(true, version), ct);
    public Task<TunnelOperationResultDto> InstallManagedRuntimeFromServerFileAsync(string version, string archivePath, CancellationToken ct = default) => SendOperationAsync(HttpMethod.Post, TunnelApiRoutes.RuntimeInstallFromFile, new InstallManagedTunnelRuntimeFromFileRequest(true, version, archivePath), ct);
    public Task<TunnelOperationResultDto> RollbackManagedRuntimeAsync(CancellationToken ct = default) => SendOperationAsync(HttpMethod.Post, TunnelApiRoutes.RuntimeRollback, null, ct);
    public Task<TunnelRuntimeDto> DetectExternalRuntimeAsync(string path, CancellationToken ct = default) => SendRequiredAsync<TunnelRuntimeDto>(HttpMethod.Post, TunnelApiRoutes.RuntimeDetectExternal, new DetectExternalTunnelRuntimeRequest(path), ct);

    private async Task<TunnelOperationResultDto> SendOperationAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, payload, ct);
        var result = await response.Content.ReadFromJsonAsync<TunnelOperationResultDto>(RemoteOsJsonOptions.Default, ct);
        if (result is not null) return result;
        await EnsureSuccessAsync(response, ct);
        throw new HttpRequestException("Tunnel operation response was empty.");
    }
    private async Task<T> SendRequiredAsync<T>(HttpMethod method, string path, object? payload, CancellationToken ct) where T : class
    {
        return await SendAsync<T>(method, path, payload, ct) ?? throw new HttpRequestException("Tunnel response was empty.");
    }
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? payload, CancellationToken ct) where T : class
    {
        using var response = await SendRawAsync(method, path, payload, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, ct);
    }
    private async Task SendNoContentAsync(HttpMethod method, string path, object? payload = null, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(method, path, payload, ct);
        await EnsureSuccessAsync(response, ct);
    }
    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null)
            throw new InvalidOperationException("RemoteOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), path.TrimStart('/')));
        if (payload is not null) request.Content = JsonContent.Create(payload, options: RemoteOsJsonOptions.Default);
        return await http.SendAsync(request, ct);
    }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(RemoteOsJsonOptions.Default, ct);
            if (!string.IsNullOrWhiteSpace(problem?.Title)) throw new TunnelRequestException(problem.Title);
        }
        catch (TunnelRequestException) { throw; }
        catch { }
        throw new TunnelRequestException($"tunnel.http_{(int)response.StatusCode}");
    }
    private static string ProfileRoute(Guid id) => TunnelApiRoutes.Profiles + "/" + id.ToString("D");
    private static string SecretRoute(Guid id) => ProfileRoute(id) + "/secret";
    private static string TunnelRoute(Guid id) => TunnelApiRoutes.Tunnels + "/" + id.ToString("D");
}

public sealed class TunnelRequestException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
