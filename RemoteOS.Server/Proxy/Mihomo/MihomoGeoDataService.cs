using RemoteOS.Protocol.Proxy;

namespace Server.Proxy.Mihomo;

/// <summary>
/// Stages a user-selected Server-local GeoIP database under a fixed private name. Mihomo only
/// receives this managed directory through <c>-d</c>; it never receives an arbitrary UI path.
/// </summary>
public sealed class MihomoGeoDataService(IProxyPlatformPaths paths, IProxyDiagnosticLogStore? diagnostics = null) : IProxyGeoDataService
{
    private const long MaximumBytes = 128L * 1024 * 1024;
    private const string FileName = "geoip.metadb";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<ProxyGeoDataDto> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var info = new FileInfo(ManagedFilePath());
            return Task.FromResult(info.Exists && info.Length is > 0 and <= MaximumBytes
                ? new ProxyGeoDataDto(true, info.Length)
                : new ProxyGeoDataDto(false));
        }
        catch (IOException) { return Task.FromResult(new ProxyGeoDataDto(false)); }
        catch (UnauthorizedAccessException) { return Task.FromResult(new ProxyGeoDataDto(false)); }
    }

    public async Task<string?> ConfigureFromServerFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!TrySafeSourcePath(filePath, out var source)) return ProxyProblemCodes.GeodataInvalid;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = paths.GetEngineDataDirectory(MihomoEngine.Id);
            Directory.CreateDirectory(directory);
            MakePrivateDirectory(directory);
            var temporary = Path.Combine(directory, ".geoip-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                if (input.Length is <= 0 or > MaximumBytes) return ProxyProblemCodes.GeodataInvalid;
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await CopyLimitedAsync(input, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                MakePrivateFile(temporary);
                File.Move(temporary, ManagedFilePath(), overwrite: true);
                MakePrivateFile(ManagedFilePath());
                await WriteDiagnosticAsync("info", "A Server-local GeoIP database was staged for managed Mihomo.", cancellationToken);
                return null;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (IOException)
        {
            await WriteDiagnosticAsync("warning", "The selected Server-local GeoIP database could not be staged.", cancellationToken);
            return ProxyProblemCodes.GeodataInvalid;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteDiagnosticAsync("warning", "The RemoteOS Server service account cannot stage the selected GeoIP database.", cancellationToken);
            return ProxyProblemCodes.PrivilegedOperationUnavailable;
        }
        finally { _gate.Release(); }
    }

    private string ManagedFilePath() => Path.Combine(paths.GetEngineDataDirectory(MihomoEngine.Id), FileName);
    private static bool TrySafeSourcePath(string value, out string path)
    {
        path = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.GetFullPath(value);
            if (!path.EndsWith(".metadb", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static async Task CopyLimitedAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            total += read;
            if (total > MaximumBytes) throw new IOException("The GeoIP database exceeds the maximum allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
    private async Task WriteDiagnosticAsync(string level, string message, CancellationToken cancellationToken)
    {
        if (diagnostics is not null) await diagnostics.WriteAsync(level, message, cancellationToken);
    }
    private static void MakePrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void MakePrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
}
