using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Examples.VideoPlayer.Services;

namespace RemoteOS.Examples.VideoPlayer.ViewModels;

/// <summary>Coordinates the permission-gated media lease and playback service for one file.</summary>
public sealed partial class VideoPlayerViewModel : ObservableObject
{
    private readonly IExternalMediaService _media;
    private readonly string _path;
    private readonly LibVlcPlaybackService _playback;

    public VideoPlayerViewModel(IExternalMediaService media, string path, LibVlcPlaybackService playback)
    {
        _media = media;
        _path = path;
        _playback = playback;
        FileName = Path.GetFileName(path);
    }

    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _statusText = "正在准备视频…";
    [ObservableProperty] private bool _canControlPlayback;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusText = "正在创建受控播放连接…";
            var result = await _media.OpenPlaybackAsync(_path, cancellationToken);
            if (result.Status == AppCapabilityResult.PermissionDenied)
            {
                StatusText = "没有读取服务器文件的权限。请在 设置 → 应用程序 → Video Player 中授权。";
                return;
            }
            if (result.Status != AppCapabilityResult.Succeeded || result.Lease is null)
            {
                StatusText = result.Detail ?? "视频文件当前不可用。";
                return;
            }

            StatusText = "正在启动 VLC 播放器…";
            _playback.Start(result.Lease.PlaybackUri, result.Lease);
            CanControlPlayback = true;
            StatusText = "正在播放";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusText = $"无法播放视频：{exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanControlPlayback))]
    private void TogglePlayPause()
    {
        _playback.TogglePlayPause();
        StatusText = _playback.IsPlaying ? "正在播放" : "已暂停";
    }

    [RelayCommand(CanExecute = nameof(CanControlPlayback))]
    private void Stop()
    {
        _playback.Stop();
        StatusText = "已停止";
    }

    partial void OnCanControlPlaybackChanged(bool value)
    {
        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
