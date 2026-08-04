// 数据流移植自 Jaya ExplorerViewModel / NavigationViewModel / AddressbarViewModel / ToolbarViewModel /
// StatusbarViewModel（BSD-3），合并为单一 VM 适配 RemoteOS DI 约定（去 ServiceLocator/EventAggregator）。
// Copyright (c) 2020, Rubal Walia. 原始许可见 LICENSE-jaya.txt 与 THIRD_PARTY_NOTICES.md。
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer.ViewModels;

/// <summary>RemoteExplorer 主视图模型。移植自 Jaya <c>ExplorerViewModel</c> + <c>NavigationViewModel</c> +
/// <c>AddressbarViewModel</c> + <c>ToolbarViewModel</c> + <c>StatusbarViewModel</c>（合并以适配 RemoteOS DI 约定，
/// 避免引入 Jaya 的 ServiceLocator/EventAggregator 反射基础设施）。
///
/// 数据流：导航树选中目录 → <see cref="NavigateToAsync"/> → <see cref="IExplorerClient.GetDirectoryAsync"/>
/// → 填充 <see cref="Entries"/> 网格 + <see cref="AddressbarPath"/>。双击目录进入；双击文件下载。
/// 历史栈支持前进/后退/向上。文件操作（删除/重命名/复制/移动/新建/上传/下载）通过对话框回调与宿主交互。</summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private readonly IExplorerClient _client;
    private readonly List<string?> _history = new();
    private int _historyIndex = -1;

    public ExplorerViewModel(IExplorerClient client)
    {
        _client = client;
        Nodes = new ObservableCollection<Models.TreeNodeModel>();
        Entries = new ObservableCollection<FileSystemEntryDto>();
    }

    /// <summary>导航树根节点集合（Computer 下挂各盘符/根）。</summary>
    public ObservableCollection<Models.TreeNodeModel> Nodes { get; }

    /// <summary>Explorer 网格条目（当前目录的子目录 + 文件）。</summary>
    public ObservableCollection<FileSystemEntryDto> Entries { get; }

    [ObservableProperty] private string? _addressbarPath;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private FileSystemEntryDto? _selectedEntry;
    [ObservableProperty] private Models.TreeNodeModel? _selectedNode;

    // 对话框/宿主回调（由 ExplorerApp 注入）
    /// <summary>请求文本输入对话框。参数：(title, prompt, defaultValue, confirmLabel) → 返回输入或 null（取消）。</summary>
    public Func<string, string, string, string, Task<string?>>? RequestTextInputAsync { get; set; }
    /// <summary>请求确认对话框。参数：(title, message, confirmLabel) → 返回 true/false。</summary>
    public Func<string, string, string, Task<bool>>? RequestConfirmAsync { get; set; }
    /// <summary>请求选择本地文件（用于上传源）。返回本地路径或 null。</summary>
    public Func<Task<string?>>? RequestLocalOpenFileAsync { get; set; }
    /// <summary>请求本地保存路径（用于下载目标）。参数：默认文件名。返回本地路径或 null。</summary>
    public Func<string, Task<string?>>? RequestLocalSaveFileAsync { get; set; }
    /// <summary>显示消息（About 等）。参数：(title, message)。</summary>
    public Func<string, string, Task>? ShowMessageAsync { get; set; }
    /// <summary>关闭 Explorer 窗口。</summary>
    public Action? CloseAction { get; set; }

    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex < _history.Count - 1;
    public bool CanGoUp => !string.IsNullOrEmpty(AddressbarPath);
    public bool HasSelection => SelectedEntry is not null;

    // ---- 加载根 ----

    public async Task LoadRootAsync()
    {
        IsBusy = true;
        StatusText = "加载驱动器列表...";
        try
        {
            var drives = await _client.GetDrivesAsync();
            Nodes.Clear();
            var computer = new Models.TreeNodeModel("Computer", null, isComputer: true);
            computer.ExpandRequested = OnNodeExpandRequested;
            foreach (var d in drives)
            {
                var node = new Models.TreeNodeModel(d.Name, d.Path, isDrive: true);
                node.AddDummyChild();
                node.ExpandRequested = OnNodeExpandRequested;
                computer.Children.Add(node);
            }
            Nodes.Add(computer);
            computer.IsExpanded = true;
            StatusText = $"就绪 — {drives.Count} 个驱动器";
        }
        catch (Exception ex) { StatusText = $"加载失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task OnNodeExpandRequested(Models.TreeNodeModel node)
    {
        if (node.IsComputer || string.IsNullOrEmpty(node.Path)) { node.MarkChildrenLoaded(); return; }
        node.IsLoading = true;
        try
        {
            var dir = await _client.GetDirectoryAsync(node.Path);
            node.Children.Clear();
            foreach (var sub in dir.Directories)
            {
                var child = new Models.TreeNodeModel(sub.Name, sub.Path);
                child.AddDummyChild();
                child.ExpandRequested = OnNodeExpandRequested;
                node.Children.Add(child);
            }
            node.MarkChildrenLoaded();
        }
        catch { }
        finally { node.IsLoading = false; }
    }

    partial void OnSelectedNodeChanged(Models.TreeNodeModel? value)
    {
        if (value is null) return;
        _ = value.IsComputer ? NavigateToAsync(null) : NavigateToAsync(value.Path);
    }

    partial void OnAddressbarPathChanged(string? value)
    {
        GoUpCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoUp));
    }

    partial void OnSelectedEntryChanged(FileSystemEntryDto? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        MoveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));
    }

    // ---- 导航 ----

    public async Task NavigateToAsync(string? path)
    {
        await NavigateToAsyncCore(path);
        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(path);
        _historyIndex = _history.Count - 1;
        RefreshHistoryCommands();
    }

    private async Task NavigateToAsyncCore(string? path)
    {
        IsBusy = true;
        try
        {
            Entries.Clear();
            if (path is null)
            {
                var drives = await _client.GetDrivesAsync();
                foreach (var d in drives)
                    Entries.Add(new FileSystemEntryDto(d.Path, d.Name, d.TotalSize,
                        FileSystemEntryType.Drive, null, null, null, false, false));
                AddressbarPath = null;
                StatusText = $"就绪 — {drives.Count} 个驱动器";
            }
            else
            {
                var dir = await _client.GetDirectoryAsync(path);
                foreach (var d in dir.Directories) Entries.Add(d);
                foreach (var f in dir.Files)
                    Entries.Add(new FileSystemEntryDto(f.Path, f.Name, f.Size, FileSystemEntryType.File,
                        f.Created, f.Modified, f.Accessed, f.IsHidden, f.IsSystem));
                AddressbarPath = dir.Path;
                StatusText = $"就绪 — {dir.Directories.Count} 个目录，{dir.Files.Count} 个文件";
            }
        }
        catch (Exception ex) { StatusText = $"加载失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        if (!CanGoBack) return;
        _historyIndex--;
        RefreshHistoryCommands();
        await NavigateToAsyncCore(_history[_historyIndex]);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task GoForwardAsync()
    {
        if (!CanGoForward) return;
        _historyIndex++;
        RefreshHistoryCommands();
        await NavigateToAsyncCore(_history[_historyIndex]);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private async Task GoUpAsync()
    {
        if (string.IsNullOrEmpty(AddressbarPath)) return;
        var parent = Path.GetDirectoryName(AddressbarPath);
        await NavigateToAsync(string.IsNullOrEmpty(parent) ? null : parent);
    }

    [RelayCommand]
    private async Task RefreshAsync()
        => await NavigateToAsyncCore(_history.Count > 0 ? _history[_historyIndex] : AddressbarPath);

    /// <summary>地址栏回车跳转。</summary>
    public async Task AddressbarGoAsync(string? path)
        => await NavigateToAsync(string.IsNullOrWhiteSpace(path) ? null : path.Trim());

    private void RefreshHistoryCommands()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoUp));
    }

    // ---- 双击条目 ----

    public async Task InvokeEntryAsync(FileSystemEntryDto entry)
    {
        if (entry.Type == FileSystemEntryType.Directory || entry.Type == FileSystemEntryType.Drive)
            await NavigateToAsync(entry.Path);
    }

    // ---- 文件操作 ----

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (string.IsNullOrEmpty(AddressbarPath))
        {
            StatusText = "请先进入一个驱动器或目录";
            return;
        }
        var name = await (RequestTextInputAsync?.Invoke("新建文件夹", "请输入文件夹名称：", "新建文件夹", "创建") ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var target = Path.Combine(AddressbarPath, name);
            await _client.CreateDirectoryAsync(target);
            StatusText = $"已创建文件夹：{name}";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = $"创建失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var confirmed = await (RequestConfirmAsync?.Invoke("删除",
            $"确定要删除 \"{entry.Name}\" 吗？\n如果是文件夹，其所有内容将被递归删除。",
            "删除") ?? Task.FromResult(false));
        if (!confirmed) return;
        try
        {
            await _client.DeleteAsync(entry.Path);
            StatusText = $"已删除：{entry.Name}";
            SelectedEntry = null;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = $"删除失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var newName = await (RequestTextInputAsync?.Invoke("重命名", "请输入新名称：", entry.Name, "重命名")
            ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;
        try
        {
            await _client.RenameAsync(entry.Path, newName);
            StatusText = $"已重命名为：{newName}";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = $"重命名失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var dest = await (RequestTextInputAsync?.Invoke("复制", "请输入目标完整路径：", entry.Path, "复制")
            ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(dest) || dest == entry.Path) return;
        try
        {
            await _client.CopyAsync(entry.Path, dest, overwrite: false);
            StatusText = $"已复制到：{dest}";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = $"复制失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var dest = await (RequestTextInputAsync?.Invoke("移动", "请输入目标完整路径：", entry.Path, "移动")
            ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(dest) || dest == entry.Path) return;
        try
        {
            await _client.MoveAsync(entry.Path, dest, overwrite: false);
            StatusText = $"已移动到：{dest}";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = $"移动失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DownloadAsync()
    {
        if (SelectedEntry is not { } entry) return;
        if (entry.Type == FileSystemEntryType.Directory || entry.Type == FileSystemEntryType.Drive)
        {
            StatusText = "暂不支持下载文件夹";
            return;
        }
        var localPath = await (RequestLocalSaveFileAsync?.Invoke(entry.Name) ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(localPath)) return;
        try
        {
            var r = await _client.DownloadAsync(entry.Path);
            if (r is not (var stream, _))
            {
                StatusText = "下载失败：文件不存在";
                return;
            }
            using (stream)
            using (var fs = File.Create(localPath))
                await stream.CopyToAsync(fs);
            StatusText = $"已下载到：{localPath}";
        }
        catch (Exception ex) { StatusText = $"下载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (string.IsNullOrEmpty(AddressbarPath))
        {
            StatusText = "请先进入一个目标目录";
            return;
        }
        var localPath = await (RequestLocalOpenFileAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(localPath)) return;
        try
        {
            var fileName = Path.GetFileName(localPath);
            using var fs = File.OpenRead(localPath);
            await _client.UploadAsync(AddressbarPath, fileName, fs);
            StatusText = $"已上传：{fileName}";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = $"上传失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task AboutAsync()
    {
        await (ShowMessageAsync?.Invoke("关于 RemoteExplorer",
            "RemoteExplorer v1.0.0\nUI 移植自 Jaya File Manager (BSD-3)\n所有文件操作经 Server 端 REST API 执行，复用宿主 OS 用户/权限。")
            ?? Task.CompletedTask);
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}
