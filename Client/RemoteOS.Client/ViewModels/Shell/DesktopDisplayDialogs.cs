using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;

namespace Client.ViewModels.Shell;

/// <summary>桌面显示配置：对话框 UI 构造器。
/// 采用「传入 close 回调 → 返回 Control」的方式，兼容 Shell 的 WindowManager 与 AppContext.ShowDialogAsync 两种调用路径。</summary>
public static class DesktopDisplayDialogs
{
    /// <summary>构造「配置桌面显示项目」对话框内容。</summary>
    /// <param name="settings">活状态 ShellSettings（读取初始值；点击确认后写回）。</param>
    /// <param name="applications">应用管理器，用于枚举已注册的内置应用。</param>
    /// <param name="saveAsync">写回持久化到服务端的回调（确认后调用）。</param>
    /// <param name="close">关闭对话框回调，参数 = true 表示保存/完成，false 表示取消/跳过。</param>
    /// <param name="isFirstTime">true = 首次引导模式（跳过按钮 + 欢迎文案）。</param>
    public static Control BuildSettingsDialog(
        ShellSettings settings,
        ApplicationManager applications,
        Func<Task> saveAsync,
        Action<bool> close,
        bool isFirstTime)
    {
        var buffer = new DesktopDisplayEditBuffer(settings, applications);

        var root = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
        };

        if (isFirstTime)
        {
            root.Children.Add(new TextBlock
            {
                Text = "欢迎使用 RemoteOS！",
                FontSize = 20,
                FontWeight = FontWeight.SemiBold,
            });
            root.Children.Add(new TextBlock
            {
                Text = "先花几秒配置一下桌面上显示哪些内容。之后随时可以在桌面右键菜单或使用 Ctrl+Shift+D 快捷键重新配置。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Opacity = 0.85,
            });
            root.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 0) });
        }
        else
        {
            root.Children.Add(new TextBlock
            {
                Text = "选择桌面上要显示的项目。配置会保存在你的 Workspace 中并在多设备间同步。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Opacity = 0.85,
            });
            root.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 0) });
        }

        // ── 区块 1：内置应用程序显示 ──
        root.Children.Add(BuildSectionTitle("内置应用程序"));

        var showAppsToggle = new CheckBox
        {
            Content = "在桌面上显示内置应用程序",
            IsChecked = buffer.ShowBuiltInApps,
            Padding = new Thickness(0, 2),
        };

        var appsContainer = new StackPanel { Spacing = 8, Margin = new Thickness(20, 4, 0, 0) };

        var allAppsRadio = new RadioButton
        {
            Content = "显示全部内置应用（默认）",
            IsChecked = buffer.ShowAllBuiltInApps,
            IsEnabled = buffer.ShowBuiltInApps,
        };
        var customAppsRadio = new RadioButton
        {
            Content = "仅显示选定的应用：",
            IsChecked = !buffer.ShowAllBuiltInApps,
            IsEnabled = buffer.ShowBuiltInApps,
        };

        var appListBox = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MaxHeight = 160,
            ItemsSource = buffer.AppItems,
            IsEnabled = buffer.ShowBuiltInApps && !buffer.ShowAllBuiltInApps,
            SelectionMode = SelectionMode.Multiple,
        };
        if (appListBox.SelectedItems is { } selectedItems)
            foreach (var item in buffer.AppItems.Where(i => i.IsVisible))
                selectedItems.Add(item);

        showAppsToggle.IsCheckedChanged += (_, _) =>
        {
            buffer.ShowBuiltInApps = showAppsToggle.IsChecked == true;
            allAppsRadio.IsEnabled = buffer.ShowBuiltInApps;
            customAppsRadio.IsEnabled = buffer.ShowBuiltInApps;
            UpdateAppListBoxEnabled();
        };
        allAppsRadio.IsCheckedChanged += (_, _) =>
        {
            if (allAppsRadio.IsChecked == true)
            {
                buffer.ShowAllBuiltInApps = true;
                UpdateAppListBoxEnabled();
            }
        };
        customAppsRadio.IsCheckedChanged += (_, _) =>
        {
            if (customAppsRadio.IsChecked == true)
            {
                buffer.ShowAllBuiltInApps = false;
                UpdateAppListBoxEnabled();
            }
        };
        appListBox.SelectionChanged += (_, _) =>
        {
            if (appListBox.SelectedItems is { } selectedItems)
                buffer.SyncVisibleFromSelection(selectedItems.Cast<AppVisibilityItem>());
        };
        void UpdateAppListBoxEnabled()
        {
            appListBox.IsEnabled = buffer.ShowBuiltInApps && !buffer.ShowAllBuiltInApps;
            if (!appListBox.IsEnabled) return;
            if (appListBox.SelectedItems is not { } selectedItems) return;
            selectedItems.Clear();
            foreach (var item in buffer.AppItems.Where(i => i.IsVisible))
                selectedItems.Add(item);
        }

        appsContainer.Children.Add(allAppsRadio);
        appsContainer.Children.Add(customAppsRadio);
        appsContainer.Children.Add(appListBox);

        root.Children.Add(showAppsToggle);
        root.Children.Add(appsContainer);

        root.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 0) });

        // ── 区块 2：服务器桌面文件 ──
        root.Children.Add(BuildSectionTitle("服务器桌面文件"));

        var showFilesToggle = new CheckBox
        {
            Content = "显示服务器桌面的一般文件（文件夹 / 文档 / 图片等）",
            IsChecked = buffer.ShowServerDesktopFiles,
            Padding = new Thickness(0, 2),
        };
        var showShortcutsToggle = new CheckBox
        {
            Content = "显示服务器桌面快捷方式（.lnk / .desktop）",
            IsChecked = buffer.ShowServerDesktopShortcuts,
            Padding = new Thickness(20, 2, 0, 2),
            Opacity = 0.92,
        };
        ToolTip.SetTip(showShortcutsToggle, "快捷方式通常指向宿主机本地路径，在 RemoteOS 中可能无法直接打开。");

        root.Children.Add(showFilesToggle);
        root.Children.Add(showShortcutsToggle);

        // ── 底部按钮 ──
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Spacing = 8,
        };

        var cancelOrSkipBtn = new Button
        {
            Content = isFirstTime ? "跳过" : "取消",
            Padding = new Thickness(16, 6),
        };
        cancelOrSkipBtn.Click += (_, _) => close(false);

        var saveBtn = new Button
        {
            Content = isFirstTime ? "开始使用" : "保存",
            Background = Brush.Parse("#1C3765"),
            Foreground = Brushes.White,
            Padding = new Thickness(16, 6),
        };
        saveBtn.Click += async (_, _) =>
        {
            buffer.ApplyTo(settings);
            await saveAsync();
            close(true);
        };

        buttonPanel.Children.Add(cancelOrSkipBtn);
        buttonPanel.Children.Add(saveBtn);

        root.Children.Add(buttonPanel);
        return root;
    }

    private static Control BuildSectionTitle(string text) => new TextBlock
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 4, 0, 0),
    };
}

