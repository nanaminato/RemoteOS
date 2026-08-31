using System.Diagnostics;
using RemoteOS.Protocol.Proxy;

namespace Server.Proxy.Platform;

/// <summary>
/// The deployable constrained host boundary for Mihomo. It has no generic command or file API:
/// every executable, service name, argument and target path is derived from fixed constants.
/// Calling it without the required OS rights safely returns a stable unavailable result.
/// </summary>
public sealed class NativeMihomoPrivilegedOperations(IProxyPlatformPaths paths) : IProxyPrivilegedOperations
{
    private const string Engine = Mihomo.MihomoEngine.Id;
    private const string Service = "remoteos-mihomo";
    private const string ConfigId = "mihomo-default";
    private const string ActiveLink = "current";

    public async Task<ProxyPrivilegedResult> InstallRuntimeAsync(InstallProxyRuntimeOperation request, CancellationToken cancellationToken) =>
        await ActivateRuntimeAsync(request.EngineId, request.Version, request.ReleaseDirectoryId, cancellationToken);
    public async Task<ProxyPrivilegedResult> ReplaceRuntimeAsync(ReplaceProxyRuntimeOperation request, CancellationToken cancellationToken) =>
        await ActivateRuntimeAsync(request.EngineId, request.Version, request.ReleaseDirectoryId, cancellationToken);
    public async Task<ProxyPrivilegedResult> RemoveRuntimeAsync(RemoveProxyRuntimeOperation request, CancellationToken cancellationToken)
    {
        if (request.EngineId != Engine) return Invalid();
        try
        {
            var active = ActivePath();
            if (Directory.Exists(active) || File.Exists(active)) Directory.Delete(active, recursive: true);
            var versions = paths.GetEngineVersionsDirectory(Engine);
            if (Directory.Exists(versions)) Directory.Delete(versions, recursive: true);
            return Success();
        }
        catch (IOException) { return Unavailable(); }
        catch (UnauthorizedAccessException) { return Unavailable(); }
    }

    public async Task<ProxyPrivilegedResult> InstallServiceAsync(InstallProxyServiceOperation request, CancellationToken cancellationToken)
    {
        if (!IsServiceRequest(request.EngineId, request.ServiceName) || request.ConfigurationId != ConfigId) return Invalid();
        if (OperatingSystem.IsLinux())
        {
            try
            {
                await File.WriteAllTextAsync(SystemdUnitPath, SystemdUnit(), cancellationToken);
                var reload = await SystemctlAsync(["daemon-reload"], cancellationToken);
                return reload.Succeeded ? await SystemctlAsync(["enable", Service], cancellationToken) : reload;
            }
            catch (IOException) { return Unavailable(); }
            catch (UnauthorizedAccessException) { return Unavailable(); }
        }
        if (OperatingSystem.IsWindows())
        {
            var binary = ActiveBinaryPath();
            return await ScAsync(["create", Service, "binPath=", QuoteWindows(binary), "start=", "auto"], cancellationToken);
        }
        return Unsupported();
    }

    public async Task<ProxyPrivilegedResult> RemoveServiceAsync(RemoveProxyServiceOperation request, CancellationToken cancellationToken)
    {
        if (!IsServiceRequest(request.EngineId, request.ServiceName)) return Invalid();
        if (OperatingSystem.IsLinux())
        {
            var disable = await SystemctlAsync(["disable", Service], cancellationToken);
            try { if (File.Exists(SystemdUnitPath)) File.Delete(SystemdUnitPath); }
            catch (IOException) { return Unavailable(); }
            catch (UnauthorizedAccessException) { return Unavailable(); }
            var reload = await SystemctlAsync(["daemon-reload"], cancellationToken);
            return disable.Succeeded || reload.Succeeded ? Success() : disable;
        }
        if (OperatingSystem.IsWindows()) return await ScAsync(["delete", Service], cancellationToken);
        return Unsupported();
    }

    public Task<ProxyPrivilegedResult> SetServiceStartupAsync(SetProxyServiceStartupOperation request, CancellationToken cancellationToken)
    {
        if (!IsServiceRequest(request.EngineId, request.ServiceName)) return Task.FromResult(Invalid());
        if (OperatingSystem.IsLinux()) return SystemctlAsync([request.Enabled ? "enable" : "disable", Service], cancellationToken);
        if (OperatingSystem.IsWindows()) return ScAsync(["config", Service, "start=", request.Enabled ? "auto" : "demand"], cancellationToken);
        return Task.FromResult(Unsupported());
    }
    public Task<ProxyPrivilegedResult> StartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => ServiceActionAsync(request, "start", cancellationToken);
    public Task<ProxyPrivilegedResult> StopServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => ServiceActionAsync(request, "stop", cancellationToken);
    public Task<ProxyPrivilegedResult> RestartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => ServiceActionAsync(request, "restart", cancellationToken);
    public Task<ProxyPrivilegedResult> RepairServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => ServiceActionAsync(request, "try-restart", cancellationToken);

