using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Examples.VideoPlayer.Services;
using RemoteOS.Examples.VideoPlayer.ViewModels;
using RemoteOS.Examples.VideoPlayer.Views;
using RemoteRect = RemoteOS.Core.Primitives.Rect;

namespace RemoteOS.Examples.VideoPlayer;

/// <summary>Composition root for the Video Player development package.</summary>
public sealed class VideoPlayerApp : IExternalRemoteApplication, IExternalFileOpenApplication
{
    public ApplicationManifest Manifest { get; } = new(
        new AppId("com.remoteos.example.video-player"),
        "Video Player",
        "0.2.0-dev",
        "🎬",
        "VLC-based remote video player for Windows",
        [AppPermissions.ServerFilesRead]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        context.Windows.ShowWindow("Video Player", new VideoPlayerHomeView(),
            new RemoteRect(190, 130, 540, 250), Manifest.IconGlyph);
        return Task.CompletedTask;
    }

    public Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default)
    {
        var playback = new LibVlcPlaybackService();
        var viewModel = new VideoPlayerViewModel(context.Media, path, playback);
        var view = new VideoPlayerView { DataContext = viewModel };
        playback.Attach(view.VideoSurface);
        var window = context.Windows.ShowWindow("Video Player", view,
            new RemoteRect(130, 80, 960, 640), Manifest.IconGlyph);
        _ = new VideoPlayerWindowLifetime(window, playback);
        _ = viewModel.OpenAsync(window.Closed);
        return Task.CompletedTask;
    }
}
