using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Server.WebServer;

/// <summary>Checks the identity of the server process; HTTP callers can never supply elevation credentials.</summary>
public interface IHostPrivilegeService
{
    bool IsAdministrator { get; }
}

public sealed class HostPrivilegeService : IHostPrivilegeService
{
    public bool IsAdministrator => OperatingSystem.IsWindows() ? IsWindowsAdministrator() : IsUnixAdministrator();

    private static bool IsUnixAdministrator() => geteuid() == 0;

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("libc")]
    private static extern uint geteuid();
}
