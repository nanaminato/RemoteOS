using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteOS.Protocol.Git;

namespace Client.Apps.Git.Views;

/// <summary>项目选择视图：列出已注册项目供直接打开，或新打开一个远程文件夹作为新项目。</summary>
internal partial class GitProjectPickerView : UserControl
{
    private GitClientViewModel? _vm;

    public GitProjectPickerView() => InitializeComponent();
    public GitProjectPickerView(GitClientViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GitRepositoryDto repo } && _vm is not null)
            _vm.OpenProjectCommand.Execute(repo);
    }
}
