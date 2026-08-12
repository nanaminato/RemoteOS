namespace Server.Firewall;

/// <summary>
/// Paths for the root-owned firewall helper installed by the Linux deployment script.
/// The Server itself remains an unprivileged process and invokes this helper through
/// a narrowly scoped sudoers rule.
/// </summary>
public sealed class FirewallPrivilegedHelperOptions
{
    public string HelperPath { get; init; } = "/usr/local/lib/remoteos/remoteos-firewall-helper";
    public string SudoPath { get; init; } = "/usr/bin/sudo";
}
