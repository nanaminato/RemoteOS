using System.Diagnostics;
using RemoteOS.Protocol.ProcessGuardian;

namespace Server.ProcessGuardian;

/// <summary>Runs only a host-administrator configured Agent installer; it never accepts a command from HTTP.</summary>
public interface IGuardianAgentInstaller
{
    Task<GuardianInstallationPlanDto> CreatePlanAsync(CancellationToken cancellationToken = default);
    Task<GuardianOperationResult> ExecuteAsync(GuardianInstallationExecutionRequest request, CancellationToken cancellationToken = default);
}

public sealed class GuardianAgentInstaller(GuardianAgentInstallerOptions options) : IGuardianAgentInstaller
{
    public Task<GuardianInstallationPlanDto> CreatePlanAsync(CancellationToken cancellationToken = default)
    {
        var installed = !string.IsNullOrWhiteSpace(options.Command) && File.Exists(options.Command);
        var platform = OperatingSystem.IsWindows() ? "Windows SCM service" : "systemd service";
        return Task.FromResult(installed
            ? new GuardianInstallationPlanDto(true, string.Empty, [$"Install or repair the RemoteOS Guardian Agent as a {platform}.", "Verify local authenticated IPC after installation."], ["The configured installer runs under the host service account; RemoteOS never requests an administrator password."])
            : new GuardianInstallationPlanDto(false, "guardian.install_not_configured", [], ["Configure GuardianAgentInstaller:Command and Arguments in protected host configuration."]));
    }

    public async Task<GuardianOperationResult> ExecuteAsync(GuardianInstallationExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed) return new GuardianOperationResult(false, "guardian.confirmation_required");
        if (string.IsNullOrWhiteSpace(options.Command) || !File.Exists(options.Command)) return new GuardianOperationResult(false, "guardian.install_not_configured");
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(options.Command) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var argument in options.Arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new GuardianOperationResult(false, "guardian.install_start_failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? new GuardianOperationResult(true, string.Empty) : new GuardianOperationResult(false, "guardian.install_failed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new GuardianOperationResult(false, "guardian.install_timeout"); }
        catch (Exception) { return new GuardianOperationResult(false, "guardian.install_start_failed"); }
    }
}

public sealed class GuardianAgentInstallerOptions
{
    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
