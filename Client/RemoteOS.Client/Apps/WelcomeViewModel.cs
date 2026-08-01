using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;

namespace Client.Apps;

public partial class WelcomeViewModel : ObservableObject
{
    [RelayCommand]
    private void OpenNotepad()
    {
        App.Services.GetRequiredService<ApplicationManager>()
            .Launch(new AppId("remoteos.notepad"));
    }
}
