using RemoteOS.Protocol.Docker;

namespace Server.Docker;

public sealed class DockerRuntimeInstaller(IDockerEngineService engine, DockerRuntimeInstallerOptions options) : IDockerRuntimeInstaller
{
    public async Task<DockerInstallationPlanDto> CreatePlanAsync(CancellationToken cancellationToken = default)
    {
        var status = await engine.GetStatusAsync(cancellationToken);
        if (status.IsAvailable)
            return new DockerInstallationPlanDto(false, "docker.already_available", [], []);
        if (OperatingSystem.IsWindows())
            return new DockerInstallationPlanDto(true, status.ProblemCode,
                ["Review Docker Desktop licensing and choose WSL 2 or Hyper-V.", "Run the vendor-signed installer through the host OS elevation flow.", "Restart Docker Desktop and verify hello-world."],
                ["RemoteOS does not install Docker Desktop automatically or collect administrator credentials."]);
        return new DockerInstallationPlanDto(true, status.ProblemCode,
            ["Review the official Docker Engine installation instructions for the host distribution.", "Use the host OS package manager through an administrator-approved elevation flow.", "Verify the local engine with hello-world before using RemoteOS management."],
            ["Published Docker ports can bypass parts of host firewall policy. RemoteOS does not execute package-manager commands automatically."]);
    }

    public async Task<DockerOperationResult> ExecuteAsync(DockerInstallationExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed) return new DockerOperationResult(false, "docker.confirmation_required");
        if (string.IsNullOrWhiteSpace(options.Command)) return new DockerOperationResult(false, "docker.install_not_configured");
        try
        {
            using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(options.Command) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var argument in options.Arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new DockerOperationResult(false, "docker.install_start_failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? new DockerOperationResult(true, string.Empty) : new DockerOperationResult(false, "docker.install_failed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new DockerOperationResult(false, "docker.install_timeout"); }
        catch (Exception) { return new DockerOperationResult(false, "docker.install_start_failed"); }
    }
}

/// <summary>Host-admin configured, signed installer command. It is never supplied by an HTTP caller.</summary>
public sealed class DockerRuntimeInstallerOptions
{
    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
