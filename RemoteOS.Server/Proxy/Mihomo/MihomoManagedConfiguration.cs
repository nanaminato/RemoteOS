using System.Net;
using System.Text;

namespace Server.Proxy.Mihomo;

/// <summary>
/// Keeps the management endpoint and credential under Server ownership.  Profile YAML is
/// intentionally raw, but it must never be allowed to replace the loopback controller
/// settings that RemoteOS uses to manage a hosted Mihomo process.
/// </summary>
internal static class MihomoManagedConfiguration
{
    private static readonly HashSet<string> ControllerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "external-controller",
        "secret",
    };

    public static string WithServerControllerSettings(string yaml, MihomoControllerOptions options, string secret)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(options);
        if (secret.Length is < 16 or > 512 || secret.Any(char.IsControl))
            throw new ArgumentException("The controller secret is invalid.", nameof(secret));

        var profileConfiguration = RemoveTopLevelControllerSettings(yaml);
        var builder = new StringBuilder(profileConfiguration.TrimEnd('\r', '\n'));
        if (builder.Length > 0) builder.Append('\n');
        builder.Append("external-controller: ").Append(ControllerAddress(options.Endpoint)).Append('\n');
        builder.Append("secret: \"").Append(EscapeYamlDoubleQuotedScalar(secret)).Append("\"\n");
        return builder.ToString();
    }

    private static string RemoveTopLevelControllerSettings(string yaml)
    {
        using var reader = new StringReader(yaml);
        var retained = new StringBuilder(yaml.Length);
        var skipping = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryGetTopLevelKey(line, out var key))
                skipping = ControllerKeys.Contains(key);
            if (!skipping) retained.AppendLine(line);
        }
        return retained.ToString();
    }

    private static bool TryGetTopLevelKey(string line, out string key)
    {
        key = "";
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-') return false;
        var separator = line.IndexOf(':');
        if (separator <= 0) return false;
        var candidate = line[..separator].Trim();
        if (candidate.Length >= 2 && candidate[0] == candidate[^1] && candidate[0] is '\'' or '\"')
            candidate = candidate[1..^1];
        if (candidate.Length == 0 || candidate.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
            return false;
        key = candidate;
        return true;
    }

    private static string ControllerAddress(Uri endpoint)
    {
        var host = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : endpoint.Host;
        if (IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            host = "[" + host + "]";
        return host + ":" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string EscapeYamlDoubleQuotedScalar(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
