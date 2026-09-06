namespace RemoteOS.Core.VirtualSystemDrive;

public sealed record ShellDescriptor(int SchemaVersion, string Id, string DisplayName,
    string? PreviewPath = null, IReadOnlyList<string>? Features = null);

public enum RemoteOsShortcutKind
{
    Application,
    RemoteFile,
    RemoteFolder,
    Script,
    Uri,
}

/// <summary>Persisted desktop link. Target interpretation belongs exclusively to the Host router.</summary>
public sealed record RemoteOsShortcut(int SchemaVersion, string Id, string DisplayName,
    RemoteOsShortcutKind Kind, string Target, ApplicationDescriptorIcon? Icon = null);
