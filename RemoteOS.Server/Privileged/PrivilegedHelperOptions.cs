namespace Server.Privileged;

/// <summary>Location of the root-owned local privileged helper installed with RemoteOS.</summary>
public sealed class PrivilegedHelperOptions
{
    public string HelperPath { get; init; } = string.Empty;
    public string SudoPath { get; init; } = "/usr/bin/sudo";
    public int TimeoutSeconds { get; init; } = 30;
}
