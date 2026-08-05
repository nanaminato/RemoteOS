using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Client.Apps.Browser.ViewModels;
using Client.Apps.Browser.Views;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;
using Rect = RemoteOS.Core.Primitives.Rect;

namespace Client.Apps.Browser;

/// <summary>Built-in RemoteBrowser — 平台原生 WebView 嵌入式浏览器。
/// UI 在 Client 本地渲染（NativeWebView 用平台原生引擎：Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），
/// 网页内容走客户端网络；Server 仅持久化书签与历史记录（按用户隔离，JWT via IAuthSession）。
/// 未登录时弹提示窗。</summary>
public sealed class BrowserApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("remoteos.browser"),
        DisplayName: "RemoteBrowser",
        Version: "1.0.0",
        IconGlyph: "🌐",
        Description: "网页浏览器（书签 / 历史记录持久化到服务端）");

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IBrowserClient)) as IBrowserClient;

        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            var stub = new TextBlock
            {
                Text = "RemoteBrowser 需要先登录。\n请连接到 RemoteOS Server 后再启动此应用。",
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
            context.ShowWindow("RemoteBrowser", stub,
                bounds: new Rect(200, 160, 460, 180),
                iconGlyph: Manifest.IconGlyph,
                canResize: false, canMinimize: false, canMaximize: false);
            return;
        }

        var viewModel = new BrowserViewModel(client);
        var view = new BrowserMainView { DataContext = viewModel };
        var window = context.ShowWindow("RemoteBrowser", view,
            bounds: new Rect(60, 50, 1100, 720),
            iconGlyph: Manifest.IconGlyph);
        viewModel.CloseAction = () => Dispatcher.UIThread.Post(() => context.WindowManager.Close(window));

        // 窗口打开后异步加载书签 + 历史
        _ = viewModel.LoadAsync();
    }
}
