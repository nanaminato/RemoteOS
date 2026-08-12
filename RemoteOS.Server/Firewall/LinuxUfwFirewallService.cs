using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using RemoteOS.Protocol.Firewall;

namespace Server.Firewall;

/// <summary>Linux-only, injection-safe facade over the root-owned UFW helper.</summary>
public sealed class LinuxUfwFirewallService : IHostFirewallService
{
    private static readonly Regex NumberedRule = new(
        @"^\[\s*(?<number>\d+)\]\s+(?<target>.+?)\s+(?<action>ALLOW|DENY|REJECT|LIMIT)\s+(?<direction>IN|OUT)\s+(?<source>.+?)(?:\s+#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly ILogger<LinuxUfwFirewallService> _logger;
    private readonly FirewallPrivilegedHelperOptions _helper;

    public LinuxUfwFirewallService(IConfiguration configuration, ILogger<LinuxUfwFirewallService> logger)
    {
        _logger = logger;
        _helper = configuration.GetSection("Firewall").Get<FirewallPrivilegedHelperOptions>() ?? new FirewallPrivilegedHelperOptions();
    }

    public async Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!IsHelperInstalled()) return new(false, false, "ufw", null, null, null, "firewall.privileged_proxy_required");
        var version = await RunAsync(["version"], cancellationToken);
        var status = await RunAsync(["status-verbose"], cancellationToken);
        if (!status.Success) return new(false, false, "ufw", ParseVersion(version.Output), null, null, status.ProblemCode);

