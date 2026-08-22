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

    private void Commit_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.SelectedCount == 0)
        {
            _vm.StatusText = "请先选择要提交的文件";
            return;
        }
        if (string.IsNullOrWhiteSpace(_vm.CommitMessage))
        {
            _vm.StatusText = "请输入提交消息";
            return;
        }
        _vm.CommitCommand.Execute(null);
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e)
    {
        _vm?.ClearSelectionCommand.Execute(null);
    }
}
