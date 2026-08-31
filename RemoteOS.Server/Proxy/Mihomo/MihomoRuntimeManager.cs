using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteOS.Protocol.Proxy;
using Server.Proxy.Platform;

namespace Server.Proxy.Mihomo;

/// <summary>
/// Owns only RemoteOS-managed Mihomo artifacts. Release directories are immutable; the active
/// pointer changes only after the constrained service boundary and loopback controller both pass.
/// </summary>
public sealed class MihomoRuntimeManager(
    IProxyPlatformPaths paths,
    IHttpClientFactory httpClients,
    IProxyPrivilegedOperations privileged,
    IMihomoRuntimeProbe probe,
    IMihomoControllerClient controller,
    IProxyControllerSecretStore controllerSecrets,
    MihomoControllerOptions controllerOptions,
    MihomoRuntimeManifest manifest) : IProxyRuntimeManager
{
    private const string ServiceName = "remoteos-mihomo";
    private const string ServiceConfigurationId = "mihomo-default";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProxyRuntimeDto> GetAsync(string engineId, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id) return Unsupported(engineId);
        var state = await ReadStateAsync(cancellationToken);
        if (state?.ActiveVersion is not { Length: > 0 } active)
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false, ProxyProblemCodes.RuntimeNotInstalled);
        var executable = ExecutablePath(active);
        return File.Exists(executable)
            ? new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Stopped, active, state.PreviousVersion, true, false)
            : new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, active, state.PreviousVersion, false, false, ProxyProblemCodes.RuntimeNotInstalled);
    }

    public async Task<ProxyRuntimeDto> DetectExternalAsync(string engineId, string executablePath, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id) return Unsupported(engineId);
        if (!TrySafeExternalPath(executablePath, out var path) || !File.Exists(path) || Directory.Exists(path)
            || !HasExpectedArchitecture(path) || (!OperatingSystem.IsWindows() && !IsExecutable(path)))
            return new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Failed, null, null, false, true, ProxyProblemCodes.ExternalRuntimeInvalid);
        var version = await probe.GetVersionAsync(path, cancellationToken);
        return version is null
            ? new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Failed, null, null, false, true, ProxyProblemCodes.ExternalRuntimeInvalid)
            : new(MihomoEngine.Id, ProxyRuntimeMode.External, ProxyRuntimeState.Stopped, version, null, false, true);
    }

    public Task<ProxyRuntimeDto> InstallManagedAsync(string engineId, string? version, CancellationToken cancellationToken) =>
        InstallManagedAsync(engineId, version, null, cancellationToken);

    public Task<ProxyRuntimeDto> InstallManagedAsync(string engineId, string? version, Func<string, Task>? stageReporter, CancellationToken cancellationToken) =>
        engineId != MihomoEngine.Id
            ? Task.FromResult(Unsupported(engineId))
            : InstallManagedCoreAsync(version, async (release, destination, token) =>
            {
                await ReportStageAsync(stageReporter, "downloading");
                await DownloadAndVerifyAsync(release, destination, token);
            }, stageReporter, cancellationToken);

    public Task<ProxyRuntimeDto> InstallManagedFromArchiveAsync(string engineId, string? version, string archivePath, CancellationToken cancellationToken) =>
        InstallManagedFromArchiveAsync(engineId, version, archivePath, null, cancellationToken);

    public Task<ProxyRuntimeDto> InstallManagedFromArchiveAsync(string engineId, string? version, string archivePath, Func<string, Task>? stageReporter, CancellationToken cancellationToken) =>
        engineId != MihomoEngine.Id
            ? Task.FromResult(Unsupported(engineId))
            : InstallManagedCoreAsync(version, async (release, destination, token) =>
            {
                await ReportStageAsync(stageReporter, "copying");
                await CopyAndVerifyArchiveAsync(release, archivePath, destination, token);
            }, stageReporter, cancellationToken);

    private async Task<ProxyRuntimeDto> InstallManagedCoreAsync(string? version, Func<MihomoRuntimeRelease, string, CancellationToken, Task> stageArchiveAsync, Func<string, Task>? stageReporter, CancellationToken cancellationToken)
    {
        var release = manifest.Find(version);
        if (release is null)
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Failed, null, null, false, false,
                MihomoRuntimeManifest.CurrentRid() == "unsupported" ? ProxyProblemCodes.RuntimeUnsupportedPlatform : ProxyProblemCodes.RuntimeVersionUnsupported);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ReportStageAsync(stageReporter, "preparing");
            var before = await ReadStateAsync(cancellationToken);
            var finalDirectory = VersionDirectory(release.Version);
            try
            {
                if (!Directory.Exists(finalDirectory))
                    await StageAndInstallReleaseAsync(release, finalDirectory, stageArchiveAsync, stageReporter, cancellationToken);

                await ReportStageAsync(stageReporter, "checking");
                if (await probe.GetVersionAsync(ExecutablePath(release.Version), cancellationToken) is null)
                    return Failure(before, ProxyProblemCodes.RuntimeHealthCheckFailed);

                await ReportStageAsync(stageReporter, "activating");
                var runtimeOperation = before?.ActiveVersion is { Length: > 0 }
                    ? await privileged.ReplaceRuntimeAsync(new ReplaceProxyRuntimeOperation(MihomoEngine.Id, release.Version, ReleaseDirectoryId(release.Version)), cancellationToken)
                    : await privileged.InstallRuntimeAsync(new InstallProxyRuntimeOperation(MihomoEngine.Id, release.Version, ReleaseDirectoryId(release.Version)), cancellationToken);
                if (!runtimeOperation.Succeeded) return Failure(before, runtimeOperation.ProblemCode);

                // First boot has no Profile yet. This fixed bootstrap file intentionally keeps TUN
                // off and only exposes the controller on loopback; Goal 4 replaces it transactionally.
                var bootstrap = await EnsureBootstrapConfigurationAsync(cancellationToken);
                if (!bootstrap.Succeeded) return await RestorePreviousAsync(before, release.Version, bootstrap.ProblemCode, cancellationToken);

                if (before?.ActiveVersion is null)
                {
                    await ReportStageAsync(stageReporter, "installing_service");
                    var installService = await privileged.InstallServiceAsync(new InstallProxyServiceOperation(MihomoEngine.Id, ServiceName, ServiceConfigurationId), cancellationToken);
                    if (!installService.Succeeded) return await RestorePreviousAsync(before, release.Version, installService.ProblemCode, cancellationToken);
                }

                await ReportStageAsync(stageReporter, "starting");
                var start = await privileged.RestartServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
                if (!start.Succeeded) return await RestorePreviousAsync(before, release.Version, start.ProblemCode, cancellationToken);
                if (!await controller.IsReachableAsync(cancellationToken))
                    return await RestorePreviousAsync(before, release.Version, ProxyProblemCodes.RuntimeHealthCheckFailed, cancellationToken);

                await WriteStateAsync(new RuntimeState(release.Version, before?.ActiveVersion, DateTimeOffset.UtcNow), cancellationToken);
                await ReportStageAsync(stageReporter, "completed");
                return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Running, release.Version, before?.ActiveVersion, true, false);
            }
            catch (RuntimeInstallException exception) { return Failure(before, exception.ProblemCode); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure(before, ProxyProblemCodes.RuntimeHealthCheckFailed); }
            catch (IOException) { return Failure(before, ProxyProblemCodes.RuntimeIntegrityFailed); }
            catch (UnauthorizedAccessException) { return Failure(before, ProxyProblemCodes.PrivilegedOperationUnavailable); }
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyRuntimeDto> RollbackManagedAsync(string engineId, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id) return Unsupported(engineId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var before = await ReadStateAsync(cancellationToken);
            if (before?.PreviousVersion is not { Length: > 0 } previous || await probe.GetVersionAsync(ExecutablePath(previous), cancellationToken) is null)
                return Failure(before, ProxyProblemCodes.RuntimeHealthCheckFailed);
            var result = await privileged.ReplaceRuntimeAsync(new ReplaceProxyRuntimeOperation(MihomoEngine.Id, previous, ReleaseDirectoryId(previous)), cancellationToken);
            if (!result.Succeeded) return Failure(before, result.ProblemCode);
            result = await privileged.RestartServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            if (!result.Succeeded || !await controller.IsReachableAsync(cancellationToken)) return Failure(before, result.Succeeded ? ProxyProblemCodes.RuntimeHealthCheckFailed : result.ProblemCode);
            await WriteStateAsync(new RuntimeState(previous, before.ActiveVersion, DateTimeOffset.UtcNow), cancellationToken);
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Running, previous, before.ActiveVersion, true, false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyRuntimeDto> UninstallManagedAsync(string engineId, CancellationToken cancellationToken)
    {
        if (engineId != MihomoEngine.Id) return Unsupported(engineId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var before = await ReadStateAsync(cancellationToken);
            if (before is null) return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false, ProxyProblemCodes.RuntimeNotInstalled);
            var stop = await privileged.StopServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            if (!stop.Succeeded) return Failure(before, stop.ProblemCode);
            var service = await privileged.RemoveServiceAsync(new RemoveProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            if (!service.Succeeded) return Failure(before, service.ProblemCode);
            var runtime = await privileged.RemoveRuntimeAsync(new RemoveProxyRuntimeOperation(MihomoEngine.Id), cancellationToken);
            if (!runtime.Succeeded) return Failure(before, runtime.ProblemCode);
            DeleteState(); // Version material is removed by the constrained platform helper.
            return new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.NotInstalled, null, null, false, false);
        }
        finally { _gate.Release(); }
    }

    private async Task StageAndInstallReleaseAsync(MihomoRuntimeRelease release, string finalDirectory, Func<MihomoRuntimeRelease, string, CancellationToken, Task> stageArchiveAsync, Func<string, Task>? stageReporter, CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "remoteos-mihomo-" + Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(stagingRoot, "runtime." + release.ArchiveFormat);
        var staging = Path.Combine(stagingRoot, "release");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            await stageArchiveAsync(release, archive, cancellationToken);
            await ReportStageAsync(stageReporter, "verifying");
            await ReportStageAsync(stageReporter, "extracting");
            await ExtractExpectedBinaryAsync(release, archive, staging, cancellationToken);
            if (!HasExpectedArchitecture(Path.Combine(staging, BinaryName())) || await probe.GetVersionAsync(Path.Combine(staging, BinaryName()), cancellationToken) is null)
                throw new RuntimeInstallException(ProxyProblemCodes.RuntimeHealthCheckFailed);
            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            Directory.Move(staging, finalDirectory);
            MakePrivateDirectory(finalDirectory);
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private async Task DownloadAndVerifyAsync(MihomoRuntimeRelease release, string destination, CancellationToken cancellationToken)
    {
        using var response = await httpClients.CreateClient("MihomoRuntime").GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MihomoRuntimeManifest.MaximumArchiveBytes)
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await CopyAndVerifyArchiveAsync(release, input, destination, cancellationToken);
    }

    private async Task CopyAndVerifyArchiveAsync(MihomoRuntimeRelease release, string archivePath, string destination, CancellationToken cancellationToken)
    {
        if (!TrySafeArchivePath(archivePath, out var path))
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
        var source = new FileInfo(path);
        if (source.Length > MihomoRuntimeManifest.MaximumArchiveBytes)
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
        await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        await CopyAndVerifyArchiveAsync(release, input, destination, cancellationToken);
    }

    private static async Task CopyAndVerifyArchiveAsync(MihomoRuntimeRelease release, Stream input, string destination, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) break;
            total += read; if (total > MihomoRuntimeManifest.MaximumArchiveBytes) throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(release.Sha256)))
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
    }

    private static async Task ExtractExpectedBinaryAsync(MihomoRuntimeRelease release, string archivePath, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var target = Path.Combine(destination, BinaryName());
        if (release.ArchiveFormat == "zip")
        {
            using var archive = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry? binary = null;
            foreach (var entry in archive.Entries)
            {
                EnsureSafeArchiveEntry(entry.FullName, entry.Length);
                if (!entry.FullName.EndsWith("/", StringComparison.Ordinal) && IsExpectedArchiveBinary(Path.GetFileName(entry.FullName.Replace('\\', '/'))))
                {
                    if (binary is not null) throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
                    binary = entry;
                }
            }
            if (binary is null) throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
            await using var input = binary.Open(); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyLimitedAsync(input, output, cancellationToken);
        }
        else if (release.ArchiveFormat == "gz")
        {
            await using var compressed = File.OpenRead(archivePath);
            await using var input = new GZipStream(compressed, CompressionMode.Decompress);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyLimitedAsync(input, output, cancellationToken);
        }
        else throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
        MakePrivateExecutable(target);
    }

    private async Task<ProxyRuntimeDto> RestorePreviousAsync(RuntimeState? before, string attemptedVersion, string problemCode, CancellationToken cancellationToken)
    {
        if (before?.ActiveVersion is { Length: > 0 } previous && File.Exists(ExecutablePath(previous)))
        {
            var replacement = await privileged.ReplaceRuntimeAsync(new ReplaceProxyRuntimeOperation(MihomoEngine.Id, previous, ReleaseDirectoryId(previous)), cancellationToken);
            if (!replacement.Succeeded) return Failure(before, ProxyProblemCodes.RecoveryRequired, attemptedVersion);
            var restart = await privileged.RestartServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            if (!restart.Succeeded || !await controller.IsReachableAsync(cancellationToken))
                return Failure(before, ProxyProblemCodes.RecoveryRequired, attemptedVersion);
        }
        else
        {
            var stop = await privileged.StopServiceAsync(new ProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            var service = await privileged.RemoveServiceAsync(new RemoveProxyServiceOperation(MihomoEngine.Id, ServiceName), cancellationToken);
            var runtime = await privileged.RemoveRuntimeAsync(new RemoveProxyRuntimeOperation(MihomoEngine.Id), cancellationToken);
            if (!stop.Succeeded || !service.Succeeded || !runtime.Succeeded)
                return Failure(before, ProxyProblemCodes.RecoveryRequired, attemptedVersion);
        }
        return Failure(before, problemCode, attemptedVersion);
    }

    private async Task<ProxyPrivilegedResult> EnsureBootstrapConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var directory = paths.GetProtectedConfigurationDirectory();
            Directory.CreateDirectory(directory); MakePrivateDirectory(directory);
            var secret = await controllerSecrets.GetOrCreateAsync(cancellationToken);
            if (secret.Length is < 16 or > 512 || secret.Any(char.IsControl)) return new(false, ProxyProblemCodes.ConfigInvalid);
            var endpoint = controllerOptions.Endpoint;
            var host = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : endpoint.Host;
            var controllerAddress = endpoint.Port == 80 ? host : host + ":" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var temporary = Path.Combine(directory, ".bootstrap-" + Guid.NewGuid().ToString("N"));
            var content = "mixed-port: 7890\nmode: rule\nlog-level: warning\nexternal-controller: " + controllerAddress + "\nsecret: " + secret + "\n";
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            MakePrivateFile(temporary); File.Move(temporary, Path.Combine(directory, "active.yaml"), overwrite: true); MakePrivateFile(Path.Combine(directory, "active.yaml"));
            return new(true);
        }
        catch (ProxyControllerSecretException) { return new(false, ProxyProblemCodes.ConfigApplyFailed); }
        catch (IOException) { return new(false, ProxyProblemCodes.PrivilegedOperationUnavailable); }
        catch (UnauthorizedAccessException) { return new(false, ProxyProblemCodes.PrivilegedOperationUnavailable); }
    }

    private async Task<RuntimeState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        var path = StatePath(); if (!File.Exists(path)) return null;
        try { await using var input = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<RuntimeState>(input, cancellationToken: cancellationToken); }
        catch (JsonException) { return null; }
    }
    private async Task WriteStateAsync(RuntimeState value, CancellationToken cancellationToken)
    {
        var directory = paths.GetStateDirectory(); Directory.CreateDirectory(directory); MakePrivateDirectory(directory);
        var temporary = Path.Combine(directory, ".mihomo-state-" + Guid.NewGuid().ToString("N"));
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(output, value, cancellationToken: cancellationToken);
        MakePrivateFile(temporary); File.Move(temporary, StatePath(), overwrite: true); MakePrivateFile(StatePath());
    }
    private void DeleteState() { if (File.Exists(StatePath())) File.Delete(StatePath()); }
    private string StatePath() => Path.Combine(paths.GetStateDirectory(), "mihomo-runtime.json");
    private string VersionDirectory(string version) => Path.Combine(paths.GetEngineVersionsDirectory(MihomoEngine.Id), ReleaseDirectoryId(version));
    private string ExecutablePath(string version) => Path.Combine(VersionDirectory(version), BinaryName());
    private static string ReleaseDirectoryId(string version) => version + "-" + MihomoRuntimeManifest.CurrentRid();
    private static string BinaryName() => OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo";
    private static ProxyRuntimeDto Unsupported(string engineId) => new(engineId, ProxyRuntimeMode.None, ProxyRuntimeState.Failed, null, null, false, false, ProxyProblemCodes.NotSupported);
    private static ProxyRuntimeDto Failure(RuntimeState? state, string problemCode, string? version = null) => new(MihomoEngine.Id, ProxyRuntimeMode.Managed, ProxyRuntimeState.Failed, version ?? state?.ActiveVersion, state?.PreviousVersion, false, false, string.IsNullOrEmpty(problemCode) ? ProxyProblemCodes.RuntimeHealthCheckFailed : problemCode);
    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
    private static bool TrySafeExternalPath(string value, out string path)
    {
        path = "";
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.GetFullPath(value);
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }
    private static bool TrySafeArchivePath(string value, out string path)
    {
        path = "";
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.GetFullPath(value);
            return File.Exists(path) && !Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }
    private static bool HasExpectedArchitecture(string path)
    {
        try
        {
            using var input = File.OpenRead(path); var header = new byte[512]; var count = input.Read(header, 0, header.Length);
            if (count < 64) return false;
            ushort machine;
            if (header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F') machine = BitConverter.ToUInt16(header, 18);
            else if (header[0] == (byte)'M' && header[1] == (byte)'Z')
            {
                var offset = BitConverter.ToInt32(header, 0x3c);
                if (offset < 0 || offset + 6 > count || header[offset] != (byte)'P' || header[offset + 1] != (byte)'E') return false;
                machine = BitConverter.ToUInt16(header, offset + 4);
            }
            else return false;
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => machine == 0x8664 || machine == 62,
                System.Runtime.InteropServices.Architecture.Arm64 => machine == 0xaa64 || machine == 183,
                _ => false,
            };
        }
        catch { return false; }
    }
    private static void EnsureSafeArchiveEntry(string name, long length)
    {
        var normal = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(name) || length < 0 || length > MihomoRuntimeManifest.MaximumArchiveBytes
            || Path.IsPathRooted(name) || normal.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
    }
    private static bool IsExpectedArchiveBinary(string fileName) => OperatingSystem.IsWindows()
        ? fileName.StartsWith("mihomo", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        : fileName.Equals(BinaryName(), StringComparison.OrdinalIgnoreCase);
    private static async Task CopyLimitedAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) return;
            total += read; if (total > MihomoRuntimeManifest.MaximumArchiveBytes) throw new RuntimeInstallException(ProxyProblemCodes.RuntimeIntegrityFailed);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
    private static async Task ReportStageAsync(Func<string, Task>? stageReporter, string stage)
    {
        if (stageReporter is null) return;
        try { await stageReporter(stage); }
        catch { /* Progress reporting must never interrupt a runtime installation. */ }
    }
    private static void MakePrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void MakePrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    private static void MakePrivateExecutable(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private sealed record RuntimeState(string ActiveVersion, string? PreviousVersion, DateTimeOffset ActivatedAt);
}

public sealed class RuntimeInstallException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
