using RemoteOS.Protocol.Docker;

namespace Server.Docker;

/// <summary>Controlled Compose executor. Callers supply structured definitions, never shell command strings.</summary>
public interface IDockerComposeService
{
    Task<DockerStackOperationResult> ValidateAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default);
    Task<DockerStackOperationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default);
    Task<DockerStackOperationResult> DownAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default);
}
