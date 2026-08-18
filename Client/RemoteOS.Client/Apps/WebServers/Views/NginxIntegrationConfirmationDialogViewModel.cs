using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps.WebServers.Views;

/// <summary>State for the Nginx integration confirmation dialog.</summary>
internal sealed partial class NginxIntegrationConfirmationDialogViewModel(Action<bool> complete) : ObservableObject
{
    public string Message => LocalizedText.Get("webservers.integration.confirmation.message");
    public string ConfirmLabel => LocalizedText.Get("webservers.integration.confirmation.confirm");

    [RelayCommand]
    private void Confirm() => complete(true);

    [RelayCommand]
    private void Cancel() => complete(false);
}
