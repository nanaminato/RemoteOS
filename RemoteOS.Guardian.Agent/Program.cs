using RemoteOS.Guardian.Agent;

var options = GuardianAgentOptions.Load();
if (string.IsNullOrWhiteSpace(options.SharedSecret))
    throw new InvalidOperationException("REMOTEOS_GUARDIAN_SHARED_SECRET must be configured for the Guardian Agent.");

var supervisor = new WorkloadSupervisor(options);
await supervisor.RestoreEnabledWorkloadsAsync(CancellationToken.None);
await new GuardianPipeServer(options, supervisor).RunAsync(CancellationToken.None);
