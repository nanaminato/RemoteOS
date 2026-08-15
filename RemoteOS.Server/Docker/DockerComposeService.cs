using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using RemoteOS.Protocol.Docker;

namespace Server.Docker;

/// <summary>Runs only a small allow-list of Docker Compose operations in a temporary server-owned directory.</summary>
public sealed class DockerComposeService(IHostEnvironment environment) : IDockerComposeService
{
    private const int MaximumComposeBytes = 1024 * 1024;

    public Task<DockerStackOperationResult> ValidateAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
        => ExecuteAsync(definition, ["config", "--quiet"], cancellationToken);
    public Task<DockerStackOperationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
        => ExecuteAsync(definition, ["up", "--detach", "--remove-orphans"], cancellationToken);
    public async Task<IReadOnlyList<DockerStackDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["compose", "ls", "--format", "json"], cancellationToken);
        if (!result.Success) return [];
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Select(item => new DockerStackDto(Read(item, "Name"), Read(item, "Status"), Read(item, "ConfigFiles")))
                .Where(stack => !string.IsNullOrWhiteSpace(stack.Name))
                .OrderBy(stack => stack.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException) { return []; }
    }

    private async Task<DockerStackOperationResult> ExecuteAsync(DockerStackDefinitionDto definition, IReadOnlyList<string> composeCommand, CancellationToken cancellationToken)
    {
        if (!Validate(definition, out var problemCode)) return new DockerStackOperationResult(false, problemCode, []);
        var directory = Path.Combine(environment.ContentRootPath, "data", "docker-compose", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var composePath = Path.Combine(directory, "compose.yaml");
        try
        {
            await File.WriteAllTextAsync(composePath, definition.ComposeYaml, cancellationToken);
            var arguments = new List<string> { "compose", "--project-name", definition.Name, "--file", composePath };
            arguments.AddRange(composeCommand);
            var result = await RunAsync(arguments, cancellationToken);
            // Compose errors can echo substituted environment values. Clients receive only a
            // stable problem code; sanitized diagnostics belong in protected host auditing.
            return new DockerStackOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result.Error), result.Success ? ToLines(result.Output) : []);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort cleanup; never expose the path */ }
        }
    }

    private static bool Validate(DockerStackDefinitionDto definition, out string problem)
    {
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 63 || !definition.Name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) { problem = "docker.stack_invalid_name"; return false; }
        if (string.IsNullOrWhiteSpace(definition.ComposeYaml) || System.Text.Encoding.UTF8.GetByteCount(definition.ComposeYaml) > MaximumComposeBytes) { problem = "docker.stack_invalid_compose"; return false; }
        problem = string.Empty; return true;
    }

    private static async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new CommandResult(false, string.Empty, "start_failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token); var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(process.ExitCode == 0, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new CommandResult(false, string.Empty, "timeout"); }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException) { return new CommandResult(false, string.Empty, "not_found"); }
    }

    private static IReadOnlyList<string> ToLines(string message) => message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => line.Length <= 512 ? line : line[..512]).Take(20).ToArray();
    private static string Read(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    private static string ToProblemCode(string error) => error.Contains("not_found", StringComparison.OrdinalIgnoreCase) ? "docker.not_installed" : error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "docker.permission_denied" : "docker.compose_failed";
    private sealed record CommandResult(bool Success, string Output, string Error);
}
