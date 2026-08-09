namespace RemoteOS.Protocol.Docker;

public sealed record DockerContainerDto(string Id, string Names, string Image, string State, string Status);
public sealed record DockerImageDto(string Id, string Repository, string Tag, string Size, string CreatedSince);
public sealed record DockerNetworkDto(string Id, string Name, string Driver, string Scope);
public sealed record DockerVolumeDto(string Name, string Driver, string Mountpoint);

/// <summary>Structured container lifecycle request. Confirmation is required for irreversible actions.</summary>
public sealed record DockerContainerActionRequest(bool Force = false, bool Confirmed = false);

/// <summary>Stable result that does not expose Docker daemon error text to clients.</summary>
public sealed record DockerOperationResult(bool Success, string ProblemCode);
public sealed record DockerImageOperationRequest(string ImageReference, bool Confirmed = false);
public sealed record DockerContainerCreateRequest(string Name, string Image, IReadOnlyList<string> Arguments);
