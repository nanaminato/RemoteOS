using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteOS.Protocol.Git;
using RemoteOS.WindowManager;

namespace Client.Apps.Git.Views;

internal partial class GitPushDialog : UserControl
{
    private readonly GitClientViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public GitPushDialog(GitClientViewModel viewModel, ModalDialog<bool> dialog)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void BranchLine_PointerPressed(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectPushRemoteBranchCommand.IsRunning) return;
        await _viewModel.SelectPushRemoteBranchCommand.ExecuteAsync(_dialog.Window);
    }

    private async void AllCommits_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.SelectAllPushCommitsCommand.ExecuteAsync(null);
    }

    private async void CommitList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: GitCommitDto commit })
            await _viewModel.SelectPushCommitCommand.ExecuteAsync(commit);
    }

    private void Push_Click(object? sender, RoutedEventArgs e) => _dialog.Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
}
