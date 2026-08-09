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
        return app;
    }
}
