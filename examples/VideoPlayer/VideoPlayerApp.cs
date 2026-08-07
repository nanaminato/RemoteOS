using System.Reflection;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.WindowManager;
using RemoteRect = RemoteOS.Core.Primitives.Rect;

namespace RemoteOS.Examples.VideoPlayer;

/// <summary>
/// Windows development-package example. Remote media is exposed as a host-renewed, single-file
/// HTTP lease and played by LibVLC without writing the video to a local temporary file.
/// </summary>
public sealed class VideoPlayerApp : IExternalRemoteApplication, IExternalFileOpenApplication
{
    public ApplicationManifest Manifest { get; } = new(
        new AppId("com.remoteos.example.video-player"),
        "Video Player",
        "0.1.0-dev",
        "🎬",
        "Example VLC-based remote video player for Windows",
        [AppPermissions.ServerFilesRead]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Video Player", FontSize = 22, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Open a supported video from RemoteExplorer. This sample requests only server.files.read and plays the selected file through a short-lived host-managed media lease.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Opacity = 0.75,
                },
            },
        };
        context.Windows.ShowWindow("Video Player", content, new RemoteRect(190, 130, 540, 250), "🎬");
        return Task.CompletedTask;
    }

    public Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default)
    {
        var status = new TextBlock { Text = "Preparing video…", Opacity = 0.75 };
        var videoView = new VideoView();
        var playPause = new Button { Content = "Play / Pause", IsEnabled = false };
        var stop = new Button { Content = "Stop", IsEnabled = false };
        var header = new Border
        {
            Padding = new Thickness(14, 10),
            Background = Avalonia.Media.Brushes.White,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = Path.GetFileName(path), FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    status,
                },
            },
        };
        var videoSurface = new Border { Background = Avalonia.Media.Brushes.Black, Child = videoView };
        Grid.SetRow(videoSurface, 1);
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(14, 10),
            Children = { playPause, stop },
        };
        Grid.SetRow(controls, 2);

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        content.Children.Add(header);
        content.Children.Add(videoSurface);
        content.Children.Add(controls);

        var handle = context.Windows.ShowWindow("Video Player", content, new RemoteRect(130, 80, 960, 640), "🎬");
        var session = new PlaybackSession(videoView);
        void SyncVideoVisibility() => videoView.IsVisible = handle.Window.IsActive && handle.Window.IsOnScreen;
        PropertyChangedEventHandler windowChanged = (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(ManagedWindow.IsActive) or nameof(ManagedWindow.State))
                SyncVideoVisibility();
        };
        // VideoView is backed by a native child window and ignores the managed desktop's
        // ZIndex. Only expose it while its owning managed window is active, matching the
        // NativeWebView behavior in the built-in browser.
        handle.Window.PropertyChanged += windowChanged;
        handle.Closed.Register(() =>
        {
            handle.Window.PropertyChanged -= windowChanged;
            _ = session.DisposeAsync().AsTask();
        });
        SyncVideoVisibility();
        playPause.Click += (_, _) =>
        {
            if (session.Player is null) return;
            if (session.Player.IsPlaying) session.Player.Pause();
            else session.Player.Play();
        };
        stop.Click += (_, _) => session.Player?.Stop();
        _ = LoadAndPlayAsync(context, path, handle.Closed, session, status, playPause, stop);
        return Task.CompletedTask;
    }

    private static async Task LoadAndPlayAsync(
        IExternalAppContext context,
        string remotePath,
        CancellationToken closed,
        PlaybackSession session,
        TextBlock status,
        Button playPause,
        Button stop)
    {
        try
        {
            var playback = await context.Media.OpenPlaybackAsync(remotePath, closed);
            if (playback.Status == AppCapabilityResult.PermissionDenied)
            {
                SetStatus(status, "Permission denied. Grant ‘读取服务器文件’ in Settings → Applications → Video Player.");
                return;
            }
            if (playback.Status != AppCapabilityResult.Succeeded || playback.Lease is null)
            {
                SetStatus(status, "The video file is unavailable.");
                return;
            }

            SetStatus(status, "Downloading remote video for playback…");
            session.Play(playback.Lease.PlaybackUri, playback.Lease);
            SetStatus(status, "Starting VLC playback…");
            Dispatcher.UIThread.Post(() =>
            {
                status.Text = "Playing";
                playPause.IsEnabled = true;
                stop.IsEnabled = true;
            });
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SetStatus(status, $"Cannot play video: {exception.Message}");
        }
    }

    private static void SetStatus(TextBlock status, string value) =>
        Dispatcher.UIThread.Post(() => status.Text = value);

    private sealed class PlaybackSession(VideoView videoView) : IAsyncDisposable
    {
        private static int _coreInitialized;
        private LibVLC? _libVlc;
        private Media? _media;
        private IExternalMediaLease? _lease;

        public MediaPlayer? Player { get; private set; }

        public void Play(Uri playbackUri, IExternalMediaLease lease)
        {
            InitializeCore();
            _libVlc = new LibVLC("--no-video-title-show");
            Player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
            videoView.MediaPlayer = Player;
            _lease = lease;
            _media = new Media(_libVlc, playbackUri);
            Player.Play(_media);
        }

        public async ValueTask DisposeAsync()
        {
            Player?.Stop();
            videoView.MediaPlayer = null;
            _media?.Dispose();
            Player?.Dispose();
            _libVlc?.Dispose();
            if (_lease is not null)
                await _lease.DisposeAsync();
            _lease = null;
        }

        private static void InitializeCore()
        {
            if (Interlocked.Exchange(ref _coreInitialized, 1) != 0)
                return;

            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var nativeDirectory = Path.Combine(assemblyDirectory, "libvlc", "win-x64");
            LibVLCSharp.Shared.Core.Initialize(Directory.Exists(nativeDirectory) ? nativeDirectory : assemblyDirectory);
        }
    }
}
