using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.Common;
using Rect = RemoteOS.Core.Primitives.Rect;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Firewall;

/// <summary>Built-in Linux Server UFW editor.</summary>
public sealed class FirewallApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.firewall"), "Firewall", "0.1.0", "🧱", "Manage Linux Server UFW firewall rules",
        [AppPermissions.ServerFirewallRead, AppPermissions.ServerFirewallManage],
        ServerRequirements: new ApplicationServerRequirements(Platforms: [ApplicationPlatformNames.Linux], Capabilities: [ServerCapabilities.Firewall]));

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteFirewallClient)) as IRemoteFirewallClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.firewall.display_name"), new TextBlock { Text = LocalizedText.Get("firewall.login_required"), Margin = new Avalonia.Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new FirewallViewModel(client, session);
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.firewall.display_name"), CreateView(viewModel), new Rect(90, 65, 980, 700), Manifest.IconGlyph);
        viewModel.RequestPasswordAsync = () => RequestPasswordAsync(context, window);
        _ = viewModel.StartAsync();
    }

    private static Control CreateView(FirewallViewModel vm)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(18), LastChildFill = true, DataContext = vm };
        var refresh = new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(refresh, Dock.Top); root.Children.Add(refresh);
        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 12), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText))); DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);

        var settings = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 12) };
        settings.Children.Add(new TextBlock { Text = LocalizedText.Get("firewall.warning"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(new Button { Content = LocalizedText.Get("firewall.enable"), Command = vm.EnableCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("firewall.disable"), Command = vm.DisableCommand });
        actions.Children.Add(Field(vm, nameof(vm.IncomingPolicy), "firewall.default_incoming", 120));
        actions.Children.Add(Field(vm, nameof(vm.OutgoingPolicy), "firewall.default_outgoing", 120));
        actions.Children.Add(new Button { Content = LocalizedText.Get("firewall.save_defaults"), Command = vm.SaveDefaultsCommand });
        settings.Children.Add(actions);
        DockPanel.SetDock(settings, Dock.Top); root.Children.Add(settings);

        var rules = new DockPanel();
        var editor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        editor.Children.Add(Field(vm, nameof(vm.Action), "firewall.rule.action", 90));
        editor.Children.Add(Field(vm, nameof(vm.Direction), "firewall.rule.direction", 70));
        editor.Children.Add(Field(vm, nameof(vm.Protocol), "firewall.rule.protocol", 70));
        editor.Children.Add(Field(vm, nameof(vm.Source), "firewall.rule.source", 140));
        editor.Children.Add(Field(vm, nameof(vm.Destination), "firewall.rule.destination", 140));
        editor.Children.Add(Field(vm, nameof(vm.Port), "firewall.rule.port", 90));
        editor.Children.Add(new Button { Content = LocalizedText.Get("firewall.rule.add"), Command = vm.AddRuleCommand });
        DockPanel.SetDock(editor, Dock.Top); rules.Children.Add(editor);
        var list = new ListBox { DisplayMemberBinding = new Avalonia.Data.Binding("Port") };
        list.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(vm.Rules)));
        list.Bind(SelectingItemsControl.SelectedItemProperty, new Avalonia.Data.Binding(nameof(vm.SelectedRule)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        rules.Children.Add(list);
        var delete = new Button { Content = LocalizedText.Get("firewall.rule.delete"), Command = vm.DeleteRuleCommand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        delete.Bind(Button.CommandParameterProperty, new Avalonia.Data.Binding(nameof(vm.SelectedRule)));
        DockPanel.SetDock(delete, Dock.Bottom); rules.Children.Add(delete);
        root.Children.Add(rules);
        return root;
    }

    private static TextBox Field(FirewallViewModel vm, string property, string placeholder, double width) => new()
    {
        Width = width, PlaceholderText = LocalizedText.Get(placeholder),
        [!TextBox.TextProperty] = new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay },
    };

    private static Task<string?> RequestPasswordAsync(AppContext context, RemoteOS.WindowManager.ManagedWindow owner) =>
        context.ShowDialogAsync<string?>(owner, LocalizedText.Get("firewall.password_dialog.title"), dialog =>
        {
            var password = new TextBox { PasswordChar = '•', PlaceholderText = LocalizedText.Get("firewall.password_placeholder") };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
            cancel.Click += (_, _) => dialog.Cancel();
            var confirm = new Button { Content = LocalizedText.Get("common.ok"), Classes = { "primary" } };
            confirm.Click += (_, _) => dialog.Close(password.Text);
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            return new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = LocalizedText.Get("firewall.password_dialog.message"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    password,
                    actions,
                },
            };
        }, new RemoteOS.Core.Primitives.Size(420, 180));
}
