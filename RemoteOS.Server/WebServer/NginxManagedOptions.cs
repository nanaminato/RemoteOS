namespace Server.WebServer;

/// <summary>
/// Host-admin supplied installation contract for the RemoteOS-owned Nginx instance.
/// The API never accepts an executable path, URL, package or command line from a client.
/// Both platform installers must place Nginx in <see cref="InstallationRoot"/> and leave the
/// standard Nginx layout intact before returning successfully.
/// </summary>
public sealed class NginxManagedOptions
{
    /// <summary>Absolute RemoteOS-owned root. Empty selects the platform default.</summary>
    public string InstallationRoot { get; init; } = string.Empty;

    /// <summary>Absolute, administrator-configured installer executable or script.</summary>
    public string InstallerCommand { get; init; } = string.Empty;

    /// <summary>Fixed arguments supplied by host configuration, never by HTTP callers.</summary>
    public IReadOnlyList<string> InstallerArguments { get; init; } = [];
}
