using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer;

/// <summary>
/// Process-wide clipboard for remote file-system entries. It intentionally remains separate from
/// the host OS clipboard, which is used only for uploading local files into the remote workspace.
/// </summary>
public interface IRemoteFileClipboard
{
    IReadOnlyList<FileSystemEntryDto> Entries { get; }
    RemoteFileClipboardOperation Operation { get; }
    bool HasEntries { get; }

    void Set(IReadOnlyList<FileSystemEntryDto> entries, RemoteFileClipboardOperation operation);
    void Clear();
}

public enum RemoteFileClipboardOperation { Copy, Cut }

public sealed class RemoteFileClipboard : IRemoteFileClipboard
{
    private IReadOnlyList<FileSystemEntryDto> _entries = Array.Empty<FileSystemEntryDto>();

    public IReadOnlyList<FileSystemEntryDto> Entries => _entries;
    public RemoteFileClipboardOperation Operation { get; private set; }
    public bool HasEntries => _entries.Count > 0;

    public void Set(IReadOnlyList<FileSystemEntryDto> entries, RemoteFileClipboardOperation operation)
    {
        _entries = entries.ToArray();
        Operation = operation;
    }

    public void Clear() => _entries = Array.Empty<FileSystemEntryDto>();
}
