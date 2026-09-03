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

        Process? process;
        try { process = Process.Start(start); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not start the privileged helper.");
            return new(false, 69, Error: "privileged helper could not be started");
        }
        if (process is null) return new(false, 69, Error: "privileged helper could not be started");
        using (process)
        {
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
                // A sudo rejection or a damaged apphost writes no protocol JSON. It is a helper
                // availability problem, not a file I/O failure. Keep stderr out of the HTTP response.
                logger.LogWarning("Privileged helper returned invalid output. ExitCode={ExitCode}; Stderr={Stderr}",
                    process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? "(empty)" : stderr);
                return new(false, 69, Error: "privileged helper failed; check the Server logs and sudoers configuration");
            }
        }
    }
}