/// <summary>编辑缓冲区：对话框工作期间持有一份副本，取消时不污染 ShellSettings。</summary>
internal sealed class DesktopDisplayEditBuffer
{
    public bool ShowBuiltInApps { get; set; }
    public bool ShowServerDesktopFiles { get; set; }
    public bool ShowServerDesktopShortcuts { get; set; }

    /// <summary>VisibleAppIds 为空 = true 代表"显示全部"。</summary>
    public bool ShowAllBuiltInApps { get; set; }

    public List<AppVisibilityItem> AppItems { get; } = [];

    public DesktopDisplayEditBuffer(ShellSettings settings, ApplicationManager applications)
    {
        ShowBuiltInApps = settings.ShowBuiltInApps;
        ShowServerDesktopFiles = settings.ShowServerDesktopFiles;
        ShowServerDesktopShortcuts = settings.ShowServerDesktopShortcuts;

        var visibleAppIds = new HashSet<string>(settings.VisibleAppIds, StringComparer.Ordinal);
        ShowAllBuiltInApps = visibleAppIds.Count == 0;

        var compatible = applications.Registered
            .Where(app => applications.GetManifest(app.Id) is { } manifest
                          && applications.EvaluateCompatibility(manifest).IsCompatible)
            .ToList();

        foreach (var app in compatible)
        {
            AppItems.Add(new AppVisibilityItem(app)
            {
                IsVisible = ShowAllBuiltInApps || visibleAppIds.Contains(app.Id.Value),
            });
        }
    }

    public void SyncVisibleFromSelection(IEnumerable<AppVisibilityItem> selected)
    {
        var selectedSet = new HashSet<string>(selected.Select(x => x.App.Id.Value), StringComparer.Ordinal);
        foreach (var item in AppItems)
            item.IsVisible = selectedSet.Contains(item.App.Id.Value);
    }

    public void ApplyTo(ShellSettings settings)
    {
        settings.ShowBuiltInApps = ShowBuiltInApps;
        settings.ShowServerDesktopFiles = ShowServerDesktopFiles;
        settings.ShowServerDesktopShortcuts = ShowServerDesktopShortcuts;

        if (!ShowBuiltInApps || ShowAllBuiltInApps)
        {
            settings.VisibleAppIds = new List<string>();
        }
        else
        {
            settings.VisibleAppIds = AppItems
                .Where(x => x.IsVisible)
                .Select(x => x.App.Id.Value)
                .ToList();
        }
    }
}

/// <summary>应用可见性勾选项（ListBox 行）。</summary>
internal sealed partial class AppVisibilityItem : ObservableObject
{
    public ApplicationInfo App { get; }
    public string DisplayName => App.DisplayName;
    public string IconGlyph => string.IsNullOrWhiteSpace(App.IconGlyph) ? "📦" : App.IconGlyph;

    [ObservableProperty] private bool _isVisible;

    public AppVisibilityItem(ApplicationInfo app)
    {
        App = app;
    }

    public override string ToString() => $"{IconGlyph}  {DisplayName}";
}
