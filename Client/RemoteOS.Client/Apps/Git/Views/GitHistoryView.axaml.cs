using Avalonia.Controls;

namespace Client.Apps.Git.Views;

internal partial class GitHistoryView : UserControl
{
    public GitHistoryView() => InitializeComponent();
    public GitHistoryView(GitClientViewModel vm) : this() => DataContext = vm;
}
