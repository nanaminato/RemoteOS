using System;
using Avalonia;

namespace RemoteOS.Sketch.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<RemoteOS.Sketch.Client.App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
