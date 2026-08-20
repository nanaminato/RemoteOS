using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteOS.Protocol.Git;

namespace Client.Apps.Git.Views;

internal partial class GitWorkspaceView : UserControl
{
    private GitClientViewModel? _vm;

    public GitWorkspaceView() => InitializeComponent();
    public GitWorkspaceView(GitClientViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private void StageFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GitFileChangeDto file } && _vm is not null)
            _vm.StageFileCommand.Execute(file);
    }
}
