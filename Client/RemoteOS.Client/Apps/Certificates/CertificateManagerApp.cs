using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Protocol.Certificates;
using Rect = RemoteOS.Core.Primitives.Rect;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Certificates;

/// <summary>Built-in TLS certificate manager. Host-global ACME issuance and Kestrel deployment.</summary>
public sealed class CertificateManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.certificates"), "Certificate Manager", "0.1.0", "🔐", "Manage TLS certificates on the RemoteOS Server",
        [AppPermissions.ServerCertificatesRead, AppPermissions.ServerCertificatesManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteCertificateClient)) as IRemoteCertificateClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.certificates.display_name"),
                new TextBlock { Text = LocalizedText.Get("certificates.login_required"), Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap },
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new CertificateManagerViewModel(client, session, context.Permissions);
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.certificates.display_name"),
            CreateView(viewModel), new Rect(60, 50, 1180, 780), Manifest.IconGlyph);
        _ = viewModel.StartAsync();
    }

    private static Control CreateView(CertificateManagerViewModel vm)
    {
        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true, DataContext = vm };

        // Top toolbar: refresh + live operation status + cancel.
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(toolbar, Dock.Top);
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand });
        var cancel = new Button { Content = LocalizedText.Get("common.stop"), Command = vm.CancelOperationCommand };
        cancel.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(vm.IsOperationRunning)));
        toolbar.Children.Add(cancel);
        var operation = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) };
        operation.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.OperationText)));
        toolbar.Children.Add(operation);
        root.Children.Add(toolbar);

        // Request editor.
        var editor = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(editor, Dock.Top);
        editor.Children.Add(new TextBlock { Text = LocalizedText.Get("certificates.request.title"), FontWeight = FontWeight.SemiBold });
        editor.Children.Add(new TextBlock { Text = LocalizedText.Get("certificates.request.help"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
        editor.Children.Add(TextField(vm, nameof(vm.Domains), "certificates.request.domains", "certificates.request.domains_hint", 520));
        editor.Children.Add(TextField(vm, nameof(vm.ContactEmail), "certificates.request.contact_email", "certificates.request.contact_email_hint", 320));
        var editorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Bottom };
        editorRow.Children.Add(ChoiceField(vm, nameof(vm.SelectedChallengeType), vm.ChallengeTypes, "certificates.request.challenge", 220));
        editorRow.Children.Add(ChoiceField(vm, nameof(vm.SelectedKeyAlgorithm), vm.KeyAlgorithms, "certificates.request.key_algorithm", 180));
        editor.Children.Add(editorRow);
        var checks = new StackPanel { Spacing = 4 };
        var terms = new CheckBox { Content = LocalizedText.Get("certificates.request.accept_terms") };
        terms.Bind(CheckBox.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.AcceptedTerms)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        checks.Children.Add(terms);
        var reach = new CheckBox { Content = LocalizedText.Get("certificates.request.confirm_reachability") };
        reach.Bind(CheckBox.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.PublicReachabilityConfirmed)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        checks.Children.Add(reach);
        editor.Children.Add(checks);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(new Button { Content = LocalizedText.Get("certificates.request.preflight"), Command = vm.PreflightCommand });
        actions.Children.Add(new Button { Content = LocalizedText.Get("certificates.request.submit"), Command = vm.RequestCommand, Classes = { "primary" } });
        editor.Children.Add(actions);
        var preflight = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
        preflight.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.PreflightText)));
        editor.Children.Add(preflight);
        root.Children.Add(editor);

        // Selected-certificate actions.
        var selectedActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(selectedActions, Dock.Top);
        selectedActions.Children.Add(new Button { Content = LocalizedText.Get("certificates.action.deploy"), Command = vm.DeployCommand });
        selectedActions.Children.Add(new Button { Content = LocalizedText.Get("certificates.action.renew"), Command = vm.RenewCommand });
        selectedActions.Children.Add(new Button { Content = LocalizedText.Get("certificates.action.revoke"), Command = vm.RevokeCommand });
        selectedActions.Children.Add(new Button { Content = LocalizedText.Get("common.delete"), Command = vm.DeleteCommand });
        root.Children.Add(selectedActions);

        // Status line.
        var status = new TextBlock { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        root.Children.Add(CreateCertificateTable(vm));
        return root;
    }

    private static DataGrid CreateCertificateTable(CertificateManagerViewModel vm)
    {
        var table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ItemsSource = vm.Certificates
        };
        table.Columns.Add(Column("certificates.column.domain", nameof(CertificateDto.PrimaryDomain), 220));
        table.Columns.Add(Column("certificates.column.status", nameof(CertificateDto.Status), 110));
        table.Columns.Add(Column("certificates.column.challenge", nameof(CertificateDto.ChallengeType), 130));
        table.Columns.Add(Column("certificates.column.issuer", nameof(CertificateDto.Issuer), 180));
        table.Columns.Add(Column("certificates.column.not_before", nameof(CertificateDto.NotBefore), 180));
        table.Columns.Add(Column("certificates.column.not_after", nameof(CertificateDto.NotAfter), 180));
        table.SelectionChanged += (_, _) => vm.SelectedCertificate = table.SelectedItem as CertificateDto;
        return table;
    }

    private static DataGridTextColumn Column(string headerKey, string property, double width) => new()
    {
        Header = LocalizedText.Get(headerKey), Binding = new Avalonia.Data.Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel)
    };

    private static Control ChoiceField<T>(CertificateManagerViewModel vm, string property, IReadOnlyList<CertificateOption<T>> choices, string labelKey, double width) where T : struct
    {
        var field = new StackPanel { Spacing = 4, Width = width, DataContext = vm };
        field.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey) });
        var box = new ComboBox { ItemsSource = choices, DisplayMemberBinding = new Avalonia.Data.Binding(nameof(CertificateOption<T>.Label)) };
        box.Bind(SelectingItemsControl.SelectedItemProperty, new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay });
        field.Children.Add(box);
        return field;
    }

    private static Control TextField(CertificateManagerViewModel vm, string property, string labelKey, string hintKey, double width)
    {
        var field = new StackPanel { Spacing = 4, Width = width, DataContext = vm };
        field.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey) });
        var box = new TextBox { PlaceholderText = LocalizedText.Get(hintKey) };
        box.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay });
        field.Children.Add(box);
        return field;
    }
}
