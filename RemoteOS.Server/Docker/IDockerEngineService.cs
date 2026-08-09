using RemoteOS.Protocol.Docker;

namespace Server.Docker;

/// <summary>The only server boundary allowed to invoke the host's local Docker CLI/transport.</summary>
public interface IDockerEngineService
{
    Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerImageDto>> ListImagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerNetworkDto>> ListNetworksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerVolumeDto>> ListVolumesAsync(CancellationToken cancellationToken = default);
    Task<DockerOperationResult> ApplyContainerActionAsync(string containerId, string action, DockerContainerActionRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> PullImageAsync(DockerImageOperationRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> DeleteImageAsync(string imageId, DockerImageOperationRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> CreateContainerAsync(DockerContainerCreateRequest request, CancellationToken cancellationToken = default);
}
