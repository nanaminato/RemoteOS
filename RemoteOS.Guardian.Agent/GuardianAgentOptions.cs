namespace RemoteOS.Guardian.Agent;

internal sealed record GuardianAgentOptions(string PipeName, string SharedSecret, string DataDirectory, IReadOnlyList<string> AllowedRoots)
{
    public static GuardianAgentOptions Load()
    {
        var dataDirectory = Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var roots = (Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_ALLOWED_ROOTS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new GuardianAgentOptions(
            Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_PIPE") ?? "remoteos-guardian",
            Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_SHARED_SECRET") ?? string.Empty,
            dataDirectory, roots);
    }
}
