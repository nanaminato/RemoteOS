using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Client.Localization;
using Client.Apps.TaskManager.ViewModels;
using Client.Apps.TaskManager.Views;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;
using Rect = RemoteOS.Core.Primitives.Rect;

namespace Client.Apps.TaskManager;

/// <summary>Built-in RemoteTaskManager — 远端宿主 OS 任务管理器。
/// 参考 Windows 任务管理器 / Linux 系统监视器：性能标签页（CPU/GPU/内存/磁盘/网络实时占用 + 柱状图）+
/// 进程标签页（当前可见进程列表，可结束任务，权限不足提示需在宿主 OS 提权）。
/// 数据经 <see cref="ITaskManagerClient"/> 调用 Server REST API（JWT via <see cref="IAuthSession"/>）；
/// 服务端以宿主 OS 进程身份采集（复用宿主用户/权限，不另建 ACL）。未登录时弹提示窗。</summary>
public sealed class TaskManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.taskmanager"),
        DisplayName: "任务管理器",
        Version: "1.0.0",
        IconGlyph: "📊",
        Description: "查看 CPU/内存/磁盘/网络/GPU 占用与进程，可结束任务");

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(ITaskManagerClient)) as ITaskManagerClient;

        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            var stub = new TextBlock
            {
                Text = LocalizedText.Get("task_manager.login_required"),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
            context.ShowWindow(LocalizedText.Get("application.remoteos.taskmanager.display_name"), stub,
                bounds: new Rect(200, 160, 460, 180),
                iconGlyph: Manifest.IconGlyph,
                canResize: false, canMinimize: false, canMaximize: false);
            return;
        }

        var viewModel = new TaskManagerViewModel(client);
        var view = new TaskManagerMainView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.taskmanager.display_name"), view,
            bounds: new Rect(70, 55, 980, 680),
            iconGlyph: Manifest.IconGlyph);
        viewModel.CloseAction = () => Dispatcher.UIThread.Post(() => context.WindowManager.Close(window));

        // 窗口打开后启动实时刷新
        _ = viewModel.StartAsync();
    }
}
