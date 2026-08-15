using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using RemoteOS.Protocol.Docker;

namespace Server.Docker;

/// <summary>
/// Local-only Docker provider. Docker itself selects the platform-specific named pipe or Unix
/// socket, keeping all transport details out of endpoints and clients. It deliberately uses
/// fixed argument lists (never user-provided shell strings).
/// </summary>
public sealed class DockerCliEngineService(DockerCliEngineOptions options) : IDockerEngineService
{
    private const int TimeoutSeconds = 10;
    private const int MaxArchiveBytes = 64 * 1024 * 1024;

    public async Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["version", "--format", "{{json .Server}}"], cancellationToken);
        if (!result.Success)
            return new DockerStatusDto(false, ToProblemCode(result), null, null, null);

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            return new DockerStatusDto(true, "", Read(root, "Version"), Read(root, "Os"), Read(root, "Arch"));
        }
        catch (JsonException)
        {
            return new DockerStatusDto(false, "docker.api_incompatible", null, null, null);
        }
    }

    public async Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken = default)
        => (await RunTableAsync(["ps", "-a", "--format", "{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.State}}\t{{.Status}}"], cancellationToken))
            .Select(row => new DockerContainerDto(Value(row, 0), Value(row, 1), Value(row, 2), Value(row, 3), Value(row, 4))).ToArray();

    public async Task<IReadOnlyList<DockerImageDto>> ListImagesAsync(CancellationToken cancellationToken = default)
        => (await RunTableAsync(["image", "ls", "--format", "{{.ID}}\t{{.Repository}}\t{{.Tag}}\t{{.Size}}\t{{.CreatedSince}}"], cancellationToken))
            .Select(row => new DockerImageDto(Value(row, 0), Value(row, 1), Value(row, 2), Value(row, 3), Value(row, 4))).ToArray();

    public async Task<IReadOnlyList<DockerNetworkDto>> ListNetworksAsync(CancellationToken cancellationToken = default)
        => (await RunTableAsync(["network", "ls", "--format", "{{.ID}}\t{{.Name}}\t{{.Driver}}\t{{.Scope}}"], cancellationToken))
            .Select(row => new DockerNetworkDto(Value(row, 0), Value(row, 1), Value(row, 2), Value(row, 3))).ToArray();

    public async Task<IReadOnlyList<DockerVolumeDto>> ListVolumesAsync(CancellationToken cancellationToken = default)
        => (await RunTableAsync(["volume", "ls", "--format", "{{.Name}}\t{{.Driver}}\t{{.Mountpoint}}"], cancellationToken))
            .Select(row => new DockerVolumeDto(Value(row, 0), Value(row, 1), Value(row, 2))).ToArray();

    public async Task<DockerOperationResult> ApplyContainerActionAsync(string containerId, string action, DockerContainerActionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsContainerId(containerId) || !AllowedActions.TryGetValue(action, out var command))
            return new DockerOperationResult(false, "docker.validation_failed");
        if ((action == "delete" || action == "stop" && request.Force) && !request.Confirmed)
            return new DockerOperationResult(false, "docker.confirmation_required");

        var arguments = new List<string> { command };
        if (action == "delete" && request.Force) arguments.Add("--force");
        if (action == "stop" && request.Force) arguments.Add("--time=0");
        arguments.Add(containerId);
        var result = await RunAsync(arguments, cancellationToken);
        return result.Success
            ? new DockerOperationResult(true, string.Empty)
            : new DockerOperationResult(false, ToProblemCode(result));
    }

    public async Task<DockerOperationResult> PullImageAsync(DockerImageOperationRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsImageReference(request.ImageReference)) return new DockerOperationResult(false, "docker.validation_failed");
        var result = await RunAsync(["pull", request.ImageReference], cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    public async Task<DockerOperationResult> DeleteImageAsync(string imageId, DockerImageOperationRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed) return new DockerOperationResult(false, "docker.confirmation_required");
        if (!IsContainerId(imageId)) return new DockerOperationResult(false, "docker.validation_failed");
        var result = await RunAsync(["image", "rm", imageId], cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    public async Task<DockerOperationResult> CreateContainerAsync(DockerContainerCreateRequest request, CancellationToken cancellationToken = default)
    {
        var ports = request.Ports ?? [];
        var environment = request.Environment ?? [];
        var mounts = request.Mounts ?? [];
        if (!IsContainerId(request.Name) || !IsImageReference(request.Image) || request.Arguments.Count > 64 || request.Arguments.Any(argument => !IsOptionValue(argument)) ||
            ports.Count > 32 || environment.Count > 64 || mounts.Count > 32 ||
            ports.Any(port => !IsOptionValue(port)) || environment.Any(variable => !IsOptionValue(variable)) || mounts.Any(mount => !IsOptionValue(mount)) ||
            request.Network is { Length: > 0 } network && !IsContainerId(network) ||
            request.RestartPolicy is { Length: > 0 } restartPolicy && !AllowedRestartPolicies.Contains(restartPolicy))
            return new DockerOperationResult(false, "docker.validation_failed");

        var arguments = new List<string> { "create", "--name", request.Name };
        foreach (var port in ports) { arguments.Add("--publish"); arguments.Add(port); }
        foreach (var variable in environment) { arguments.Add("--env"); arguments.Add(variable); }
        foreach (var mount in mounts) { arguments.Add("--volume"); arguments.Add(mount); }
        if (!string.IsNullOrWhiteSpace(request.Network)) { arguments.Add("--network"); arguments.Add(request.Network); }
        if (!string.IsNullOrWhiteSpace(request.RestartPolicy)) { arguments.Add("--restart"); arguments.Add(request.RestartPolicy); }
        arguments.Add(request.Image);
        arguments.AddRange(request.Arguments);
        var result = await RunAsync(arguments, cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    public async Task<DockerNetworkDetailsDto?> GetNetworkAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsContainerId(id)) return null;
        var result = await RunAsync(["network", "inspect", id, "--format", "{{json .}}"], cancellationToken);
        if (!result.Success) return null;
        try
        {
            using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
            var containers = root.TryGetProperty("Containers", out var containerMap) && containerMap.ValueKind == JsonValueKind.Object
                ? containerMap.EnumerateObject().Select(property => property.Value.TryGetProperty("Name", out var name) ? name.GetString() ?? property.Name : property.Name).ToArray() : [];
            return new DockerNetworkDetailsDto(Read(root, "Id"), Read(root, "Name"), Read(root, "Driver"), Read(root, "Scope"), containers);
        }
        catch (JsonException) { return null; }
    }

    public async Task<DockerVolumeDetailsDto?> GetVolumeAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsContainerId(name)) return null;
        var result = await RunAsync(["volume", "inspect", name, "--format", "{{json .}}"], cancellationToken);
        if (!result.Success) return null;
        try
        {
            using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
            var labels = root.TryGetProperty("Labels", out var labelMap) && labelMap.ValueKind == JsonValueKind.Object
                ? labelMap.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal) : new Dictionary<string, string>();
            return new DockerVolumeDetailsDto(Read(root, "Name"), Read(root, "Driver"), Read(root, "Mountpoint"), labels);
        }
        catch (JsonException) { return null; }
    }

    public async Task<DockerOperationResult> CreateNetworkAsync(DockerNetworkCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsContainerId(request.Name) || !IsContainerId(request.Driver)) return new DockerOperationResult(false, "docker.validation_failed");
        var result = await RunAsync(["network", "create", "--driver", request.Driver, request.Name], cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    public async Task<DockerOperationResult> CreateVolumeAsync(DockerVolumeCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsContainerId(request.Name) || !IsContainerId(request.Driver)) return new DockerOperationResult(false, "docker.validation_failed");
        var result = await RunAsync(["volume", "create", "--driver", request.Driver, request.Name], cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    public async Task<DockerOperationResult> DeleteNetworkAsync(string id, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) return new DockerOperationResult(false, "docker.confirmation_required");
        if (!IsContainerId(id)) return new DockerOperationResult(false, "docker.validation_failed");
        var result = await RunAsync(["network", "rm", id], cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    public async Task<DockerOperationResult> DeleteVolumeAsync(string name, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) return new DockerOperationResult(false, "docker.confirmation_required");
        if (!IsContainerId(name)) return new DockerOperationResult(false, "docker.validation_failed");
        var result = await RunAsync(["volume", "rm", name], cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    public async Task<DockerContainerLogsDto?> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken = default)
    {
        if (!IsContainerId(id) || tail is < 1 or > 1000) return null;
        var result = await RunAsync(["logs", "--timestamps", "--tail", tail.ToString(System.Globalization.CultureInfo.InvariantCulture), id], cancellationToken);
        if (!result.Success) return null;
        var lines = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new DockerContainerLogsDto(lines, lines.Length == tail);
    }

    public async Task<DockerContainerStatsDto?> GetContainerStatsAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsContainerId(id)) return null;
        var rows = await RunTableAsync(["stats", "--no-stream", "--format", "{{.ID}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.NetIO}}\t{{.BlockIO}}", id], cancellationToken);
        var row = rows.FirstOrDefault();
        return row is null ? null : new DockerContainerStatsDto(Value(row, 0), Value(row, 1), Value(row, 2), Value(row, 3), Value(row, 4));
    }

    public async Task<DockerOperationResult> BuildImageAsync(DockerBuildRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsImageReference(request.ImageReference) || !IsBuildPathAllowed(request.ContextDirectory, out var contextDirectory))
            return new DockerOperationResult(false, "docker.validation_failed");
        var arguments = new List<string> { "build", "--tag", request.ImageReference };
        if (!string.IsNullOrWhiteSpace(request.Dockerfile))
        {
            var dockerfile = Path.GetFullPath(request.Dockerfile);
            if (!File.Exists(dockerfile) || !IsPathWithin(dockerfile, contextDirectory)) return new DockerOperationResult(false, "docker.validation_failed");
            arguments.Add("--file"); arguments.Add(dockerfile);
        }
        arguments.Add(contextDirectory);
        var result = await RunAsync(arguments, cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    public async Task<DockerImageArchiveDto?> ExportImageAsync(string imageId, CancellationToken cancellationToken = default)
    {
        if (!IsImageReference(imageId)) return null;
        var archivePath = Path.Combine(Path.GetTempPath(), $"remoteos-docker-{Guid.NewGuid():N}.tar");
        try
        {
            var result = await RunAsync(["image", "save", "--output", archivePath, imageId], cancellationToken);
            if (!result.Success || !File.Exists(archivePath)) return null;
            var size = new FileInfo(archivePath).Length;
            if (size > MaxArchiveBytes) return null;
            return new DockerImageArchiveDto(imageId, Convert.ToBase64String(await File.ReadAllBytesAsync(archivePath, cancellationToken)));
        }
        finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    }

    public async Task<DockerOperationResult> ImportImageAsync(DockerImageArchiveDto archive, CancellationToken cancellationToken = default)
    {
        if (!IsImageReference(archive.ImageReference) || string.IsNullOrWhiteSpace(archive.ContentBase64) || archive.ContentBase64.Length > MaxArchiveBytes * 4 / 3 + 8)
            return new DockerOperationResult(false, "docker.validation_failed");
        byte[] content;
        try { content = Convert.FromBase64String(archive.ContentBase64); }
        catch (FormatException) { return new DockerOperationResult(false, "docker.validation_failed"); }
        if (content.Length > MaxArchiveBytes) return new DockerOperationResult(false, "docker.archive_too_large");
        var archivePath = Path.Combine(Path.GetTempPath(), $"remoteos-docker-{Guid.NewGuid():N}.tar");
        try
        {
            await File.WriteAllBytesAsync(archivePath, content, cancellationToken);
            var result = await RunAsync(["image", "load", "--input", archivePath], cancellationToken);
            return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
        }
        finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    }

    private static readonly IReadOnlyDictionary<string, string> AllowedActions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["start"] = "start", ["stop"] = "stop", ["restart"] = "restart", ["pause"] = "pause", ["unpause"] = "unpause", ["delete"] = "rm",
    };
    private static readonly HashSet<string> AllowedRestartPolicies = new(StringComparer.Ordinal)
    {
        "no", "always", "unless-stopped", "on-failure"
    };

    private async Task<IReadOnlyList<string[]>> RunTableAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(arguments, cancellationToken);
        if (!result.Success) return Array.Empty<string[]>();
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t')).ToArray();
    }

    private static async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new CommandResult(false, "", "start_failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(process.ExitCode == 0, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new CommandResult(false, "", "timeout"); }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException) { return new CommandResult(false, "", "not_found"); }
    }

    private static string Read(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    private static string Value(IReadOnlyList<string> row, int index) => index < row.Count ? row[index] : string.Empty;
    private static bool IsContainerId(string value) => value.Length is >= 3 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    private static bool IsImageReference(string value) => value.Length is >= 1 and <= 255 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or ':' or '.' or '_' or '-');
    private static bool IsOptionValue(string value) => value.Length is >= 1 and <= 4096 && !value.Contains('\0') && !value.Any(char.IsControl);
    private bool IsBuildPathAllowed(string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || options.BuildRoots.Count == 0) return false;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        var candidate = fullPath;
        return Directory.Exists(candidate) && options.BuildRoots.Any(root => IsPathWithin(candidate, Path.GetFullPath(root)));
    }
    private static bool IsPathWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
    private static string ToProblemCode(CommandResult result) => result.Error.Contains("not_found", StringComparison.OrdinalIgnoreCase) ? "docker.not_installed" : result.Error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "docker.permission_denied" : "docker.unavailable";
    private sealed record CommandResult(bool Success, string Output, string Error);
}

/// <summary>Host-admin approved source roots for Docker builds; an API caller cannot read arbitrary paths.</summary>
public sealed class DockerCliEngineOptions
{
    public IReadOnlyList<string> BuildRoots { get; init; } = [];
}
