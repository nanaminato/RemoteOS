using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Client.Apps.Git.Views;

/// <summary>Git Client shell. Layout lives in AXAML; this class switches pages and handles navigation.
/// 支持两种显示模式：IsPickerMode=true 时显示项目选择器；否则显示当前仓库的工作区。</summary>
internal partial class GitClientWorkspace : UserControl
{
    private readonly GitClientViewModel _viewModel;
    private Button? _selectedButton;

    private GitClientWorkspace(GitClientViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        ShowPage("overview");
    }

    public static Control Create(GitClientViewModel viewModel) => new GitClientWorkspace(viewModel);

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } btn)
            ShowPage(section, btn);
    }

    private void ShowPage(string section, Button? sourceButton = null)
    {
        // Reset previously selected nav button
        if (_selectedButton is not null)
        {
            _selectedButton.Background = Brushes.Transparent;
            _selectedButton.Foreground = Brush.Parse("#36506F");
        }

        // Highlight the active nav button (if triggered from a button)
        if (sourceButton is not null)
        {
            sourceButton.Background = Brush.Parse("#DCE6F4");
            sourceButton.Foreground = Brush.Parse("#122344");
            _selectedButton = sourceButton;
        }

        ContentHost.Content = section switch
        {
            "workspace" => new GitWorkspaceView(_viewModel),
            "branches" => new GitBranchesView(_viewModel),
            "history" => new GitHistoryView(_viewModel),
            "remotes" => new GitRemotesView(_viewModel),
            "conflicts" => new GitConflictResolutionView(_viewModel),
            _ => new GitOverviewView(_viewModel)
        };
    }
}
