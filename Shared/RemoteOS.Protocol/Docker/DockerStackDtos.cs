namespace RemoteOS.Protocol.Docker;

/// <summary>Compose input kept structured at the API boundary; it is never treated as shell input.</summary>
public sealed record DockerStackDefinitionDto(string Name, string ComposeYaml);

/// <summary>Compose project reported by the local Docker CLI.</summary>
public sealed record DockerStackDto(string Name, string Status, string ConfigFiles);

/// <summary>Bounded diagnostic result for a Compose operation.</summary>
public sealed record DockerStackOperationResult(bool Success, string ProblemCode, IReadOnlyList<string> Messages);
