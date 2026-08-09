using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteOS.Guardian.Agent;

/// <summary>Runs the IPC endpoint and all supervision loops under the OS service lifetime.</summary>
internal sealed class GuardianWorker(
    WorkloadSupervisor supervisor,
    GuardianPipeServer pipeServer,
    ProtectedServerMonitor protectedServerMonitor,
    ILogger<GuardianWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await supervisor.RestoreEnabledWorkloadsAsync(stoppingToken);
            await Task.WhenAll(
                pipeServer.RunAsync(stoppingToken),
                supervisor.RunHealthChecksAsync(stoppingToken),
                protectedServerMonitor.RunAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal systemd/SCM shutdown.
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "RemoteOS Guardian Agent terminated unexpectedly.");
            throw;
        }
    }
}
