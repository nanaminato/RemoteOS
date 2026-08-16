using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using RemoteOS.Protocol.Docker;

namespace Server.Docker;

/// <summary>Runs only a small allow-list of Docker Compose operations from server-owned files.</summary>
public sealed class DockerComposeService(IHostEnvironment environment) : IDockerComposeService
{
    private const int MaximumComposeBytes = 1024 * 1024;

    public Task<DockerStackOperationResult> ValidateAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
        => ExecuteAsync(definition, ["config", "--quiet"], persistSource: false, cancellationToken: cancellationToken);
    public Task<DockerStackOperationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
        => ExecuteAsync(definition, ["up", "--detach", "--remove-orphans"], persistSource: true, cancellationToken: cancellationToken);

    /// <summary>
    /// Reads container labels as belonging to a Compose project. This also works for projects
    /// started outside RemoteOS and for projects whose containers have all been stopped.
    /// </summary>
    public async Task<IReadOnlyList<DockerStackServiceDto>> ListServicesAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsProjectName(name)) return [];
        var result = await RunAsync(["ps", "--all", "--filter", $"label=com.docker.compose.project={name}", "--format", "{{.Label \\\"com.docker.compose.service\\\"}}\\t{{.Names}}\\t{{.Image}}\\t{{.State}}\\t{{.Status}}"], cancellationToken);
        if (!result.Success) return [];
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Select(row => new DockerStackServiceDto(Value(row, 0), Value(row, 1), Value(row, 2), Value(row, 3), Value(row, 4)))
            .OrderBy(service => service.Service, StringComparer.OrdinalIgnoreCase)
            .ThenBy(service => service.Container, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Applies a safe lifecycle action to every container labelled for the project.</summary>
    public async Task<DockerStackOperationResult> ApplyActionAsync(string name, string action, CancellationToken cancellationToken = default)
    {
        if (!IsProjectName(name) || action is not ("start" or "stop" or "restart"))
            return new DockerStackOperationResult(false, "docker.validation_failed", []);

        var containers = await RunAsync(["ps", "--all", "--quiet", "--filter", $"label=com.docker.compose.project={name}"], cancellationToken);
        if (!containers.Success) return new DockerStackOperationResult(false, ToProblemCode(containers.Error), []);
        var ids = containers.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return new DockerStackOperationResult(false, "docker.stack_no_services", []);

        var arguments = new List<string> { action };
        arguments.AddRange(ids);
        var result = await RunAsync(arguments, cancellationToken);
        return new DockerStackOperationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result.Error), result.Success ? ToLines(result.Output) : []);
    }
    public async Task<IReadOnlyList<DockerStackDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["compose", "ls", "--all", "--format", "json"], cancellationToken);
        var stacks = new Dictionary<string, DockerStackDto>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (result.Success)
            {
                using var document = JsonDocument.Parse(result.Output);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        var name = Read(item, "Name");
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            var files = Read(item, "ConfigFiles");
                            stacks[name] = new DockerStackDto(name, Read(item, "Status"), files, ConfigDirectory(files));
                        }
                    }
                }
            }
        }
        catch (JsonException) { /* Container labels below still recover stopped projects. */ }

        // docker compose ls has varied between Compose releases. Labels are the Engine source
        // of truth, so merge them as a fallback rather than letting a stopped project vanish.
        var labels = await RunAsync(["ps", "--all", "--format", "{{.Label \\\"com.docker.compose.project\\\"}}\t{{.Label \\\"com.docker.compose.project.config_files\\\"}}"], cancellationToken);
        if (labels.Success)
        {
            foreach (var row in labels.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => line.Split('\t', 2)))
            {
                var name = Value(row, 0);
                if (!IsProjectName(name) || stacks.ContainsKey(name)) continue;
                var files = Value(row, 1);
                stacks[name] = new DockerStackDto(name, "stopped", files, ConfigDirectory(files));
            }
        }

        return stacks.Values.OrderBy(stack => stack.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DockerStackDefinitionDto?> GetDefinitionAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsProjectName(name)) return null;
        var stack = (await ListAsync(cancellationToken)).FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var composePath = FirstConfigFile(stack?.ConfigFiles);
        if (composePath is null || !File.Exists(composePath)) return null;
        try
        {
            var yaml = await File.ReadAllTextAsync(composePath, cancellationToken);
            return yaml.Length <= MaximumComposeBytes ? new DockerStackDefinitionDto(stack!.Name, yaml) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task<DockerStackOperationResult> ExecuteAsync(DockerStackDefinitionDto definition, IReadOnlyList<string> composeCommand, bool persistSource, CancellationToken cancellationToken)
    {
        if (!Validate(definition, out var problemCode)) return new DockerStackOperationResult(false, problemCode, []);
        var directory = persistSource
            ? Path.Combine(environment.ContentRootPath, "data", "docker-compose", definition.Name)
            : Path.Combine(environment.ContentRootPath, "data", "docker-compose", "validation", Guid.NewGuid().ToString("N"));
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
            if (!persistSource)
                try { Directory.Delete(directory, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static bool Validate(DockerStackDefinitionDto definition, out string problem)
    {
        if (!IsProjectName(definition.Name)) { problem = "docker.stack_invalid_name"; return false; }
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
    private static string Value(IReadOnlyList<string> row, int index) => index < row.Count ? row[index] : string.Empty;
    private static bool IsProjectName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 63 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string ConfigDirectory(string configFiles) => Path.GetDirectoryName(FirstConfigFile(configFiles) ?? string.Empty) ?? string.Empty;
    private static string? FirstConfigFile(string? configFiles) => string.IsNullOrWhiteSpace(configFiles)
        ? null
        : configFiles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    private static string Read(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    private static string ToProblemCode(string error) => error.Contains("not_found", StringComparison.OrdinalIgnoreCase) ? "docker.not_installed" : error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "docker.permission_denied" : "docker.compose_failed";
    private sealed record CommandResult(bool Success, string Output, string Error);
}
