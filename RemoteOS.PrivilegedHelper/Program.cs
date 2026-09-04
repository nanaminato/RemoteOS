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