        var enabled = status.Output.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
        var defaults = Regex.Match(status.Output, @"Default:\s+(?<incoming>\w+)\s+\(incoming\),\s+(?<outgoing>\w+)\s+\(outgoing\)", RegexOptions.IgnoreCase);
        return new(true, enabled, "ufw", ParseVersion(version.Output),
            defaults.Success ? defaults.Groups["incoming"].Value.ToLowerInvariant() : null,
            defaults.Success ? defaults.Groups["outgoing"].Value.ToLowerInvariant() : null);
    }

    public async Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["status-numbered"], cancellationToken);
        if (!result.Success) return [];
        var rules = new List<FirewallRuleDto>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = NumberedRule.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number)) continue;
            var (destination, port, protocol) = ParseTarget(match.Groups["target"].Value);
            rules.Add(new FirewallRuleDto(number, match.Groups["action"].Value.ToLowerInvariant(),
                match.Groups["direction"].Value.ToLowerInvariant(), protocol,
                match.Groups["source"].Value.Trim(), destination, port));
        }
        return rules;
    }

    public Task<FirewallOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        RunOperationAsync([enabled ? "enable" : "disable"], cancellationToken);

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
        if (!TryValidateRule(request.Action, request.Direction, request.Protocol, request.Source, request.Destination, request.Port, out var args, out var problem)) return Task.FromResult(new FirewallOperationResult(false, problem));
        return RunOperationAsync(["rule", .. args], cancellationToken);
    }

    public Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        if (number is <= 0 or > 10_000) return Task.FromResult(new FirewallOperationResult(false, "firewall.invalid_rule_number"));
        if (!TryValidateRule(request.Action, request.Direction, request.Protocol, request.Source, request.Destination, request.Port, out var args, out var problem)) return Task.FromResult(new FirewallOperationResult(false, problem));
        return RunOperationAsync(["replace", number.ToString(System.Globalization.CultureInfo.InvariantCulture), .. args], cancellationToken);
    }

    public Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken cancellationToken) =>
        number is <= 0 or > 10_000
            ? Task.FromResult(new FirewallOperationResult(false, "firewall.invalid_rule_number"))
            : RunOperationAsync(["delete", number.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);

    private bool IsHelperInstalled() => File.Exists(_helper.HelperPath);

    private async Task<FirewallOperationResult> RunOperationAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!IsHelperInstalled()) return new(false, "firewall.privileged_proxy_required");
        var result = await RunAsync(arguments, cancellationToken);
        return result.Success ? new(true) : new(false, result.ProblemCode);
    }

    private async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(_helper.SudoPath)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add(_helper.HelperPath);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return new(false, "", "firewall.command_unavailable");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var text = await output;
            var stderr = await error;
            if (process.ExitCode == 0) return new(true, text, string.Empty);
            _logger.LogWarning("Firewall helper failed with exit code {ExitCode}; stderr omitted from API.", process.ExitCode);
            return new(false, text, ProblemCodeForFailure(stderr));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Firewall helper operation could not be started.");
            return new(false, "", "firewall.command_unavailable");
        }
    }

    private static string ProblemCodeForFailure(string stderr)
    {
        if (stderr.Contains("ufw is not installed", StringComparison.OrdinalIgnoreCase)) return "firewall.ufw_not_installed";
        return IsPrivilegeFailure(stderr) ? "firewall.privileged_proxy_required" : "firewall.operation_failed";
    }

    private static bool IsPrivilegeFailure(string stderr) =>
        stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("sudo:", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("not allowed to run sudo", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("root is required", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("only be run as root", StringComparison.OrdinalIgnoreCase);

    private static string? ParseVersion(string text) => Regex.Match(text, @"ufw\s+(?<version>[\d.]+)", RegexOptions.IgnoreCase) is { Success: true } match
        ? match.Groups["version"].Value : null;

    private static (string Destination, string Port, string Protocol) ParseTarget(string target)
    {
        // `ufw status numbered` has a column-like target field. It is usually
        // `22/tcp`, but can also be `192.0.2.10 22/tcp` or `22/tcp (v6)`.
        // Keeping the destination separate makes the table accurately reflect
        // rules that constrain the local address as well as the port.
        var normalized = target.Trim();
        if (normalized.EndsWith(" (v6)", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^5].TrimEnd();
        if (normalized.Equals("anywhere", StringComparison.OrdinalIgnoreCase)) return ("any", "any", "any");
        if (TryNormalizeEndpoint(normalized, out var endpoint)) return (endpoint, "any", "any");

        var parts = normalized.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var portAndProtocol = parts[^1];
        var slash = portAndProtocol.IndexOf('/');
        var port = slash > 0 ? portAndProtocol[..slash] : portAndProtocol;
        var protocol = slash > 0 ? portAndProtocol[(slash + 1)..].ToLowerInvariant() : "any";
        var destination = parts.Length > 1 ? string.Join(' ', parts[..^1]) : "any";
        return (destination.Equals("anywhere", StringComparison.OrdinalIgnoreCase) ? "any" : destination, port, protocol);
    }

    private static bool IsPolicy(string? value) => value?.Trim().ToLowerInvariant() is "allow" or "deny" or "reject";
    private static bool IsAction(string? value) => value?.Trim().ToLowerInvariant() is "allow" or "deny" or "reject" or "limit";
    private static bool IsDirection(string? value) => value?.Trim().ToLowerInvariant() is "in" or "out";
    private static bool IsProtocol(string? value) => value?.Trim().ToLowerInvariant() is "tcp" or "udp" or "any";

    private static bool TryValidateRule(string? action, string? direction, string? protocol, string? sourceValue, string? destinationValue, string? portValue,
        out IReadOnlyList<string> args, out string problem)
    {
        args = []; problem = "firewall.invalid_rule";
        if (!IsAction(action) || !IsDirection(direction) || !IsProtocol(protocol)) return false;
        if (!TryNormalizeEndpoint(sourceValue, out var source) || !TryNormalizeEndpoint(destinationValue, out var destination)) return false;
        if (!TryNormalizePort(portValue, out var port)) return false;
        args = [action!.Trim().ToLowerInvariant(), direction!.Trim().ToLowerInvariant(),
            protocol!.Trim().ToLowerInvariant(), source, destination, port];
        problem = string.Empty;
        return true;
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