    public Task<ProxyPrivilegedResult> WriteProtectedConfigurationAsync(WriteProxyConfigurationOperation request, CancellationToken cancellationToken) =>
        Task.FromResult(request.EngineId == Engine && request.ConfigurationId == ConfigId ? Unavailable() : Invalid());
    public Task<ProxyPrivilegedResult> RestoreNetworkConfigurationAsync(RestoreProxyNetworkOperation request, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(request.RecoveryMarkerId) ? Invalid() : Unavailable());

    private async Task<ProxyPrivilegedResult> ActivateRuntimeAsync(string engineId, string version, string releaseDirectoryId, CancellationToken cancellationToken)
    {
        if (engineId != Engine || !IsRelease(version, releaseDirectoryId)) return Invalid();
        var release = Path.Combine(paths.GetEngineVersionsDirectory(Engine), releaseDirectoryId);
        if (!File.Exists(Path.Combine(release, BinaryName()))) return new(false, ProxyProblemCodes.RuntimeNotInstalled);
        try
        {
            var active = ActivePath();
            if (OperatingSystem.IsLinux())
            {
                var temporary = active + ".new";
                if (Directory.Exists(temporary) || File.Exists(temporary)) Directory.Delete(temporary, recursive: true);
                Directory.CreateSymbolicLink(temporary, release);
                File.Move(temporary, active, overwrite: true);
            }
            else if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(active + ".txt", releaseDirectoryId, cancellationToken);
            }
            else return Unsupported();
            return Success();
        }
        catch (IOException) { return Unavailable(); }
        catch (UnauthorizedAccessException) { return Unavailable(); }
    }

    private Task<ProxyPrivilegedResult> ServiceActionAsync(ProxyServiceOperation request, string action, CancellationToken cancellationToken)
    {
        if (!IsServiceRequest(request.EngineId, request.ServiceName)) return Task.FromResult(Invalid());
        if (OperatingSystem.IsLinux()) return SystemctlAsync([action, Service], cancellationToken);
        if (OperatingSystem.IsWindows()) return ScAsync([action == "try-restart" ? "start" : action, Service], cancellationToken);
        return Task.FromResult(Unsupported());
    }
    private static bool IsServiceRequest(string engineId, string serviceName) => engineId == Engine && serviceName == Service;
    private static bool IsRelease(string version, string releaseId) => version == Mihomo.MihomoRuntimeManifest.SupportedVersion && releaseId == version + "-" + Mihomo.MihomoRuntimeManifest.CurrentRid();
    private string ActivePath() => Path.Combine(paths.GetEngineVersionsDirectory(Engine), ActiveLink);
    private string ActiveBinaryPath() => Path.Combine(ActivePath(), BinaryName());
    private string SystemdUnitPath => "/etc/systemd/system/remoteos-mihomo.service";
    private string SystemdUnit() => string.Join('\n',
        "[Unit]",
        "Description=RemoteOS managed Mihomo",
        "After=network-online.target",
        "Wants=network-online.target",
        "",
        "[Service]",
        "Type=simple",
        $"ExecStart={ActiveBinaryPath()} -f {Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml")}",
        "Restart=on-failure",
        "RestartSec=3",
        "NoNewPrivileges=true",
        "PrivateTmp=true",
        "",
        "[Install]",
        "WantedBy=multi-user.target",
        "");
    private static string BinaryName() => OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo";
    private static string QuoteWindows(string value) => "\"" + value.Replace("\"", "", StringComparison.Ordinal) + "\"";
    private static Task<ProxyPrivilegedResult> SystemctlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) => RunFixedAsync("/usr/bin/systemctl", arguments, cancellationToken);
    private static Task<ProxyPrivilegedResult> ScAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) => RunFixedAsync("sc.exe", arguments, cancellationToken);
    private static async Task<ProxyPrivilegedResult> RunFixedAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return Unavailable();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? Success() : Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch { return Unavailable(); }
    }
    private static ProxyPrivilegedResult Success() => new(true);
    private static ProxyPrivilegedResult Invalid() => new(false, ProxyProblemCodes.NotSupported);
    private static ProxyPrivilegedResult Unavailable() => new(false, ProxyProblemCodes.PrivilegedOperationUnavailable);
    private static ProxyPrivilegedResult Unsupported() => new(false, ProxyProblemCodes.RuntimeUnsupportedPlatform);
}
