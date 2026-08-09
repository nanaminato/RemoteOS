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
public sealed class DockerCliEngineService : IDockerEngineService
{
    private const int TimeoutSeconds = 10;

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
        if (!IsContainerId(request.Name) || !IsImageReference(request.Image) || request.Arguments.Count > 64 || request.Arguments.Any(argument => argument.Length > 4096 || argument.Contains('\0')))
            return new DockerOperationResult(false, "docker.validation_failed");
        var arguments = new List<string> { "create", "--name", request.Name, request.Image };
        arguments.AddRange(request.Arguments);
        var result = await RunAsync(arguments, cancellationToken);
        return new DockerOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result));
    }

    private static readonly IReadOnlyDictionary<string, string> AllowedActions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["start"] = "start", ["stop"] = "stop", ["restart"] = "restart", ["pause"] = "pause", ["unpause"] = "unpause", ["delete"] = "rm",
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
    private static string ToProblemCode(CommandResult result) => result.Error.Contains("not_found", StringComparison.OrdinalIgnoreCase) ? "docker.not_installed" : result.Error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "docker.permission_denied" : "docker.unavailable";
    private sealed record CommandResult(bool Success, string Output, string Error);
}
