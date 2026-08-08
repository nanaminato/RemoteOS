using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Client.Apps.Explorer.Dialogs;
using Client.Apps.Explorer.ViewModels;
using Client.Apps.Explorer.Views;
using Client.Apps.Settings;
using Client.Services;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.Files;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;
using Rect = RemoteOS.Core.Primitives.Rect;

namespace Client.Apps.Explorer;

/// <summary>Built-in RemoteExplorer — file manager for the remote host OS.
/// UI 结构移植自 Jaya File Manager (BSD-3)：导航树 + Explorer 网格 + 地址栏 + 工具栏 + 状态栏。
/// 所有文件操作经 <see cref="IExplorerClient"/> 调用 Server 端 REST API（JWT via <see cref="IAuthSession"/>）。
/// 未登录时弹提示窗；服务端以宿主 OS 进程身份执行 IO，复用宿主用户/权限（不另建 ACL）。</summary>
public sealed class ExplorerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.explorer"),
        DisplayName: "RemoteExplorer",
        Version: "1.0.0",
        IconGlyph: "📁",
        Description: "远端文件管理器");

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;

        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            var stub = new TextBlock
            {
                Text = "RemoteExplorer 需要先登录。\n请连接到 RemoteOS Server 后再启动此应用。",
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
            context.ShowWindow("RemoteExplorer", stub,
                bounds: new Rect(200, 160, 460, 180),
                iconGlyph: Manifest.IconGlyph,
                canResize: false, canMinimize: false, canMaximize: false);
            return;
        }

        var viewModel = new ExplorerViewModel(client);
        WireDialogs(context, viewModel, client);
        var view = new ExplorerMainView { DataContext = viewModel };
        var window = context.ShowWindow("RemoteExplorer", view,
            bounds: new Rect(80, 60, 960, 640),
            iconGlyph: Manifest.IconGlyph);
        viewModel.CloseAction = () => Dispatcher.UIThread.Post(() => context.WindowManager.Close(window));

        // 窗口打开后异步加载根
        _ = viewModel.LoadRootAsync();
    }

    /// <summary>将对话框回调注入 VM：文本输入 / 确认 / 本地文件选择（上传/下载） / 消息 / 关闭。</summary>
    private static void WireDialogs(AppContext context, ExplorerViewModel vm, IExplorerClient client)
    {
        vm.RequestTextInputAsync = async (title, prompt, defaultValue, confirmLabel) =>
        {
            string? result = null;
            var tcs = new TaskCompletionSource<string?>();
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = FindOwnerWindow(context, vm);
                if (owner is null) { tcs.TrySetResult(null); return; }
                await context.ShowDialogAsync<string?>(owner, title, dialog =>
                {
                    var dvm = new TextInputDialogViewModel(prompt, defaultValue, r =>
                    {
                        result = r;
                        dialog.Close(r);
                    }, confirmLabel);
                    return new TextInputDialogView { DataContext = dvm };
                });
                tcs.TrySetResult(result);
            });
            return await tcs.Task;
        };

        vm.RequestConfirmAsync = async (title, message, confirmLabel) =>
        {
            var result = false;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = FindOwnerWindow(context, vm);
                if (owner is null) return;
                await context.ShowDialogAsync<bool?>(owner, title, dialog =>
                {
                    var dvm = new ConfirmDialogViewModel(message, r =>
                    {
                        result = r;
                        dialog.Close(r);
                    }, confirmLabel);
                    return new ConfirmDialogView { DataContext = dvm };
                });
            });
            return result;
        };

        vm.ShowMessageAsync = async (title, message) =>
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var owner = FindOwnerWindow(context, vm);
                if (owner is null) return;
                await context.ShowDialogAsync<bool?>(owner, title, dialog =>
                {
                    var dvm = new ConfirmDialogViewModel(message, _ => dialog.Close(true), "知道了");
                    return new ConfirmDialogView { DataContext = dvm };
                });
            });
        };

        vm.RequestLocalOpenFileAsync = async () =>
        {
            var topLevel = GetTopLevel(context, vm);
            if (topLevel is null) return null;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要上传的本地文件",
                AllowMultiple = false,
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };

        vm.RequestLocalSaveFileAsync = async defaultName =>
        {
            var topLevel = GetTopLevel(context, vm);
            if (topLevel is null) return null;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存下载文件到...",
                SuggestedFileName = defaultName,
            });
            return file?.TryGetLocalPath();
        };

        var applications = context.Services.GetService(typeof(ApplicationManager)) as ApplicationManager;
        var defaults = context.Services.GetService(typeof(DefaultAppRegistry)) as DefaultAppRegistry;
        vm.OpenFileAsync = async entry =>
        {
            var extension = Path.GetExtension(entry.Name);
            var defaultApplicationId = defaults?.Resolve(extension);
            var applicationId = defaultApplicationId is not null && applications?.SupportsFile(new AppId(defaultApplicationId), entry.Path) == true
                ? defaultApplicationId
                : applications?.FileOpenersForExtension(extension).FirstOrDefault()?.Id.Value;
            if (applicationId is null || applications?.OpenFile(new AppId(applicationId), entry.Path) != true)
                await (vm.ShowMessageAsync?.Invoke("Open file", "No application is registered for this file type.") ?? Task.CompletedTask);
        };

        vm.RequestOpenWithAsync = async entry =>
        {
            var owner = FindOwnerWindow(context, vm);
            var extension = Path.GetExtension(entry.Name);
            var openers = applications?.FileOpenersForExtension(extension) ?? Array.Empty<ApplicationInfo>();
            if (owner is null || openers.Count == 0)
            {
                await (vm.ShowMessageAsync?.Invoke("Open with", "No installed application declares support for this file type.") ?? Task.CompletedTask);
                return;
            }
            var choice = await context.ShowDialogAsync<OpenWithChoice>(owner, "Open with", dialog =>
                new OpenWithDialogView
                {
                    DataContext = new OpenWithDialogViewModel(openers, extension, result =>
                    {
                        if (result is null) dialog.Cancel();
                        else dialog.Close(result);
                    }),
                });
            if (choice is null) return;

            if (choice.SetAsDefault && !string.IsNullOrWhiteSpace(extension))
                await SaveDefaultAppAsync(context, defaults, extension, choice.ApplicationId);

            if (!applications!.OpenFile(new AppId(choice.ApplicationId), entry.Path))
                await (vm.ShowMessageAsync?.Invoke("Open file", "The selected application is no longer available.") ?? Task.CompletedTask);
        };

        vm.ShowPropertiesAsync = async properties =>
        {
            var owner = FindOwnerWindow(context, vm);
            if (owner is null) return;
            await context.ShowDialogAsync<bool>(owner, "Properties", dialog => new FilePropertiesDialogView
            {
                DataContext = new FilePropertiesDialogViewModel(
                    properties,
                    unixMode => client.SetUnixPermissionsAsync(properties.Path, unixMode),
                    () => dialog.Close(true)),
            });
        };
    }

    private static async Task SaveDefaultAppAsync(AppContext context, DefaultAppRegistry? defaults, string extension, string applicationId)
    {
        if (defaults is null) return;
        var mappings = defaults.Snapshot.Where(m => !m.Scheme.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Append(new DefaultAppMappingDto(extension, applicationId)).ToArray();
        defaults.SetMappings(mappings);

        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var settings = context.Services.GetService(typeof(ShellSettings)) as ShellSettings;
        var settingsClient = context.Services.GetService(typeof(ISettingsClient)) as ISettingsClient;
        if (session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace }
            || settings is null || settingsClient is null)
            return;

        await settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id, settings.ToPreferences(mappings));
    }

    /// <summary>查找 VM 对应的 ManagedWindow（遍历 WindowManager 已创建窗口，匹配 DataContext）。</summary>
    private static ManagedWindow? FindOwnerWindow(AppContext context, ExplorerViewModel vm)
    {
        // ExplorerMainView 的 DataContext == vm；ManagedWindow.View 是 RemoteWindow，其 Content 即此 view
        var wm = context.WindowManager as WindowManager;
        if (wm is null) return null;
        foreach (var w in wm.Windows)
        {
            if (w.View.Content is ExplorerMainView v && ReferenceEquals(v.DataContext, vm))
                return w;
        }
        return null;
    }

    /// <summary>获取宿主 Avalonia TopLevel（MainWindow）用于 StorageProvider 文件选择器。
    /// ManagedWindow/RemoteWindow 不是 TopLevel（它们是桌面外壳 Canvas 内的 TemplatedControl），
    /// 故取应用主窗口作为 StorageProvider 根。</summary>
    private static TopLevel? GetTopLevel(AppContext context, ExplorerViewModel vm)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
