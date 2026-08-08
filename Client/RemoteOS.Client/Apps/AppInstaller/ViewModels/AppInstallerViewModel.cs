using System.Collections.ObjectModel;
using Client.Services.AppPackages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Core.Applications;

namespace Client.Apps.AppInstaller.ViewModels;

/// <summary>Queues packages and requires an explicit install/update action for every package.</summary>
public sealed partial class AppInstallerViewModel : ObservableObject, IDisposable
{
    private readonly AppPackageInstallerService _installer;
    private readonly Queue<AppPackageCandidate> _pending = new();

    public AppInstallerViewModel(AppPackageInstallerService installer) => _installer = installer;

    public Func<Task<IReadOnlyList<string>>>? RequestLocalPackagesAsync { get; set; }
    public Func<Task<IReadOnlyList<string>>>? RequestServerPackagesAsync { get; set; }
    public Func<string, string, Task>? ShowMessageAsync { get; set; }

    [ObservableProperty] private AppPackageCandidate? _currentPackage;
    [ObservableProperty] private string _status = "选择本机或服务器上的 .roapp 应用包。";
    [ObservableProperty] private bool _isBusy;

    public bool HasPackage => CurrentPackage is not null;
    public string ActionLabel => CurrentPackage?.IsUpdate == true ? "更新" : "安装";
    public string CurrentVersionText => CurrentPackage?.InstalledVersion is null ? "未安装" : $"当前版本：{CurrentPackage.InstalledVersion}";
    public int PendingCount => _pending.Count;
    public IReadOnlyList<AppPermissionDefinition> RequestedPermissions => CurrentPackage?.Manifest.RequestedPermissions
        ?.Select(AppPermissions.Find).OfType<AppPermissionDefinition>().ToArray() ?? [];
    public bool HasRequestedPermissions => RequestedPermissions.Count > 0;

    [RelayCommand]
    private async Task ChooseLocalAsync()
    {
        if (RequestLocalPackagesAsync is null) return;
        await QueueLocalPackagesAsync(await RequestLocalPackagesAsync());
    }

    [RelayCommand]
    private async Task ChooseServerAsync()
    {
        if (RequestServerPackagesAsync is null) return;
        await QueueServerPackagesAsync(await RequestServerPackagesAsync());
    }

    public async Task QueueLocalPackagesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { _pending.Enqueue(await _installer.InspectLocalAsync(path)); }
            catch (Exception exception) { await ReportAsync("无法读取应用包", $"{Path.GetFileName(path)}：{exception.Message}"); }
        }
        ShowNext();
    }

    public async Task QueueServerPackagesAsync(IEnumerable<string> paths)
    {
        IsBusy = true;
        Status = "正在下载所选应用包到本机临时目录…";
        try
        {
            foreach (var path in paths)
            {
                try { _pending.Enqueue(await _installer.StageServerPackageAsync(path)); }
                catch (Exception exception) { await ReportAsync("无法下载应用包", $"{Path.GetFileName(path)}：{exception.Message}"); }
            }
        }
        finally { IsBusy = false; }
        ShowNext();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (CurrentPackage is null) return;
        IsBusy = true;
        try
        {
            var installed = await _installer.InstallAsync(CurrentPackage);
            Status = $"{(CurrentPackage.IsUpdate ? "已更新" : "已安装")} {installed.DisplayName} {installed.Version}。";
            CurrentPackage = null;
            ShowNext();
        }
        catch (Exception exception)
        {
            Status = $"安装失败：{exception.Message}";
            await ReportAsync("安装失败", Status);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip()
    {
        if (CurrentPackage is not null) _installer.Discard(CurrentPackage);
        CurrentPackage = null;
        Status = "已跳过应用包。";
        ShowNext();
    }

    private bool CanInstall() => CurrentPackage is not null && !IsBusy;
    private bool CanSkip() => CurrentPackage is not null && !IsBusy;

    partial void OnCurrentPackageChanged(AppPackageCandidate? value)
    {
        OnPropertyChanged(nameof(HasPackage));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(RequestedPermissions));
        OnPropertyChanged(nameof(HasRequestedPermissions));
        InstallCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
    }

    private void ShowNext()
    {
        if (CurrentPackage is not null || _pending.Count == 0) return;
        CurrentPackage = _pending.Dequeue();
        Status = CurrentPackage.IsUpdate ? "请确认更新此应用。" : "请确认安装此应用。";
        OnPropertyChanged(nameof(PendingCount));
    }

    private Task ReportAsync(string title, string message) => ShowMessageAsync?.Invoke(title, message) ?? Task.CompletedTask;

    public void Dispose()
    {
        if (CurrentPackage is not null)
            _installer.Discard(CurrentPackage);
        while (_pending.TryDequeue(out var candidate))
            _installer.Discard(candidate);
    }
}
