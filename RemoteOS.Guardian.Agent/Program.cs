using RemoteOS.Guardian.Agent;

var options = GuardianAgentOptions.Load();
if (string.IsNullOrWhiteSpace(options.SharedSecret))
    throw new InvalidOperationException("REMOTEOS_GUARDIAN_SHARED_SECRET must be configured for the Guardian Agent.");

var supervisor = new WorkloadSupervisor(options);
await supervisor.RestoreEnabledWorkloadsAsync(CancellationToken.None);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; shutdown.Cancel(); };
var healthChecks = supervisor.RunHealthChecksAsync(shutdown.Token);
await new GuardianPipeServer(options, supervisor).RunAsync(shutdown.Token);
await healthChecks;
