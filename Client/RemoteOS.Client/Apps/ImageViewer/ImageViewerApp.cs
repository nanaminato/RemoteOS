using Client.Apps.Explorer;
using Client.Apps.ImageViewer.ViewModels;
using Client.Apps.ImageViewer.Views;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.ImageViewer;

/// <summary>Lightweight built-in viewer for common remote image files.</summary>
public sealed class ImageViewerApp : RemoteApplicationBase, IFileOpenApplication
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".webp", ".ico"];

    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.imageviewer"),
        DisplayName: "Image Viewer",
        Version: "1.0.0",
        IconGlyph: "🖼️",
        Description: "Lightweight viewer for remote image files",
        SupportedFileExtensions: SupportedExtensions);

    public override void Activate(AppContext context) => OpenViewer(context, null);

    public void OpenFile(AppContext context, string path) => OpenViewer(context, path);

    private void OpenViewer(AppContext context, string? path)
    {
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var viewModel = new ImageViewerViewModel(files);
        var view = new ImageViewerView { DataContext = viewModel };
        var window = context.ShowWindow("Image Viewer", view,
            bounds: new Rect(180, 100, 880, 640),
            iconGlyph: Manifest.IconGlyph);

        EventHandler<RemoteOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            viewModel.Dispose();
        };
        context.WindowManager.WindowClosed += closed;

        if (!string.IsNullOrWhiteSpace(path))
            _ = viewModel.OpenPathAsync(path);
    }
}
