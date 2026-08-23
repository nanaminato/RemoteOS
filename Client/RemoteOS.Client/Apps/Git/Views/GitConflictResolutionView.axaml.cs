using Avalonia.Controls;

namespace Client.Apps.Git.Views;

internal partial class GitConflictResolutionView : UserControl
{
    public GitConflictResolutionView() => InitializeComponent();
    public GitConflictResolutionView(GitClientViewModel vm) : this() => DataContext = vm;
}
