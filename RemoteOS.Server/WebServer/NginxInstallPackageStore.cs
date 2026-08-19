using System.IO.Compression;

namespace Server.WebServer;

/// <summary>Stores only validated, short-lived Windows Nginx ZIP archives outside user-controlled paths.</summary>
internal sealed class NginxInstallPackageStore(IHostEnvironment environment)
{
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private readonly string _root = Path.Combine(environment.ContentRootPath, "data", "webserver-packages");

    public async Task<string?> SaveAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(_root, $"{id}.uploading");
        var destination = Path.Combine(_root, $"{id}.zip");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumPackageBytes) return null;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (!ContainsNginxExecutable(temporary)) return null;
            File.Move(temporary, destination);
            return id;
        }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string? GetPath(string packageId)
    {
        if (!Guid.TryParseExact(packageId, "N", out _)) return null;
        var path = Path.Combine(_root, $"{packageId}.zip");
        return File.Exists(path) && !IsSymbolicLink(path) ? path : null;
    }

    public void Delete(string? packageId)
    {
        var path = packageId is null ? null : GetPath(packageId);
        if (path is not null) File.Delete(path);
    }

    public static bool ContainsNginxExecutable(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Any(entry => IsSafeEntry(entry.FullName)
            && entry.FullName.EndsWith("/nginx.exe", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSafeEntry(string entryName) => !Path.IsPathRooted(entryName)
        && !entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");

    private static bool IsSymbolicLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (IOException) { return true; }
    }
}
