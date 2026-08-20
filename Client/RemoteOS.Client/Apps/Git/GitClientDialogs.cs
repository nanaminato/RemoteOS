using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Protocol.Git;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Git;

/// <summary>Dialog factory for the Git Client. Uses AppContext.ShowDialogAsync (same pattern as DockerManagerDialogs).</summary>
internal static class GitClientDialogs
{
    public static Task<GitCommitRequest?> ShowCommitDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<GitCommitRequest?>(owner, "Commit",
            dialog => BuildCommitDialog(vm, dialog),
            new RemoteOS.Core.Primitives.Size(470, 380));

    public static Task<GitBranchCreateRequest?> ShowCreateBranchDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<GitBranchCreateRequest?>(owner, "New Branch",
            dialog => BuildCreateBranchDialog(dialog),
            new RemoteOS.Core.Primitives.Size(470, 240));

    public static Task<GitPullRequest?> ShowPullDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<GitPullRequest?>(owner, "Pull",
            dialog => BuildPullDialog(dialog),
            new RemoteOS.Core.Primitives.Size(420, 200));

    public static Task<GitRepositoryRegistration?> ShowRegisterRepositoryDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<GitRepositoryRegistration?>(owner, "Register Repository",
            dialog => BuildRegisterDialog(dialog),
            new RemoteOS.Core.Primitives.Size(500, 260));

    public static async Task<bool> ShowConfirmAsync(AppContext context, ManagedWindow owner, string message)
    {
        var result = await context.ShowDialogAsync<bool?>(owner, "Confirm",
            dialog => BuildConfirmDialog(message, dialog),
            new RemoteOS.Core.Primitives.Size(400, 180));
        return result ?? false;
    }

    /// <summary>Show the "Git engine unavailable" dialog with install/refresh/cancel actions.</summary>
    public static Task<bool> ShowGitUnavailableAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, "Git 未安装或不可用",
            dialog => BuildGitUnavailableDialog(vm, dialog),
            new RemoteOS.Core.Primitives.Size(520, 320));

    // ── Dialog builders (programmatic — no separate AXAML files needed) ──

    private static Control BuildCommitDialog(GitClientViewModel vm, ModalDialog<GitCommitRequest?> dialog)
    {
        var messageBox = new TextBox { PlaceholderText = "Commit message…", MinHeight = 60, AcceptsReturn = true };
        var amendCheck = new CheckBox { Content = "Amend last commit" };

        var commitBtn = new Button
        {
            Content = "✔ Commit",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        commitBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(messageBox.Text))
                return;
            var paths = vm.StagedFiles.Select(f => f.Path).ToArray();
            dialog.Close(new GitCommitRequest(messageBox.Text!, paths, amendCheck.IsChecked == true));
        };

        var cancelBtn = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                messageBox,
                amendCheck,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { commitBtn, cancelBtn }
                }
            }
        };
    }

    private static Control BuildCreateBranchDialog(ModalDialog<GitBranchCreateRequest?> dialog)
    {
        var nameBox = new TextBox { PlaceholderText = "Branch name…" };
        var startBox = new TextBox { PlaceholderText = "Start point (optional)…" };
        var trackCheck = new CheckBox { Content = "Track upstream" };

        var createBtn = new Button { Content = "Create", HorizontalAlignment = HorizontalAlignment.Right };
        createBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) return;
            dialog.Close(new GitBranchCreateRequest(nameBox.Text!,
                string.IsNullOrWhiteSpace(startBox.Text) ? null : startBox.Text,
                trackCheck.IsChecked == true));
        };

        var cancelBtn = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Branch name", FontSize = 13 },
                nameBox,
                new TextBlock { Text = "Start point", FontSize = 13 },
                startBox,
                trackCheck,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { createBtn, cancelBtn }
                }
            }
        };
    }

    private static Control BuildPullDialog(ModalDialog<GitPullRequest?> dialog)
    {
        var mergeRadio = new RadioButton { Content = "Merge", IsChecked = true, GroupName = "strategy" };
        var rebaseRadio = new RadioButton { Content = "Rebase", GroupName = "strategy" };

        var pullBtn = new Button { Content = "Pull", HorizontalAlignment = HorizontalAlignment.Right };
        pullBtn.Click += (_, _) =>
            dialog.Close(new GitPullRequest(rebaseRadio.IsChecked == true ? "rebase" : "merge"));

        var cancelBtn = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Strategy", FontSize = 13 },
                new StackPanel { Spacing = 8, Children = { mergeRadio, rebaseRadio } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { pullBtn, cancelBtn }
                }
            }
        };
    }

    private static Control BuildRegisterDialog(ModalDialog<GitRepositoryRegistration?> dialog)
    {
        var nameBox = new TextBox { PlaceholderText = "Repository name…" };
        var pathBox = new TextBox { PlaceholderText = "/absolute/path/to/repo" };

        var registerBtn = new Button { Content = "Register", HorizontalAlignment = HorizontalAlignment.Right };
        registerBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(pathBox.Text)) return;
            dialog.Close(new GitRepositoryRegistration(nameBox.Text!, pathBox.Text!));
        };

        var cancelBtn = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Name", FontSize = 13 },
                nameBox,
                new TextBlock { Text = "Path (absolute)", FontSize = 13 },
                pathBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { registerBtn, cancelBtn }
                }
            }
        };
    }

    private static Control BuildConfirmDialog(string message, ModalDialog<bool?> dialog)
    {
        var yesBtn = new Button { Content = "Yes", HorizontalAlignment = HorizontalAlignment.Right };
        yesBtn.Click += (_, _) => dialog.Close(true);

        var noBtn = new Button { Content = "No", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        noBtn.Click += (_, _) => dialog.Close(false);

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yesBtn, noBtn }
                }
            }
        };
    }

    private static Control BuildGitUnavailableDialog(GitClientViewModel vm, ModalDialog<bool> dialog)
    {
        var header = new TextBlock
        {
            Text = "当前服务器未检测到可用的 Git 命令行工具。\n请安装 Git 后再使用本应用。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14,
        };

        var statusBox = new TextBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            IsReadOnly = true,
            MinHeight = 80,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        statusBox.Bind(TextBox.TextProperty, new Avalonia.Data.Binding
        {
            Path = nameof(GitClientViewModel.InstallMessage),
            Mode = Avalonia.Data.BindingMode.OneWay,
            FallbackValue = "",
            TargetNullValue = "",
        });

        var installBtn = new Button
        {
            Content = "安装 Git",
            Classes = { "primary" },
            IsEnabled = !vm.IsInstalling && vm.CanAutoInstall,
        };
        installBtn.Click += async (_, _) =>
        {
            installBtn.IsEnabled = false;
            await vm.InstallEngineCommand.ExecuteAsync(null);
            if (vm.IsGitAvailable) dialog.Close(true);
            installBtn.IsEnabled = !vm.IsInstalling && vm.CanAutoInstall && !vm.IsGitAvailable;
        };

        var refreshBtn = new Button { Content = "刷新检测" };
        refreshBtn.Click += async (_, _) =>
        {
            await vm.RefreshEngineStatusCommand.ExecuteAsync(null);
            if (vm.IsGitAvailable) dialog.Close(true);
        };

        var cancelBtn = new Button { Content = "取消 / 退出", Margin = new Thickness(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Close(false);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { installBtn, refreshBtn, cancelBtn },
            Spacing = 8,
        };

        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children = { header, statusBox, footer }
        };
    }
}
