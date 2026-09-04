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

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        => EnsureSuccess(await runner.ExecuteAsync(new PrivilegedOperationRequest("delete", Path: path), cancellationToken), path);

    public async Task<FileSystemEntryDto> RenameAsync(string sourcePath, string newName, CancellationToken cancellationToken = default)
    {
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest("rename", Path: sourcePath, NewName: newName), cancellationToken);
        EnsureSuccess(result, sourcePath);
        var parent = Path.GetDirectoryName(sourcePath);
        return ToSystemEntry(Path.Combine(parent ?? string.Empty, newName));
    }

    public async Task<FileSystemEntryDto> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest("move", Path: sourcePath, DestinationPath: destinationPath, Overwrite: overwrite), cancellationToken);
        EnsureSuccess(result, sourcePath);
        return ToSystemEntry(destinationPath);
    }

    public async Task<FileSystemEntryDto> CopyAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest("copy", Path: sourcePath, DestinationPath: destinationPath, Overwrite: overwrite), cancellationToken);
        EnsureSuccess(result, sourcePath);
        return ToSystemEntry(destinationPath);
    }

    public async Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var bytes = new MemoryStream();
        await content.CopyToAsync(bytes, cancellationToken);
        var path = Path.Combine(targetDirectoryPath, fileName);
        var result = await runner.ExecuteAsync(new PrivilegedOperationRequest("upload", Path: targetDirectoryPath,
            FileName: fileName, ContentBase64: Convert.ToBase64String(bytes.ToArray())), cancellationToken);
        EnsureSuccess(result, path);
        return ToFileEntry(path);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => EnsureSuccess(await runner.ExecuteAsync(new PrivilegedOperationRequest("create-directory", Path: path), cancellationToken), path);

    private static void EnsureSuccess(RemoteOS.Protocol.Privileged.PrivilegedOperationResult result, string path)
    {
        if (!result.Success) throw ToException(result, path);
    }

    private static FileSystemEntryDto ToSystemEntry(string path)
    {
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return new FileSystemEntryDto(path, directory.Name, null, FileSystemEntryType.Directory,
                directory.CreationTimeUtc, directory.LastWriteTimeUtc, directory.LastAccessTimeUtc,
                directory.Attributes.HasFlag(FileAttributes.Hidden), directory.Attributes.HasFlag(FileAttributes.System), "inode/directory");
        }
        var file = ToFileEntry(path);
        return new FileSystemEntryDto(file.Path, file.Name, file.Size, FileSystemEntryType.File,
            file.Created, file.Modified, file.Accessed, file.IsHidden, file.IsSystem, file.MimeType);
    }

    private static FileEntryDto ToFileEntry(string path)
    {
        var file = new FileInfo(path);
        var extension = string.IsNullOrEmpty(file.Extension) ? null : file.Extension[1..].ToLowerInvariant();
        return new FileEntryDto(path, file.Name, extension, file.Length, file.CreationTimeUtc,
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
