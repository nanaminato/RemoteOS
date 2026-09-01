using System.Text.Json;
using Microsoft.Win32;
using RemoteOS.Protocol.Proxy;

namespace Server.Proxy.Mihomo;

/// <summary>Applies the small, safe set of Manager-owned Mihomo options to the protected config.</summary>
public sealed class MihomoSettingsService(
    IProxyPlatformPaths paths,
    IMihomoControllerClient controller,
    IProxyControllerSecretStore controllerSecrets,
    MihomoControllerOptions controllerOptions) : IProxySettingsService
{
    private static readonly HashSet<string> LogLevels = new(StringComparer.OrdinalIgnoreCase) { "silent", "error", "warning", "info", "debug" };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
        await ReadAsync(cancellationToken) ?? Defaults;

    public async Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.MixedPort is < 1 or > 65535 || !LogLevels.Contains(request.LogLevel)) return ProxyProblemCodes.ConfigInvalid;
        if (request.SystemProxyEnabled && !OperatingSystem.IsWindows()) return ProxyProblemCodes.NotSupported;
        var settings = new ProxySettingsDto(request.SystemProxyEnabled, request.AllowLan, request.DnsEnabled, request.Ipv6Enabled,
            request.UnifiedDelay, request.LogLevel.ToLowerInvariant(), request.MixedPort);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = paths.GetProtectedConfigurationDirectory();
            var active = Path.Combine(directory, "active.yaml");
            if (!File.Exists(active)) return ProxyProblemCodes.RuntimeNotInstalled;
            var original = await File.ReadAllTextAsync(active, cancellationToken);
            string updated;
            try
            {
                updated = MihomoManagedConfiguration.WithServerControllerSettings(
                    MihomoManagedConfiguration.WithRuntimeSettings(original, settings), controllerOptions,
                    await controllerSecrets.GetOrCreateAsync(cancellationToken));
            }
            catch (ProxyControllerSecretException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }

            var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temporary, updated, cancellationToken);
                File.Move(temporary, active, overwrite: true);
                var controllerAvailable = (await controller.IsReachableAsync(cancellationToken)).Succeeded;
                var reload = controllerAvailable ? await controller.ReloadAsync(cancellationToken) : null;
                if (controllerAvailable && !string.IsNullOrEmpty(reload))
                {
                    await File.WriteAllTextAsync(active, original, cancellationToken);
                    await controller.ReloadAsync(cancellationToken);
                    return ProxyProblemCodes.ConfigApplyFailed;
                }
                if (!ApplyWindowsSystemProxy(settings.SystemProxyEnabled, settings.MixedPort))
                {
                    await File.WriteAllTextAsync(active, original, cancellationToken);
                    if (controllerAvailable) await controller.ReloadAsync(cancellationToken);
                    return ProxyProblemCodes.PrivilegedOperationUnavailable;
                }
                await WriteAsync(settings, cancellationToken);
                return null;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (IOException) { return ProxyProblemCodes.ConfigApplyFailed; }
        catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
        finally { _gate.Release(); }
    }

    private async Task<ProxySettingsDto?> ReadAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
        if (!File.Exists(path)) return null;
        try { await using var input = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<ProxySettingsDto>(input, cancellationToken: cancellationToken); }
        catch (JsonException) { return null; }
    }

    private async Task WriteAsync(ProxySettingsDto settings, CancellationToken cancellationToken)
    {
        var directory = paths.GetStateDirectory(); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "mihomo-settings.json");
        var temporary = path + ".new";
        await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, settings, cancellationToken: cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool ApplyWindowsSystemProxy(bool enabled, int port)
    {
        if (!OperatingSystem.IsWindows()) return !enabled;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
            if (key is null) return false;
            key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
            if (enabled)
            {
                key.SetValue("ProxyServer", "127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                key.SetValue("ProxyOverride", "<local>;127.0.0.1;localhost", RegistryValueKind.String);
            }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    private static readonly ProxySettingsDto Defaults = new(false, false, true, true, false, "warning", 7890);
}
