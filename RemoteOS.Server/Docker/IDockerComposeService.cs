using RemoteOS.Protocol.Docker;

namespace Server.Docker;

/// <summary>Controlled Compose executor. Callers supply structured definitions, never shell command strings.</summary>
public interface IDockerComposeService
{
    Task<IReadOnlyList<DockerStackDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<DockerStackOperationResult> ValidateAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default);
    Task<DockerStackOperationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default);
    Task<DockerStackDefinitionDto?> GetDefinitionAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerStackServiceDto>> ListServicesAsync(string name, CancellationToken cancellationToken = default);
    Task<DockerStackOperationResult> ApplyActionAsync(string name, string action, CancellationToken cancellationToken = default);
}
