using System.Security.Cryptography;
using System.Text.Json;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Workspace;

namespace Client.Services.Auth;

/// <summary>Persists an opted-in session for the current Windows user with DPAPI encryption.</summary>
public interface IRememberedSessionStore
{
    Task<RememberedSession?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(RememberedSession session, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed record RememberedSession(
    string ServerUrl,
    AuthTokens Tokens,
    UserDto User,
    WorkspaceDto Workspace,
    SessionDto Session,
    DeviceDto Device,
    DeviceRole AssignedRole)
{
    public static RememberedSession From(string serverUrl, LoginResponse response)
        => new(serverUrl, response.Tokens, response.User, response.Workspace, response.Session, response.Device, response.AssignedRole);

    public static RememberedSession From(string serverUrl, AuthSession session)
        => new(
            serverUrl,
            session.Tokens!,
            session.CurrentUser!,
            session.CurrentWorkspace!,
            session.CurrentSession!,
            session.CurrentDevice!,
            session.AssignedRole);

    public LoginResponse ToLoginResponse(AuthTokens tokens)
        => new(User, Workspace, Session, Device, tokens, AssignedRole);
}

public sealed class RememberedSessionStore : IRememberedSessionStore
{
    private static readonly byte[] Entropy = "RemoteOS.RememberedSession.v1"u8.ToArray();
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteOS",
        "remembered-session.bin");

    public async Task<RememberedSession?> LoadAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            if (!File.Exists(_filePath)) return null;
            var protectedBytes = await File.ReadAllBytesAsync(_filePath, ct);
            var json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<RememberedSession>(json, RemoteOsJsonOptions.Default);
        }
        catch (CryptographicException)
        {
            await ClearAsync(ct);
            return null;
        }
        catch (JsonException)
        {
            await ClearAsync(ct);
            return null;
        }
    }

    public async Task SaveAsync(RememberedSession session, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.SerializeToUtf8Bytes(session, RemoteOsJsonOptions.Default);
        var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_filePath, protectedBytes, ct);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(_filePath)) File.Delete(_filePath);
        return Task.CompletedTask;
    }
}
