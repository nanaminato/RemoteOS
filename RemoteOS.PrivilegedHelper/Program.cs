using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteOS.Protocol.Privileged;

if (OperatingSystem.IsLinux() && geteuid() != 0)
{
    await WriteResultAsync(new(false, 77, Error: "root is required"));
    return 77;
}

PrivilegedOperationRequest? request;
try
{
    request = await JsonSerializer.DeserializeAsync<PrivilegedOperationRequest>(Console.OpenStandardInput());
}
catch (JsonException)
{
    await WriteResultAsync(new(false, 64, Error: "invalid request"));
    return 64;
}

if (request is null)
{
    await WriteResultAsync(new(false, 64, Error: "missing request"));
    return 64;
}

var result = await ExecuteAsync(request);
await WriteResultAsync(result);
return result.ExitCode;

static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
{
    try
    {
        return request.Operation switch
        {
            "read-file" => await ReadFileAsync(request.Path),
            "write-file" => await WriteFileAsync(request.Path, request.ContentBase64),
            "delete" => Delete(request.Path),
            "rename" => Rename(request.Path, request.NewName),
            "move" => Move(request.Path, request.DestinationPath, request.Overwrite),
            "copy" => Copy(request.Path, request.DestinationPath, request.Overwrite),
            "upload" => await UploadAsync(request.Path, request.FileName, request.ContentBase64),
            "create-directory" => CreateDirectory(request.Path),
            "run" => await RunAsync(request),
            _ => new(false, 64, Error: "unknown operation"),
        };
    }
    catch (UnauthorizedAccessException ex) { return new(false, 77, Error: ex.Message); }
    catch (DirectoryNotFoundException ex) { return new(false, 2, Error: ex.Message); }
    catch (FileNotFoundException ex) { return new(false, 2, Error: ex.Message); }
    catch (ArgumentException ex) { return new(false, 64, Error: ex.Message); }
    catch (Exception ex) { return new(false, 1, Error: ex.Message); }
}

static async Task<PrivilegedOperationResult> ReadFileAsync(string? path)
{
    if (string.IsNullOrWhiteSpace(path)) return new(false, 64, Error: "path is required");
    var bytes = await File.ReadAllBytesAsync(path);
    return new(true, OutputBase64: Convert.ToBase64String(bytes));
}

static async Task<PrivilegedOperationResult> WriteFileAsync(string? path, string? contentBase64)
{
    if (string.IsNullOrWhiteSpace(path) || contentBase64 is null) return new(false, 64, Error: "path and content are required");
    var directory = Path.GetDirectoryName(path);
    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        throw new DirectoryNotFoundException($"Target directory does not exist: {directory}");
    await File.WriteAllBytesAsync(path, Convert.FromBase64String(contentBase64));
    return new(true);
}

static PrivilegedOperationResult Delete(string? path)
{
    if (string.IsNullOrWhiteSpace(path)) return new(false, 64, Error: "path is required");
    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    else if (File.Exists(path)) File.Delete(path);
    else throw new FileNotFoundException("Path does not exist.", path);
    return new(true);
}

static PrivilegedOperationResult Rename(string? sourcePath, string? newName)
{
    if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(newName)) return new(false, 64, Error: "path and newName are required");
    var parent = Path.GetDirectoryName(sourcePath);
    var destination = Path.Combine(parent ?? string.Empty, newName);
    if (Directory.Exists(sourcePath)) new DirectoryInfo(sourcePath).MoveTo(destination);
    else if (File.Exists(sourcePath)) File.Move(sourcePath, destination);
    else throw new FileNotFoundException("Path does not exist.", sourcePath);
    return new(true);
}

static PrivilegedOperationResult Move(string? sourcePath, string? destinationPath, bool overwrite)
{
    if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath)) return new(false, 64, Error: "path and destinationPath are required");
    if (Directory.Exists(sourcePath))
    {
        if (Directory.Exists(destinationPath) && overwrite) Directory.Delete(destinationPath, recursive: true);
        Directory.Move(sourcePath, destinationPath);
    }
    else if (File.Exists(sourcePath)) File.Move(sourcePath, destinationPath, overwrite);
    else throw new FileNotFoundException("Path does not exist.", sourcePath);
    return new(true);
}

static PrivilegedOperationResult Copy(string? sourcePath, string? destinationPath, bool overwrite)
{
    if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath)) return new(false, 64, Error: "path and destinationPath are required");
    if (Directory.Exists(sourcePath))
    {
        if (Directory.Exists(destinationPath) && overwrite) Directory.Delete(destinationPath, recursive: true);
        CopyDirectory(sourcePath, destinationPath);
    }
    else if (File.Exists(sourcePath)) File.Copy(sourcePath, destinationPath, overwrite);
    else throw new FileNotFoundException("Path does not exist.", sourcePath);
    return new(true);
}

static async Task<PrivilegedOperationResult> UploadAsync(string? targetDirectoryPath, string? fileName, string? contentBase64)
{
    if (string.IsNullOrWhiteSpace(targetDirectoryPath) || string.IsNullOrWhiteSpace(fileName) || contentBase64 is null)
        return new(false, 64, Error: "path, fileName and content are required");
    if (!Directory.Exists(targetDirectoryPath)) throw new DirectoryNotFoundException($"Target directory does not exist: {targetDirectoryPath}");
    await File.WriteAllBytesAsync(Path.Combine(targetDirectoryPath, fileName), Convert.FromBase64String(contentBase64));
    return new(true);
}

static PrivilegedOperationResult CreateDirectory(string? path)
{
    if (string.IsNullOrWhiteSpace(path)) return new(false, 64, Error: "path is required");
    if (Directory.Exists(path)) return new(false, 17, Error: "directory already exists");
    Directory.CreateDirectory(path);
    return new(true);
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
    foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
}

static async Task<PrivilegedOperationResult> RunAsync(PrivilegedOperationRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Executable)) return new(false, 64, Error: "executable is required");
    var start = new ProcessStartInfo(request.Executable)
    {
        RedirectStandardInput = request.StandardInputBase64 is not null,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var argument in request.Arguments ?? []) start.ArgumentList.Add(argument);
    using var process = Process.Start(start);
    if (process is null) return new(false, 1, Error: "process could not be started");
    if (request.StandardInputBase64 is not null)
    {
        var input = Convert.FromBase64String(request.StandardInputBase64);
        await process.StandardInput.BaseStream.WriteAsync(input);
        await process.StandardInput.DisposeAsync();
    }
    var output = ReadAllBytesAsync(process.StandardOutput.BaseStream);
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var outputBytes = await output;
    var errorText = await error;
    return new(process.ExitCode == 0, process.ExitCode, Convert.ToBase64String(outputBytes), string.IsNullOrWhiteSpace(errorText) ? null : errorText);
}

static Task WriteResultAsync(PrivilegedOperationResult result) =>
    JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result);

static async Task<byte[]> ReadAllBytesAsync(Stream stream)
{
    await using var buffer = new MemoryStream();
    await stream.CopyToAsync(buffer);
    return buffer.ToArray();
}

[DllImport("libc")]
static extern uint geteuid();
