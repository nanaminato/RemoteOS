using System.Security.Claims;
using RemoteOS.Protocol.Files;
using RemoteOS.Protocol.Privileged;

namespace Server.Privileged;

/// <summary>Explorer compatibility facade over the capability-scoped host elevation store.</summary>
public sealed class FileElevationSessionStore(IHostElevationSessionStore elevations) : IFileElevationSessionStore
{
    // Retained only for older callers. Endpoint code must use an explicit operation capability.
    private const HostElevationCapability LegacyCapability = HostElevationCapability.FileWrite;

    public FileElevationSessionStore() : this(new HostElevationSessionStore()) { }

    public bool IsElevated(ClaimsPrincipal principal, string path) => elevations.IsGranted(principal, LegacyCapability, path);
    public bool IsElevated(ClaimsPrincipal principal, params string[] paths) => paths.All(path => IsElevated(principal, path));
    public DateTimeOffset Grant(ClaimsPrincipal principal, string path, bool includeDescendants = false)
        => elevations.Grant(principal, LegacyCapability, path, includeDescendants, "legacy-file-elevation");

    public bool IsElevated(ClaimsPrincipal principal, FileElevationCapability capability, params string[] paths)
        => paths.All(path => elevations.IsGranted(principal, ToHostCapability(capability), path));

    public DateTimeOffset Grant(ClaimsPrincipal principal, FileElevationCapability capability, string path, bool includeDescendants = false,
        string authenticationMethod = "host-password", string? correlationId = null)
        => elevations.Grant(principal, ToHostCapability(capability), path, includeDescendants, authenticationMethod, correlationId);

    private static HostElevationCapability ToHostCapability(FileElevationCapability capability) => capability switch
    {
        FileElevationCapability.Read => HostElevationCapability.FileRead,
        FileElevationCapability.Write => HostElevationCapability.FileWrite,
        FileElevationCapability.CreateDirectory => HostElevationCapability.FileCreateDirectory,
        FileElevationCapability.Delete => HostElevationCapability.FileDelete,
        FileElevationCapability.Rename => HostElevationCapability.FileRename,
        FileElevationCapability.Move => HostElevationCapability.FileMove,
        FileElevationCapability.Copy => HostElevationCapability.FileCopy,
        FileElevationCapability.Upload => HostElevationCapability.FileUpload,
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };
}
