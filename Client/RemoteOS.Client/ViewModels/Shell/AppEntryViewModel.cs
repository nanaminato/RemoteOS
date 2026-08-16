using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;

namespace Client.ViewModels.Shell;

/// <summary>A launchable application entry shown on the desktop and in the start menu.</summary>
public partial class AppEntryViewModel : ObservableObject
{
    private readonly ApplicationManager _applications;

    public AppEntryViewModel(ApplicationInfo info, ApplicationManager applications)
    {
        _applications = applications;
        Id = info.Id;
        DisplayName = info.DisplayName;
        IconGlyph = info.IconGlyph;
        Description = info.Description;
    }

    public AppId Id { get; }
    public string DisplayName { get; }
    public string? IconGlyph { get; }
    public string? Description { get; }
    [ObservableProperty] private bool _isDesktopSelected;

    [RelayCommand]
    private void Launch()
    {
        _applications.Launch(Id);
    }
}
