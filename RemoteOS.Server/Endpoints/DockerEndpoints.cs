using RemoteOS.Protocol.Docker;

namespace Server.Endpoints;

public static class DockerEndpoints
{
    public static IEndpointRouteBuilder MapDockerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/docker").RequireAuthorization().WithTags("Docker");
        group.MapGet("/status", (Server.Docker.IDockerEngineService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapGet("/containers", (Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ListContainersAsync(ct));
        group.MapPost("/containers", (DockerContainerCreateRequest request, Server.Docker.IDockerEngineService service, CancellationToken ct) => service.CreateContainerAsync(request, ct));
        group.MapPost("/containers/{id}/{action}", (string id, string action, DockerContainerActionRequest request, Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ApplyContainerActionAsync(id, action, request, ct));
        group.MapGet("/images", (Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ListImagesAsync(ct));
        group.MapPost("/images/pull", (DockerImageOperationRequest request, Server.Docker.IDockerEngineService service, CancellationToken ct) => service.PullImageAsync(request, ct));
        group.MapDelete("/images/{id}", (string id, DockerImageOperationRequest request, Server.Docker.IDockerEngineService service, CancellationToken ct) => service.DeleteImageAsync(id, request, ct));
        group.MapGet("/networks", (Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ListNetworksAsync(ct));
        group.MapGet("/volumes", (Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ListVolumesAsync(ct));
        group.MapPost("/stacks/validate", (DockerStackDefinitionDto definition, Server.Docker.IDockerComposeService service, CancellationToken ct) => service.ValidateAsync(definition, ct));
        group.MapPost("/stacks/deploy", (DockerStackDefinitionDto definition, Server.Docker.IDockerComposeService service, CancellationToken ct) => service.DeployAsync(definition, ct));
        group.MapPost("/stacks/down", (DockerStackDefinitionDto definition, Server.Docker.IDockerComposeService service, CancellationToken ct) => service.DownAsync(definition, ct));
        return app;
    }
}
