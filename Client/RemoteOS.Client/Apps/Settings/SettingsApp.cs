using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Client.Apps.Settings.ViewModels;
using Client.Apps.Settings.Views;
using Client.Apps.Explorer.Dialogs;
using Client.Services;
using Client.Services.Auth;
using Client.Services.AppPermissions;
using Client.Services.Developer;
using Client.Services.Diagnostics;
using Client.Apps.TaskManager;
using Client.Apps.Browser;
using Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;
using AvaloniaApplication = Avalonia.Application;

namespace Client.Apps.Settings;

/// <summary>Built-in Settings application — Windows 11 / GNOME 风格的设置中心。
/// 5 个分类：系统 / 个性化 / 时间和语言 / 网络 / 应用（含默认程序）。用户偏好（壁纸/主题/时间格式/语言/区域/默认程序）
/// 持久化到服务端 Workspace（<c>/workspaces/{id}/preferences</c>），多设备登录同一 Workspace 共享。
/// 未登录时仍可打开（仅本地 ShellSettings，不持久化）。</summary>
public sealed class SettingsApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.settings"),
        DisplayName: "Settings",
        Version: "1.0.0",
        IconGlyph: "⚙️",
        Description: "个性化与系统设置");

    public override void Activate(AppContext context)
    {
        var settings = context.Services.GetRequiredService<ShellSettings>();
        var session = context.Services.GetRequiredService<IAuthSession>();
        var settingsClient = context.Services.GetRequiredService<ISettingsClient>();
        var apps = context.Services.GetRequiredService<ApplicationManager>();
        var remote = context.Services.GetRequiredService<IRemoteOsClient>();
        var system = context.Services.GetRequiredService<ITaskManagerClient>();
        var registry = context.Services.GetRequiredService<DefaultAppRegistry>();
        var permissions = context.Services.GetRequiredService<IAppPermissionManager>();
        var localization = context.Services.GetRequiredService<LocalizationService>();
        var developerMode = context.Services.GetRequiredService<DeveloperModeService>();
        var packages = context.Services.GetRequiredService<DeveloperPackageManager>();
        var networkInspector = context.Services.GetRequiredService<NetworkInspectorWindowService>();
        var settingsNavigation = context.Services.GetRequiredService<ISettingsNavigation>();
        var wallpapers = context.Services.GetRequiredService<WallpaperService>();
        var browserClient = context.Services.GetRequiredService<IBrowserClient>();

        var viewModel = new SettingsViewModel(settings, settingsClient, session, apps, remote, system, registry, developerMode, packages,
            browserClient, networkInspector, wallpapers: wallpapers);
        var view = new SettingsView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("settings.title"), view,
            bounds: new Rect(180, 90, 820, 560),
            iconGlyph: Manifest.IconGlyph);
        if (settingsNavigation is SettingsNavigationService navigation)
            navigation.Register(window, viewModel);

        var appsPage = viewModel.Pages.OfType<AppsPageViewModel>().Single();
        var personalizationPage = viewModel.Pages.OfType<PersonalizationPageViewModel>().Single();
        personalizationPage.RequestCustomWallpaperAsync = async () =>
        {
            var topLevel = AvaloniaApplication.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            if (topLevel is null) return;
            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("settings.wallpaper.choose_image"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(LocalizedText.Get("settings.wallpaper"))
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif"],
                    },
                ],
            });
            var file = selected.FirstOrDefault();
            if (file is null) return;
            await using var stream = await file.OpenReadAsync();
            await wallpapers.UploadAndApplyAsync(stream, file.Name);
        };
        appsPage.RequestPermissionEditorAsync = async app =>
        {
            AppPermissionDialogViewModel? dialogViewModel = null;
            await context.ShowDialogAsync<bool>(
                window,
                LocalizedText.Format("settings.apps.permissions_title", app.DisplayName),
                dialog => new AppPermissionDialogView
                {
                    DataContext = dialogViewModel = new AppPermissionDialogViewModel(app, permissions, localization, dialog.Close),
                },
                new Size(560, 540));
            dialogViewModel?.Dispose();
        };
        appsPage.RequestUninstallConfirmationAsync = async app =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Format("settings.apps.uninstall_title", app.DisplayName), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(
                    LocalizedText.Format("settings.apps.uninstall_confirmation", app.DisplayName),
                    result => { confirmed = result; dialog.Close(result); },
                    LocalizedText.Get("settings.uninstall")),
            });
            return confirmed;
        };

        EventHandler<ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            if (settingsNavigation is SettingsNavigationService navigation)
                navigation.Unregister(window);
            viewModel.Dispose();
        };
        context.WindowManager.WindowClosed += closed;

        // 窗口打开后异步加载服务端偏好。
        _ = viewModel.InitializeAsync();
    }
}
