using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Browser;

namespace Client.Apps.Browser.ViewModels;

/// <summary>RemoteBrowser 主视图模型。
///
/// 数据流：
/// - 用户输入地址 → <see cref="NavigateCommand"/> → 设置 <see cref="WebViewSource"/>（Uri 绑定到 NativeWebView.Source）
/// - NativeWebView.NavigationStarted/Completed 事件由 View code-behind 转发到
///   <see cref="OnNavigationStarted"/> / <see cref="OnNavigationCompleted"/>，更新地址栏 + 记录历史
/// - 书签/历史通过 <see cref="IBrowserClient"/> 调用 Server REST API（JWT via IAuthSession）
/// - Sidebar 双标签页（书签 / 历史），点击条目导航，X 删除单条，"清空"清全部
///
/// 注意：WebView 的实际渲染在客户端完成（NativeWebView 用平台原生引擎：Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），
/// 网页内容走客户端网络而非 Server；Server 仅持久化书签与历史（按用户隔离）。</summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private readonly IBrowserClient _client;
    private bool _savedLocalPortForwardingEnabled;
    private Uri? _currentUri;          // 当前 WebView 实际加载的 URI（区别于地址栏文本，可能正在输入未提交）

    public BrowserViewModel(IBrowserClient client)
    {
        _client = client;
        Bookmarks = new ObservableCollection<BookmarkDto>();
        History = new ObservableCollection<HistoryEntryDto>();
    }

    /// <summary>书签列表（侧边栏"书签"标签页绑定）。</summary>
    public ObservableCollection<BookmarkDto> Bookmarks { get; }

    /// <summary>历史记录列表（侧边栏"历史记录"标签页绑定，按 LastVisitedAt 倒序）。</summary>
    public ObservableCollection<HistoryEntryDto> History { get; }

    [ObservableProperty] private Uri? _webViewSource;
    [ObservableProperty] private string _addressText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private bool _isCurrentBookmarked;
    [ObservableProperty] private SidebarTab _activeSidebarTab = SidebarTab.Bookmarks;
    [ObservableProperty] private bool _isSidebarVisible = true;
    [ObservableProperty] private bool _isLocalPortForwardingEnabled;
    [ObservableProperty] private string _localPortForwardingStatus = "本地端口映射：关闭";

    /// <summary>主页 URI（地址栏"主页"按钮的目标）。可空：未设置时不显示主页按钮或 no-op。</summary>
    public Uri? HomePage { get; set; } = new("https://www.bing.com");

    /// <summary>关闭窗口回调（由 BrowserApp 注入）。</summary>
    public Action? CloseAction { get; set; }
    public Func<Task>? RequestSettingsAsync { get; set; }
    public Action? CloseSettingsAction { get; set; }

    /// <summary>由 View code-behind 在 NativeWebView.CanGoBack/CanGoForward 变化时调用。</summary>
    public void UpdateNavigationState(bool canGoBack, bool canGoForward)
    {
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
    }

    /// <summary>由 View code-behind 在 NativeWebView.NavigationStarted 触发时调用。
    /// 更新地址栏为实际正在加载的 URI；记录"开始加载"状态。</summary>
    public void OnNavigationStarted(Uri url)
    {
        var displayUrl = ToDisplayUrl(url);
        _currentUri = displayUrl;
        AddressText = displayUrl.IsAbsoluteUri ? displayUrl.ToString() : displayUrl.OriginalString;
        IsLoading = true;
        StatusText = $"正在加载 {displayUrl}...";
    }

    /// <summary>由 View code-behind 在 NativeWebView.NavigationCompleted 触发时调用。
    /// 成功加载时记录历史（去重，单次导航只记一次），刷新书签星标。</summary>
    public async void OnNavigationCompleted(Uri? url, bool isSuccess)
    {
        IsLoading = false;
        if (url is not null && isSuccess)
        {
            var displayUrl = ToDisplayUrl(url);
            _currentUri = displayUrl;
            AddressText = displayUrl.ToString();
            StatusText = $"完成 — {displayUrl}";
            // 异步记录到服务端历史（fire-and-forget 友好：失败只更新状态栏不阻塞 UI）
            _ = RecordVisitAsync(displayUrl);
            _ = RefreshBookmarkStarAsync(displayUrl);
        }
        else
        {
            StatusText = url is null ? "已停止" : $"加载失败：{url}";
        }
    }

    /// <summary>由 View code-behind 在 NativeWebView.GoBack/GoForward 实际执行后调用，
    /// 同步当前 URI（用于历史记录与书签星标刷新）。</summary>
    public void OnNavigatedToExisting(Uri url)
    {
        var displayUrl = ToDisplayUrl(url);
        _currentUri = displayUrl;
        AddressText = displayUrl.IsAbsoluteUri ? displayUrl.ToString() : displayUrl.OriginalString;
        _ = RefreshBookmarkStarAsync(displayUrl);
    }

    // ---- 导航命令 ----

    [RelayCommand]
    private void Navigate(string? address)
    {
        var uri = NormalizeAddress(address);
        if (uri is null)
        {
            StatusText = "地址无效";
            return;
        }
        var displayUri = uri;
        if (IsLocalPortForwardingEnabled && IsLoopbackTarget(uri))
        {
            try { uri = _client.CreateLocalPortForwardingUri(uri); }
            catch (Exception ex)
            {
                StatusText = $"无法创建本地端口映射：{ex.Message}";
                return;
            }
        }

        WebViewSource = uri;
        // OnNavigationStarted 由 View 转发；这里也同步一份防事件丢失
        if (_currentUri != uri)
        {
            _currentUri = displayUri;
            AddressText = displayUri.ToString();
            IsLoading = true;
            StatusText = $"正在加载 {displayUri}...";
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => ViewGoBackRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => ViewGoForwardRequested?.Invoke();

    [RelayCommand]
    private void Refresh() => ViewRefreshRequested?.Invoke();

    [RelayCommand]
    private void Stop() => ViewStopRequested?.Invoke();

    [RelayCommand]
    private void GoHome()
    {
        if (HomePage is not null) Navigate(HomePage.ToString());
    }

    /// <summary>由 View code-behind 注入：当 GoBack/GoForward/Refresh/Stop 命令触发时，
    /// View 接管实际调用 NativeWebView 对应方法（VM 不持有 WebView 引用）。</summary>
    public Action? ViewGoBackRequested { get; set; }
    public Action? ViewGoForwardRequested { get; set; }
    public Action? ViewRefreshRequested { get; set; }
    public Action? ViewStopRequested { get; set; }

    // ---- 书签 ----

    [RelayCommand]
    private async Task ToggleBookmarkAsync()
    {
        if (_currentUri is null) return;
        var url = _currentUri.IsAbsoluteUri ? _currentUri.ToString() : _currentUri.OriginalString;
        if (IsCurrentBookmarked)
        {
            // 找到当前 URL 对应书签并删除
            var bm = Bookmarks.FirstOrDefault(b => b.Url == url);
            if (bm is not null)
            {
                try
                {
                    await _client.DeleteBookmarkAsync(bm.Id);
                    Bookmarks.Remove(bm);
                    IsCurrentBookmarked = false;
                    StatusText = $"已删除书签：{url}";
                }
                catch (Exception ex) { StatusText = $"删除书签失败：{ex.Message}"; }
            }
        }
        else
        {
            var title = url;
            try
            {
                var dto = await _client.AddBookmarkAsync(title, url);
                Bookmarks.Add(dto);
                IsCurrentBookmarked = true;
                StatusText = $"已添加书签：{title}";
            }
            catch (Exception ex) { StatusText = $"添加书签失败：{ex.Message}"; }
        }
    }

    [RelayCommand]
    private async Task OpenBookmarkAsync(BookmarkDto? bookmark)
    {
        if (bookmark is null) return;
        Navigate(bookmark.Url);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteBookmarkAsync(BookmarkDto? bookmark)
    {
        if (bookmark is null) return;
        try
        {
            await _client.DeleteBookmarkAsync(bookmark.Id);
            Bookmarks.Remove(bookmark);
            if (_currentUri is not null && bookmark.Url == (_currentUri.IsAbsoluteUri ? _currentUri.ToString() : _currentUri.OriginalString))
                IsCurrentBookmarked = false;
            StatusText = $"已删除书签：{bookmark.Title}";
        }
        catch (Exception ex) { StatusText = $"删除书签失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task ClearBookmarksAsync()
    {
        try
        {
            await _client.ClearBookmarksAsync();
            Bookmarks.Clear();
            IsCurrentBookmarked = false;
            StatusText = "已清空全部书签";
        }
        catch (Exception ex) { StatusText = $"清空书签失败：{ex.Message}"; }
    }

    // ---- 历史 ----

    [RelayCommand]
    private async Task OpenHistoryAsync(HistoryEntryDto? entry)
    {
        if (entry is null) return;
        Navigate(entry.Url);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteHistoryAsync(HistoryEntryDto? entry)
    {
        if (entry is null) return;
        try
        {
            await _client.DeleteHistoryAsync(entry.Id);
            History.Remove(entry);
            StatusText = $"已删除历史记录：{entry.Title}";
        }
        catch (Exception ex) { StatusText = $"删除历史记录失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _client.ClearHistoryAsync();
            History.Clear();
            StatusText = "已清空全部历史记录";
        }
        catch (Exception ex) { StatusText = $"清空历史记录失败：{ex.Message}"; }
    }

    // ---- 侧边栏切换 ----

    [RelayCommand]
    private void SwitchToBookmarks() => ActiveSidebarTab = SidebarTab.Bookmarks;

    [RelayCommand]
    private void SwitchToHistory() => ActiveSidebarTab = SidebarTab.History;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private async Task OpenSettingsAsync()
        => await (RequestSettingsAsync?.Invoke() ?? Task.CompletedTask);

    [RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();

    [RelayCommand]
    private async Task SaveLocalPortForwardingAsync()
    {
        try
        {
            var saved = await _client.SaveSettingsAsync(new BrowserSettingsDto(IsLocalPortForwardingEnabled));
            IsLocalPortForwardingEnabled = saved.LocalPortForwardingEnabled;
            _savedLocalPortForwardingEnabled = saved.LocalPortForwardingEnabled;
            LocalPortForwardingStatus = saved.LocalPortForwardingEnabled
                ? "本地端口映射：已开启（localhost 请求将访问远程计算机）"
                : "本地端口映射：已关闭";
            StatusText = LocalPortForwardingStatus;
        }
        catch (Exception ex)
        {
            IsLocalPortForwardingEnabled = _savedLocalPortForwardingEnabled;
            LocalPortForwardingStatus = $"本地端口映射保存失败：{ex.Message}";
            StatusText = LocalPortForwardingStatus;
        }
    }

    // ---- 初始化加载 ----

    /// <summary>登录后由 BrowserApp 调用：加载书签列表 + 最近历史。</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = "正在同步书签与历史记录...";
        try
        {
            var bms = await _client.ListBookmarksAsync();
            Bookmarks.Clear();
            foreach (var b in bms) Bookmarks.Add(b);
            var hist = await _client.ListHistoryAsync(limit: 100);
            History.Clear();
            foreach (var h in hist) History.Add(h);
            var settings = await _client.GetSettingsAsync();
            IsLocalPortForwardingEnabled = settings.LocalPortForwardingEnabled;
            _savedLocalPortForwardingEnabled = settings.LocalPortForwardingEnabled;
            LocalPortForwardingStatus = settings.LocalPortForwardingEnabled
                ? "本地端口映射：已开启（localhost 请求将访问远程计算机）"
                : "本地端口映射：已关闭";
            StatusText = $"就绪 — {Bookmarks.Count} 个书签，{History.Count} 条历史";
        }
        catch (Exception ex)
        {
            StatusText = $"同步失败：{ex.Message}";
        }
        finally { IsLoading = false; }
    }

    /// <summary>记录一次访问到服务端历史（仅 fire-and-forget 调用，错误不抛出）。</summary>
    private async Task RecordVisitAsync(Uri url)
    {
        try
        {
            var urlStr = url.IsAbsoluteUri ? url.ToString() : url.OriginalString;
            var dto = await _client.RecordVisitAsync(urlStr, urlStr);
            // 更新本地列表：若已存在则替换，否则插入到顶部
            var existing = History.FirstOrDefault(h => h.Id == dto.Id);
            if (existing is not null)
            {
                var idx = History.IndexOf(existing);
                History[idx] = dto;
            }
            else
            {
                History.Insert(0, dto);
                // 上限 100 条本地缓存（避免无限增长）
                while (History.Count > 100) History.RemoveAt(History.Count - 1);
            }
        }
        catch
        {
            // 历史记录失败不阻塞浏览
        }
    }

    /// <summary>刷新当前 URL 是否已加书签（用于星标 UI）。</summary>
    private async Task RefreshBookmarkStarAsync(Uri url)
    {
        var urlStr = url.IsAbsoluteUri ? url.ToString() : url.OriginalString;
        var exists = Bookmarks.Any(b => b.Url == urlStr);
        IsCurrentBookmarked = exists;
        await Task.CompletedTask;
    }

    /// <summary>把用户输入归一为绝对 Uri。已是绝对 URL 直接用；否则尝试加 https:// 前缀；
    /// 形如 "example.com foo"（含空格）当作搜索引擎查询（用 bing）。null/空返回 null。</summary>
    private static Uri? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var trimmed = address.Trim();
        if (Uri.IsWellFormedUriString(trimmed, UriKind.Absolute))
            return new Uri(trimmed);
        // localhost:9999 is a normal browser address even without an explicit scheme.
        if (Uri.TryCreate("http://" + trimmed, UriKind.Absolute, out var loopback)
            && IsLoopbackTarget(loopback))
            return loopback;
        // 看起来像域名（无 scheme）—— 补 https://
        if (trimmed.Contains('.') && !trimmed.Contains(' '))
            return new Uri("https://" + trimmed);
        // 否则当作搜索查询
        return new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(trimmed));
    }

    private Uri ToDisplayUrl(Uri url) => _client.TryGetLocalPortForwardingTarget(url) ?? url;

    private static bool IsLoopbackTarget(Uri uri)
        => uri.IsAbsoluteUri
           && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1")
           && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}

/// <summary>侧边栏标签页枚举。</summary>
public enum SidebarTab
{
    Bookmarks,
    History,
}
