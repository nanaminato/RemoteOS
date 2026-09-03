using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
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
        var systemProxyHost = NormalizeSystemProxyHost(request.SystemProxyHost);
        if (systemProxyHost is null) return ProxyProblemCodes.ConfigInvalid;
        var tun = request.Tun ?? ProxyTunSettingsDto.Default;
        if (!IsValidTunSettings(tun)) return ProxyProblemCodes.ConfigInvalid;
        var settings = new ProxySettingsDto(request.SystemProxyEnabled, request.AllowLan, request.DnsEnabled, request.Ipv6Enabled,
            request.UnifiedDelay, request.LogLevel.ToLowerInvariant(), request.MixedPort, request.AllowInsecureSubscriptionSources, systemProxyHost, tun);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = await ReadAsync(cancellationToken) ?? Defaults;
            var directory = paths.GetProtectedConfigurationDirectory();
            var active = Path.Combine(directory, "active.yaml");
            if (!File.Exists(active))
            {
                // Subscription trust is host-level rather than a Mihomo YAML option. Let an
                // operator change it before importing a subscription or installing the runtime.
                if (!CanPersistWithoutRuntime(previous, settings)) return ProxyProblemCodes.RuntimeNotInstalled;
                await WriteAsync(settings, cancellationToken);
                return null;
            }
            var original = await File.ReadAllTextAsync(active, cancellationToken);
            string updated;
            try
            {
                updated = MihomoManagedConfiguration.WithServerControllerSettings(
                    MihomoManagedConfiguration.WithServerGeoDataSettings(
                        MihomoManagedConfiguration.WithRuntimeSettings(
                            MihomoManagedConfiguration.WithManagedTunSettings(original, settings), settings)), controllerOptions,
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
                if (!ApplyWindowsSystemProxy(settings.SystemProxyEnabled, settings.SystemProxyHost, settings.MixedPort))
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
        try
        {
            await using var input = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<ProxySettingsDto>(input, cancellationToken: cancellationToken);
            return settings is null ? null : settings with { Tun = settings.Tun ?? ProxyTunSettingsDto.Default };
        }
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

    private static bool ApplyWindowsSystemProxy(bool enabled, string host, int port)
    {
        if (!OperatingSystem.IsWindows()) return !enabled;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
            if (key is null) return false;
            key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
            if (enabled)
            {
                var proxyHost = IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? "[" + host + "]"
                    : host;
                key.SetValue("ProxyServer", proxyHost + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
                key.SetValue("ProxyOverride", "<local>;127.0.0.1;localhost", RegistryValueKind.String);
            }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    private static bool CanPersistWithoutRuntime(ProxySettingsDto previous, ProxySettingsDto updated) =>
        previous with
        {
            AllowInsecureSubscriptionSources = updated.AllowInsecureSubscriptionSources,
            Tun = updated.Tun,
        } == updated;

    private static string? NormalizeSystemProxyHost(string? value)
    {
        if (!IPAddress.TryParse(value, out var address)) return null;
        if (IPAddress.IsLoopback(address)) return address.ToString();
        try
        {
            var isLocal = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address));
            return isLocal ? address.ToString() : null;
        }
        catch (NetworkInformationException) { return null; }
    }

    private static bool IsValidTunSettings(ProxyTunSettingsDto settings) =>
        settings.Stack is "system" or "gvisor" or "mixed"
        && Regex.IsMatch(settings.DeviceName, "^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)
        && settings.Mtu is >= 576 and <= 9000
        && IsValidDnsHijack(settings.DnsHijack)
        && (!settings.StrictRoute || settings.AutoRoute);

    private static bool IsValidDnsHijack(string value)
    {
        var match = Regex.Match(value, "^(?:(?:tcp|udp)://)?(?:any|[0-9A-Fa-f:.]+):(\\d{1,5})$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) && port is > 0 and <= 65535;
    }

    private static readonly ProxySettingsDto Defaults = new(false, false, true, true, false, "warning", 7890, false, "127.0.0.1", ProxyTunSettingsDto.Default);
}
