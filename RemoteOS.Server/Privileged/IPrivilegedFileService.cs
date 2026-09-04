using RemoteOS.Protocol.Files;

namespace Server.Privileged;

public interface IPrivilegedFileService
{
    Task<(Stream Stream, string FileName)> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    Task<FileEntryDto> WriteAsync(string path, Stream content, CancellationToken cancellationToken = default);
}
