using Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Client.Apps;

public partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(ShellSettings settings)
    {
        Settings = settings;
    }

    public ShellSettings Settings { get; }
}
