using RemoteOS.AppSDK;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Certificates.Views;

/// <summary>Opens Certificate Manager modal workflows at their intended sizes.</summary>
internal static class CertificateManagerDialogs
{
    public static Task ShowRequestCertificateAsync(AppContext context, ManagedWindow owner, CertificateManagerViewModel viewModel) =>
        context.ShowDialogAsync<bool>(owner, Client.Localization.LocalizedText.Get("certificates.request.title"),
            dialog => new CertificateRequestDialogView(viewModel, dialog),
            new RemoteOS.Core.Primitives.Size(620, 620));

    public static Task ShowCreateSelfSignedCertificateAsync(AppContext context, ManagedWindow owner, CertificateManagerViewModel viewModel) =>
        context.ShowDialogAsync<bool>(owner, Client.Localization.LocalizedText.Get("certificates.self_signed.title"),
            dialog => new SelfSignedCertificateDialogView(viewModel, dialog),
            new RemoteOS.Core.Primitives.Size(560, 430));
}
