using RemoteOS.Protocol.ProcessGuardian;

namespace Server.Endpoints;

public static class ProcessGuardianEndpoints
{
    public static IEndpointRouteBuilder MapProcessGuardianEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/guardian").RequireAuthorization().WithTags("Process Guardian");
        group.MapGet("/status", (Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapGet("/workloads", (Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ListWorkloadsAsync(ct));
        group.MapPost("/workloads", (ProcessDefinitionDto definition, Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.UpsertAsync(definition, ct));
        group.MapPost("/workloads/{id}/{action}", (string id, string action, Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ApplyActionAsync(id, action, ct));
        group.MapGet("/workloads/{id}/logs", (string id, Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ListLogsAsync(id, ct));
        group.MapGet("/audit", (Server.ProcessGuardian.IProcessGuardianService service, CancellationToken ct) => service.ListAuditAsync(ct));
        group.MapGet("/services", (Server.ProcessGuardian.INativeServiceAdapter services, CancellationToken ct) => services.ListAsync(ct));
        group.MapPost("/services/{id}/{action}", (string id, string action, NativeServiceActionRequest request, Server.ProcessGuardian.INativeServiceAdapter services, CancellationToken ct) => services.ApplyActionAsync(id, action, request, ct));
        group.MapPost("/agent/installation/plan", (Server.ProcessGuardian.IGuardianAgentInstaller installer, CancellationToken ct) => installer.CreatePlanAsync(ct));
        group.MapPost("/agent/installation/execute", (GuardianInstallationExecutionRequest request, Server.ProcessGuardian.IGuardianAgentInstaller installer, CancellationToken ct) => installer.ExecuteAsync(request, ct));
        return app;
    }
}
