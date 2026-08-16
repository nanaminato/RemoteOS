namespace RemoteOS.Protocol.Docker;

/// <summary>Compose input kept structured at the API boundary; it is never treated as shell input.</summary>
public sealed record DockerStackDefinitionDto(string Name, string ComposeYaml);

/// <summary>Compose project reported by the local Docker CLI.</summary>
public sealed record DockerStackDto(string Name, string Status, string ConfigFiles);

/// <summary>A Compose-managed container, grouped by its Compose service.</summary>
public sealed record DockerStackServiceDto(string Service, string Container, string Image, string State, string Status);

/// <summary>Bounded diagnostic result for a Compose operation.</summary>
public sealed record DockerStackOperationResult(bool Success, string ProblemCode, IReadOnlyList<string> Messages);
