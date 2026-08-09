using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.ComponentModel;
using Client.Apps.Browser.ViewModels;
using RemoteOS.Protocol.Browser;

namespace Client.Apps.Browser.Views;

/// <summary>BrowserMainView 的 code-behind。负责把 NativeWebView 的导航事件桥接到 VM，
/// 以及处理侧边栏列表的双击导航 / 单条删除按钮（DTO 经 DataContext 传递）。
///
/// 注意：NativeWebView.NavigationStarted/NavigationCompleted 事件参数类型在
/// Avalonia.Controls.WebView 12.0.1 中为 WebViewNavigationStartingEventArgs 与
/// WebViewNavigationCompletedEventArgs，二者均含 Uri 属性；Completed 含 IsSuccess。
/// 因事件委托签名为 EventHandler&lt;TArgs&gt;，handler 参数类型直接用具体 EventArgs 类型。</summary>
public partial class BrowserMainView : UserControl
{
    private const double DefaultSidebarWidth = 260;
    private const double SidebarSplitterWidth = 4;

    private BrowserViewModel? _observedViewModel;
    private double _sidebarWidth = DefaultSidebarWidth;
    private bool _settingsButtonAdded;

    private ColumnDefinition SidebarColumn => BrowserContentGrid.ColumnDefinitions[0];
    private ColumnDefinition SidebarSplitterColumn => BrowserContentGrid.ColumnDefinitions[1];

    public BrowserMainView()
    {
        InitializeComponent();
        WebView.EnvironmentRequested += ConfigureWebViewEnvironment;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void ConfigureWebViewEnvironment(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Avalonia 12 exposes this switch on its Linux-specific event args, while the
        // public event uses the platform-neutral base type. Use the runtime type here so
        // non-Linux builds do not need a reference to the internal backend type.
        try { ((dynamic)e).PreferWebKitGtkInstead = true; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
    }

    private BrowserViewModel? ViewModel => DataContext as BrowserViewModel;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        WireWebViewCommands();
        ObserveViewModel();
        MoveBrowserSettingsToDialog();
        // 让 WebView 获得键盘焦点以便直接交互
        WebView.Focus();
    }

    private void MoveBrowserSettingsToDialog()
    {
        if (_settingsButtonAdded || Content is not DockPanel root)
            return;

        var toolbar = root.Children.OfType<Border>().FirstOrDefault()?.Child as StackPanel;
        if (toolbar is null)
            return;

        var forwardingToggle = toolbar.Children.OfType<CheckBox>().FirstOrDefault();
        if (forwardingToggle is not null)
            forwardingToggle.IsVisible = false;

        toolbar.Children.Add(new Button
        {
            Content = "Full screen",
            Command = ViewModel?.ToggleFullScreenCommand,
        });
        toolbar.Children.Add(new Button
        {
            Content = "Settings",
            Command = ViewModel?.OpenSettingsCommand,
        });
        _settingsButtonAdded = true;
    }

    /// <summary>把 VM 的 GoBack/GoForward/Refresh/Stop 命令接到 NativeWebView 的实际方法。</summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_observedViewModel is null)
            return;

        _observedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _observedViewModel = null;
    }

    /// <summary>
    /// NativeWebView is backed by a platform child view. Keep it hidden while this managed
    /// window is inactive so it cannot render above another Avalonia-managed window.
    /// </summary>
    public void SetWebViewVisible(bool isVisible) => WebView.IsVisible = isVisible;

    private void ObserveViewModel()
    {
        var viewModel = ViewModel;
        if (ReferenceEquals(_observedViewModel, viewModel))
            return;

        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _observedViewModel = viewModel;
        if (viewModel is null)
            return;

        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateSidebarLayout(viewModel.IsSidebarVisible);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserViewModel.IsSidebarVisible) && sender is BrowserViewModel viewModel)
            UpdateSidebarLayout(viewModel.IsSidebarVisible);
    }

    private void UpdateSidebarLayout(bool isVisible)
    {
        if (isVisible)
        {
            SidebarColumn.Width = new GridLength(_sidebarWidth, GridUnitType.Pixel);
            SidebarSplitterColumn.Width = new GridLength(SidebarSplitterWidth, GridUnitType.Pixel);
            return;
        }

        if (SidebarColumn.ActualWidth > 0)
            _sidebarWidth = SidebarColumn.ActualWidth;

        SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
        SidebarSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
    }

    private void WireWebViewCommands()
    {
        if (ViewModel is null) return;
        ViewModel.ViewGoBackRequested = () => WebView.GoBack();
        ViewModel.ViewGoForwardRequested = () => WebView.GoForward();
        ViewModel.ViewRefreshRequested = () => WebView.Refresh();
        ViewModel.ViewStopRequested = () => WebView.Stop();
        // 初始状态同步
        ViewModel.UpdateNavigationState(WebView.CanGoBack, WebView.CanGoForward);
    }

    // ---- 地址栏 ----

    private void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            ViewModel?.NavigateCommand.Execute(tb.Text);
            e.Handled = true;
        }
    }

    private void GoButton_Click(object? sender, RoutedEventArgs e)
        => ViewModel?.NavigateCommand.Execute(AddressBox.Text);

    // ---- 侧边栏列表 ----

    private void BookmarksList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (BookmarksList.SelectedItem is BookmarkDto bm)
            ViewModel?.NavigateCommand.Execute(bm.Url);
    }

    private void HistoryList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntryDto h)
            ViewModel?.NavigateCommand.Execute(h.Url);
    }

    /// <summary>侧边栏每行的"✕"删除按钮。Tag 区分 bookmark / history，DataContext 是该行 DTO。</summary>
    private void DeleteItemButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || ViewModel is null) return;
        switch (btn.Tag as string)
        {
            case "bookmark" when btn.DataContext is BookmarkDto bm:
                _ = ViewModel.DeleteBookmarkCommand.ExecuteAsync(bm);
                break;
            case "history" when btn.DataContext is HistoryEntryDto h:
                _ = ViewModel.DeleteHistoryCommand.ExecuteAsync(h);
                break;
        }
    }

    // ---- WebView 事件桥接 ----
    // NativeWebView.NavigationStarted → VM.OnNavigationStarted（更新地址栏 + 加载状态 + 历史）
    // NativeWebView.NavigationCompleted → VM.OnNavigationCompleted（停止指示 + 记录历史）

    private void WebView_NavigationStarted(object? sender, Avalonia.Controls.WebViewNavigationStartingEventArgs e)
    {
        if (ViewModel is null) return;
        var url = WebView.Source;
        if (url is not null)
        {
            ViewModel.OnNavigationStarted(url);
        }
        ViewModel.UpdateNavigationState(WebView.CanGoBack, WebView.CanGoForward);
    }

    private void WebView_NavigationCompleted(object? sender, Avalonia.Controls.WebViewNavigationCompletedEventArgs e)
    {
        if (ViewModel is null) return;
        var url = WebView.Source;
        // 完成事件无统一 IsSuccess 字段（平台差异），按成功处理；失败时 WebView 自身会显示错误页
        ViewModel.OnNavigationCompleted(url, isSuccess: true);
        ViewModel.UpdateNavigationState(WebView.CanGoBack, WebView.CanGoForward);
    }
}
