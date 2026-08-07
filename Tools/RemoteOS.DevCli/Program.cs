using System.Net.Http.Headers;

const string defaultEndpoint = "http://127.0.0.1:45321/api/developer/v1/";
var arguments = args.ToList();
var token = ReadOption(arguments, "--token") ?? Environment.GetEnvironmentVariable("REMOTEOS_DEV_TOKEN");
var endpoint = ReadOption(arguments, "--endpoint") ?? Environment.GetEnvironmentVariable("REMOTEOS_DEV_ENDPOINT") ?? defaultEndpoint;

if (string.IsNullOrWhiteSpace(token) || arguments.Count == 0)
{
    PrintUsage();
    return 2;
}

using var http = new HttpClient { BaseAddress = new Uri(endpoint, UriKind.Absolute) };
http.DefaultRequestHeaders.Add("X-RemoteOS-Dev-Token", token);

try
{
    switch (arguments[0].ToLowerInvariant())
    {
        case "apps":
            await SendAsync(new HttpRequestMessage(HttpMethod.Get, "apps"));
            break;
        case "install" when arguments.Count == 2:
            await InstallAsync(arguments[1], launch: true);
            break;
        case "update" when arguments.Count == 2:
            await InstallAsync(arguments[1], launch: true);
            break;
        case "watch" when arguments.Count == 2:
            await WatchAsync(arguments[1]);
            break;
        case "launch" when arguments.Count == 2:
            await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"apps/{Uri.EscapeDataString(arguments[1])}/launch"));
            break;
        case "uninstall" when arguments.Count == 2:
            await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"apps/{Uri.EscapeDataString(arguments[1])}"));
            break;
        default:
            PrintUsage();
            return 2;
    }
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"Cannot reach the RemoteOS Developer Bridge: {exception.Message}");
    return 1;
}
catch (FileNotFoundException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

return 0;

async Task InstallAsync(string packagePath, bool launch)
{
    var fullPath = Path.GetFullPath(packagePath);
    if (!File.Exists(fullPath))
        throw new FileNotFoundException("Development package was not found.", fullPath);

    await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var request = new HttpRequestMessage(HttpMethod.Post, $"packages?launch={launch.ToString().ToLowerInvariant()}")
    {
        Content = new StreamContent(stream),
    };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    await SendAsync(request);
}

async Task WatchAsync(string packagePath)
{
    var fullPath = Path.GetFullPath(packagePath);
    Console.WriteLine($"Watching {fullPath}");
    await InstallAsync(fullPath, launch: true);
    using var watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath))
    {
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        EnableRaisingEvents = true,
    };
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
    var lastChange = DateTime.MinValue;
    watcher.Changed += async (_, _) =>
    {
        if (DateTime.UtcNow - lastChange < TimeSpan.FromMilliseconds(500))
            return;
        lastChange = DateTime.UtcNow;
        try
        {
            await Task.Delay(350, stop.Token);
            await InstallAsync(fullPath, launch: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Console.Error.WriteLine($"Update failed: {exception.Message}"); }
    };
    watcher.Created += async (_, _) =>
    {
        try
        {
            await Task.Delay(350, stop.Token);
            await InstallAsync(fullPath, launch: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Console.Error.WriteLine($"Update failed: {exception.Message}"); }
    };
    watcher.Renamed += async (_, _) =>
    {
        try
        {
            await Task.Delay(350, stop.Token);
            await InstallAsync(fullPath, launch: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Console.Error.WriteLine($"Update failed: {exception.Message}"); }
    };
    await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token).ContinueWith(_ => { });
}

async Task SendAsync(HttpRequestMessage request)
{
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"Developer Bridge returned {(int)response.StatusCode}: {body}");
    Console.WriteLine(body);
}

static string? ReadOption(List<string> arguments, string option)
{
    var index = arguments.FindIndex(value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
    if (index < 0)
        return null;
    if (index == arguments.Count - 1)
        throw new ArgumentException($"{option} requires a value.");
    var value = arguments[index + 1];
    arguments.RemoveAt(index + 1);
    arguments.RemoveAt(index);
    return value;
}

static void PrintUsage()
{
    Console.WriteLine("""
Usage: remoteos-dev [--token <pairing-token>] [--endpoint <url>] <command>

Commands:
  apps
  install <package.roapp>
  update <package.roapp>
  watch <package.roapp>
  launch <app-id>
  uninstall <app-id>

Set REMOTEOS_DEV_TOKEN to avoid passing the pairing token on each command.
""");
}
