using System.Diagnostics;
using System.Text.Json;
using RemoteOS.Protocol.Privileged;

namespace Server.Privileged;

/// <summary>Runs the installed helper. Linux uses its dedicated passwordless sudoers rule.</summary>
public sealed class LocalPrivilegedOperationRunner(PrivilegedHelperOptions options, ILogger<LocalPrivilegedOperationRunner> logger) : IPrivilegedOperationRunner
{
    public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.HelperPath) || !File.Exists(options.HelperPath))
            return new(false, 69, Error: "privileged helper is not installed");

        var start = OperatingSystem.IsLinux()
            ? new ProcessStartInfo(options.SudoPath) { ArgumentList = { "-n", options.HelperPath } }
            : new ProcessStartInfo(options.HelperPath);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        using var process = Process.Start(start);
        if (process is null) return new(false, 1, Error: "privileged helper could not be started");
        await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, cancellationToken: cancellationToken);
        await process.StandardInput.DisposeAsync();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new(false, 124, Error: "privileged helper timed out");
        }

        var response = await output;
        var stderr = await error;
        try
        {
            return JsonSerializer.Deserialize<PrivilegedOperationResult>(response)
                   ?? new(false, process.ExitCode, Error: "privileged helper returned no result");
        }
        catch (JsonException)
        {
            logger.LogWarning("Privileged helper exited with {ExitCode}; stderr omitted from API.", process.ExitCode);
            return new(false, process.ExitCode, Error: string.IsNullOrWhiteSpace(stderr) ? "privileged helper failed" : "privileged helper failed");
        }
    }
}
