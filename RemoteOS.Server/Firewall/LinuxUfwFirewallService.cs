using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using RemoteOS.Protocol.Firewall;

namespace Server.Firewall;

/// <summary>Linux-only, injection-safe facade over the locally installed UFW executable.</summary>
public sealed class LinuxUfwFirewallService : IHostFirewallService
{
    private static readonly Regex NumberedRule = new(
        @"^\[\s*(?<number>\d+)\]\s+(?<port>\S+)\s+(?<action>ALLOW|DENY|REJECT|LIMIT)\s+(?<direction>IN|OUT)\s+(?<source>.+?)(?:\s+#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly ILogger<LinuxUfwFirewallService> _logger;
    private readonly string _ufwPath;

    public LinuxUfwFirewallService(IConfiguration configuration, ILogger<LinuxUfwFirewallService> logger)
    {
        _logger = logger;
        _ufwPath = configuration["Firewall:UfwPath"] ?? "/usr/sbin/ufw";
    }

    public async Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!IsUfwInstalled()) return new(false, false, "ufw", null, null, null, "firewall.ufw_not_installed");
        var version = await RunAsync(["--version"], cancellationToken);
        var status = await RunAsync(["status", "verbose"], cancellationToken);
        if (!status.Success) return new(false, false, "ufw", ParseVersion(version.Output), null, null, status.ProblemCode);

        var enabled = status.Output.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
        var defaults = Regex.Match(status.Output, @"Default:\s+(?<incoming>\w+)\s+\(incoming\),\s+(?<outgoing>\w+)\s+\(outgoing\)", RegexOptions.IgnoreCase);
        return new(true, enabled, "ufw", ParseVersion(version.Output),
            defaults.Success ? defaults.Groups["incoming"].Value.ToLowerInvariant() : null,
            defaults.Success ? defaults.Groups["outgoing"].Value.ToLowerInvariant() : null);
    }

    public async Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["status", "numbered"], cancellationToken);
        if (!result.Success) return [];
        var rules = new List<FirewallRuleDto>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = NumberedRule.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number)) continue;
            var portAndProtocol = match.Groups["port"].Value;
            var slash = portAndProtocol.IndexOf('/');
            var port = slash > 0 ? portAndProtocol[..slash] : portAndProtocol;
            var protocol = slash > 0 ? portAndProtocol[(slash + 1)..].ToLowerInvariant() : "any";
            rules.Add(new FirewallRuleDto(number, match.Groups["action"].Value.ToLowerInvariant(),
                match.Groups["direction"].Value.ToLowerInvariant(), protocol,
                match.Groups["source"].Value.Trim(), "any", port));
        }
        return rules;
    }

    public Task<FirewallOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        RunOperationAsync([enabled ? "--force" : "--force", enabled ? "enable" : "disable"], cancellationToken);

    public async Task<FirewallOperationResult> SetDefaultsAsync(string incomingPolicy, string outgoingPolicy, CancellationToken cancellationToken)
    {
        if (!IsPolicy(incomingPolicy) || !IsPolicy(outgoingPolicy)) return new(false, "firewall.invalid_default_policy");
        var incoming = await RunOperationAsync(["default", incomingPolicy.Trim().ToLowerInvariant(), "incoming"], cancellationToken);
        return incoming.Success
            ? await RunOperationAsync(["default", outgoingPolicy.Trim().ToLowerInvariant(), "outgoing"], cancellationToken)
            : incoming;
    }

    public Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        if (!TryValidateRule(request, out var args, out var problem)) return Task.FromResult(new FirewallOperationResult(false, problem));
        return RunOperationAsync(args, cancellationToken);
    }

    public Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken cancellationToken) =>
        number is <= 0 or > 10_000
            ? Task.FromResult(new FirewallOperationResult(false, "firewall.invalid_rule_number"))
            : RunOperationAsync(["--force", "delete", number.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);

    private bool IsUfwInstalled() => File.Exists(_ufwPath) || _ufwPath == "ufw";

    private async Task<FirewallOperationResult> RunOperationAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!IsUfwInstalled()) return new(false, "firewall.ufw_not_installed");
        var result = await RunAsync(arguments, cancellationToken);
        return result.Success ? new(true) : new(false, result.ProblemCode);
    }

    private async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(_ufwPath)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return new(false, "", "firewall.command_unavailable");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var text = await output;
            var stderr = await error;
            if (process.ExitCode == 0) return new(true, text, string.Empty);
            _logger.LogWarning("UFW operation failed with exit code {ExitCode}; stderr omitted from API.", process.ExitCode);
            return new(false, text, stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                ? "firewall.privileged_proxy_required" : "firewall.operation_failed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "UFW operation could not be started.");
            return new(false, "", "firewall.command_unavailable");
        }
    }

    private static string? ParseVersion(string text) => Regex.Match(text, @"ufw\s+(?<version>[\d.]+)", RegexOptions.IgnoreCase) is { Success: true } match
        ? match.Groups["version"].Value : null;

    private static bool IsPolicy(string? value) => value?.Trim().ToLowerInvariant() is "allow" or "deny" or "reject";
    private static bool IsAction(string? value) => value?.Trim().ToLowerInvariant() is "allow" or "deny" or "reject" or "limit";
    private static bool IsDirection(string? value) => value?.Trim().ToLowerInvariant() is "in" or "out";
    private static bool IsProtocol(string? value) => value?.Trim().ToLowerInvariant() is "tcp" or "udp" or "any";

    private static bool TryValidateRule(CreateFirewallRuleRequest request, out IReadOnlyList<string> args, out string problem)
    {
        args = []; problem = "firewall.invalid_rule";
        if (!IsAction(request.Action) || !IsDirection(request.Direction) || !IsProtocol(request.Protocol)) return false;
        if (!TryNormalizeEndpoint(request.Source, out var source) || !TryNormalizeEndpoint(request.Destination, out var destination)) return false;
        if (!TryNormalizePort(request.Port, out var port)) return false;
        var values = new List<string> { request.Action.Trim().ToLowerInvariant(), request.Direction.Trim().ToLowerInvariant() };
        if (!string.Equals(request.Protocol, "any", StringComparison.OrdinalIgnoreCase)) { values.Add("proto"); values.Add(request.Protocol.Trim().ToLowerInvariant()); }
        if (source != "any") { values.Add("from"); values.Add(source); }
        if (destination != "any") { values.Add("to"); values.Add(destination); }
        if (port != "any") { values.Add("port"); values.Add(port); }
        args = values; problem = string.Empty; return true;
    }

    private static bool TryNormalizeEndpoint(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (normalized is "any" or "anywhere") { normalized = "any"; return true; }
        var slash = normalized.IndexOf('/');
        var address = slash < 0 ? normalized : normalized[..slash];
        if (!IPAddress.TryParse(address, out _)) return false;
        if (slash < 0) return true;
        return int.TryParse(normalized[(slash + 1)..], out var prefix) && prefix >= 0 && prefix <= (address.Contains(':') ? 128 : 32);
    }

    private static bool TryNormalizePort(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (normalized is "any" or "") { normalized = "any"; return true; }
        var parts = normalized.Split(':');
        return parts.Length is 1 or 2 && parts.All(part => int.TryParse(part, out var port) && port is > 0 and <= 65535)
            && (parts.Length == 1 || int.Parse(parts[0]) <= int.Parse(parts[1]));
    }

    private sealed record CommandResult(bool Success, string Output, string ProblemCode);
}
