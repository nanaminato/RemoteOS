using RemoteOS.Protocol.Files;
using RemoteOS.Protocol.Privileged;

namespace Server.Privileged;

public sealed class PrivilegedFileService(IPrivilegedOperationRunner runner) : IPrivilegedFileService
{
    public async Task<(Stream Stream, string FileName)> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest("read-file", Path: path), cancellationToken);
        if (!result.Success) throw ToException(result, path);
        var bytes = Convert.FromBase64String(result.OutputBase64 ?? string.Empty);
        return (new MemoryStream(bytes, writable: false), Path.GetFileName(path));
    }

    public async Task<FileEntryDto> WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        using var bytes = new MemoryStream();
        await content.CopyToAsync(bytes, cancellationToken);
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest("write-file", Path: path,
            ContentBase64: Convert.ToBase64String(bytes.ToArray())), cancellationToken);
        if (!result.Success) throw ToException(result, path);
        var file = new FileInfo(path);
        return new FileEntryDto(path, file.Name, file.Extension, file.Length, file.CreationTimeUtc,
            file.LastWriteTimeUtc, file.LastAccessTimeUtc, file.Attributes.HasFlag(FileAttributes.Hidden),
            file.Attributes.HasFlag(FileAttributes.System), "application/octet-stream");
    }

    private static Exception ToException(PrivilegedOperationResult result, string path) => result.ExitCode switch
    {
        2 => new FileNotFoundException(result.Error ?? "File not found", path),
        69 => new InvalidOperationException(result.Error ?? "Privileged helper unavailable"),
        77 => new UnauthorizedAccessException(result.Error),
        _ => new IOException(result.Error ?? "Privileged file operation failed"),
    };
}
