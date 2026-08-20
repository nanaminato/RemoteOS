using Avalonia.Controls;

namespace Client.Apps.Git.Views;

internal partial class GitOverviewView : UserControl
{
    public GitOverviewView() => InitializeComponent();
    public GitOverviewView(GitClientViewModel vm) : this() => DataContext = vm;
}
