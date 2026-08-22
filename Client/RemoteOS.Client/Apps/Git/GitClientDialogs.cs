using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Localization;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Protocol.Git;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Apps.Git;

/// <summary>Dialog factory for the Git Client. Uses AppContext.ShowDialogAsync (same pattern as DockerManagerDialogs).
/// All user-facing strings are resolved through <see cref="LocalizedText"/> using keys from git_client.json.</summary>
internal static class GitClientDialogs
{
    public static Task<GitCommitRequest?> ShowCommitDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<GitCommitRequest?>(owner, LocalizedText.Get("git.dialog.commit.title"),
            dialog => BuildCommitDialog(vm, dialog),
            new RemoteOS.Core.Primitives.Size(470, 380));

    public static Task<GitBranchCreateRequest?> ShowCreateBranchDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<GitBranchCreateRequest?>(owner, LocalizedText.Get("git.dialog.new_branch.title"),
            dialog => BuildCreateBranchDialog(dialog),
            new RemoteOS.Core.Primitives.Size(470, 240));

    public static Task<GitPullRequest?> ShowPullDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<GitPullRequest?>(owner, LocalizedText.Get("git.dialog.pull.title"),
            dialog => BuildPullDialog(dialog),
            new RemoteOS.Core.Primitives.Size(420, 200));

    public static Task<GitRepositoryRegistration?> ShowRegisterRepositoryDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<GitRepositoryRegistration?>(owner, LocalizedText.Get("git.dialog.register.title"),
            dialog => BuildRegisterDialog(dialog),
            new RemoteOS.Core.Primitives.Size(500, 260));

    public static async Task<bool> ShowConfirmAsync(AppContext context, ManagedWindow owner, string message)
    {
        var result = await context.ShowDialogAsync<bool?>(owner, LocalizedText.Get("git.dialog.confirm"),
            dialog => BuildConfirmDialog(message, dialog),
            new RemoteOS.Core.Primitives.Size(400, 180));
        return result ?? false;
    }

    /// <summary>Single-button modal notice. Use this for validation/errors that must grab the user's
    /// attention (e.g. "commit message required", "commit failed: …") instead of writing to StatusText.</summary>
    public static async Task ShowMessageAsync(AppContext context, ManagedWindow owner, string message)
    {
        _ = await context.ShowDialogAsync<bool?>(owner, LocalizedText.Get("git.dialog.notice"),
            dialog => BuildMessageDialog(message, dialog),
            new RemoteOS.Core.Primitives.Size(420, 200));
    }

    /// <summary>Show the "Git engine unavailable" dialog with install/refresh/cancel actions.</summary>
    public static Task<bool> ShowGitUnavailableAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, LocalizedText.Get("git.dialog.git_unavailable.title"),
            dialog => BuildGitUnavailableDialog(vm, dialog),
            new RemoteOS.Core.Primitives.Size(520, 320));

    // ── 项目选择 / 探测 / 远程管理 ──

    /// <summary>Confirms whether to initialize a Git repository at the supplied path.</summary>
    public static async Task<bool> ShowInitConfirmAsync(AppContext context, ManagedWindow owner, string path)
    {
        var result = await context.ShowDialogAsync<bool?>(owner, LocalizedText.Get("git.dialog.init.title"),
            dialog => BuildInitConfirmDialog(path, dialog),
            new RemoteOS.Core.Primitives.Size(440, 220));
        return result ?? false;
    }

    /// <summary>Prompts the user for a new/edited remote. <paramref name="existing"/> null = add; non-null = edit.</summary>
    public static Task<GitRemoteRequest?> ShowRemoteDialogAsync(AppContext context, ManagedWindow owner, GitRemoteDto? existing)
        => context.ShowDialogAsync<GitRemoteRequest?>(owner,
            existing is null ? LocalizedText.Get("git.dialog.add_remote.title")
                             : LocalizedText.Format("git.dialog.edit_remote.title_format", existing.Name),
            dialog => BuildRemoteDialog(existing, dialog),
            new RemoteOS.Core.Primitives.Size(480, 240));

    /// <summary>Prompts for a new branch name when renaming. Returns null on cancel.</summary>
    public static Task<string?> ShowRenameBranchDialogAsync(AppContext context, ManagedWindow owner, GitBranchDto branch)
        => context.ShowDialogAsync<string?>(owner, LocalizedText.Format("git.dialog.rename_branch.title_format", branch.Name),
            dialog => BuildRenameBranchDialog(branch, dialog),
            new RemoteOS.Core.Primitives.Size(420, 180));

    /// <summary>Prompts for merge strategy (merge/no-ff/ff-only/squash) + optional message. Returns null on cancel.</summary>
    public static Task<GitMergeRequest?> ShowMergeDialogAsync(AppContext context, ManagedWindow owner, GitBranchDto sourceBranch, GitClientViewModel vm)
        => context.ShowDialogAsync<GitMergeRequest?>(owner, LocalizedText.Format("git.dialog.merge.title_format", sourceBranch.Name),
            dialog => BuildMergeDialog(sourceBranch, vm, dialog),
            new RemoteOS.Core.Primitives.Size(460, 340));

    /// <summary>Prompts for an upstream (remote/branch) to track, or choose "Unset". Returns null on cancel.</summary>
    public static Task<GitBranchTrackingRequest?> ShowSetTrackingDialogAsync(AppContext context, ManagedWindow owner, GitBranchDto localBranch, GitClientViewModel vm)
        => context.ShowDialogAsync<GitBranchTrackingRequest?>(owner, LocalizedText.Format("git.dialog.set_tracking.title_format", localBranch.Name),
            dialog => BuildSetTrackingDialog(localBranch, vm, dialog),
            new RemoteOS.Core.Primitives.Size(460, 260));

    // ── Dialog builders (programmatic — no separate AXAML files needed) ──

    private static Control BuildInitConfirmDialog(string path, ModalDialog<bool?> dialog)
    {
        var msg = new TextBlock
        {
            Text = LocalizedText.Format("git.dialog.init.message_format", path),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };
        var initBtn = new Button { Content = LocalizedText.Get("git.dialog.init.confirm"), Background = Brush.Parse("#1C3765"), Foreground = Brushes.White, Padding = new(14, 6) };
        initBtn.Click += (_, _) => dialog.Close(true);
        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.init.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Close(false);
        return new StackPanel
        {
            Margin = new(20),
            Spacing = 16,
            Children =
            {
                msg,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { initBtn, cancelBtn },
                },
            },
        };
    }

    private static Control BuildRemoteDialog(GitRemoteDto? existing, ModalDialog<GitRemoteRequest?> dialog)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.remote.name_placeholder"),
            Text = existing?.Name ?? string.Empty,
        };
        var urlBox = new TextBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.remote.url_placeholder"),
            Text = existing?.FetchUrl ?? string.Empty,
        };
        var pushBox = new TextBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.remote.push_placeholder"),
            Text = existing?.PushUrl ?? string.Empty,
        };

        var saveBtn = new Button
        {
            Content = existing is null ? LocalizedText.Get("git.dialog.remote.add") : LocalizedText.Get("git.dialog.remote.save"),
            Background = Brush.Parse("#1C3765"),
            Foreground = Brushes.White,
            Padding = new(14, 6),
        };
        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(urlBox.Text)) return;
            dialog.Close(new GitRemoteRequest(
                nameBox.Text!.Trim(),
                urlBox.Text!.Trim(),
                string.IsNullOrWhiteSpace(pushBox.Text) ? null : pushBox.Text!.Trim()));
        };

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.remote.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = LocalizedText.Get("git.dialog.remote.name_label"), FontSize = 13 },
                nameBox,
                new TextBlock { Text = LocalizedText.Get("git.dialog.remote.fetch_url_label"), FontSize = 13 },
                urlBox,
                new TextBlock { Text = LocalizedText.Get("git.dialog.remote.push_url_label"), FontSize = 13 },
                pushBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { saveBtn, cancelBtn },
                },
            },
        };
    }

    // ── Dialog builders (programmatic — no separate AXAML files needed) ──

    private static Control BuildCommitDialog(GitClientViewModel vm, ModalDialog<GitCommitRequest?> dialog)
    {
        var messageBox = new TextBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.commit.message_placeholder"),
            MinHeight = 60,
            AcceptsReturn = true,
            Text = vm.CommitMessage
        };
        // Sync edits back to ViewModel
        messageBox.TextChanged += (_, _) => vm.CommitMessage = messageBox.Text ?? string.Empty;

        var amendCheck = new CheckBox { Content = LocalizedText.Get("git.dialog.commit.amend") };

        var fileListTitle = new TextBlock
        {
            Text = LocalizedText.Format("git.dialog.commit.files_to_commit_format", vm.SelectedCount),
            FontSize = 12,
            Foreground = Brush.Parse("#666"),
            Margin = new(0, 8, 0, 4)
        };

        var fileListBox = new ListBox
        {
            Height = 100,
            ItemsSource = vm.SelectedFilePaths,
        };

        var commitBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.commit.commit_btn"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        commitBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(messageBox.Text))
                return;
            var paths = vm.SelectedFilePaths.ToArray();
            if (paths.Length == 0)
                return;
            dialog.Close(new GitCommitRequest(messageBox.Text!, paths, amendCheck.IsChecked == true));
        };

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.commit.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 8,
            Children =
            {
                messageBox,
                amendCheck,
                fileListTitle,
                fileListBox,
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
        var nameBox = new TextBox { PlaceholderText = LocalizedText.Get("git.dialog.new_branch.name_placeholder") };
        var startBox = new TextBox { PlaceholderText = LocalizedText.Get("git.dialog.new_branch.start_placeholder") };
        var trackCheck = new CheckBox { Content = LocalizedText.Get("git.dialog.new_branch.track") };

        var createBtn = new Button { Content = LocalizedText.Get("git.dialog.new_branch.create"), HorizontalAlignment = HorizontalAlignment.Right };
        createBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) return;
            dialog.Close(new GitBranchCreateRequest(nameBox.Text!,
                string.IsNullOrWhiteSpace(startBox.Text) ? null : startBox.Text,
                trackCheck.IsChecked == true));
        };

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.new_branch.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = LocalizedText.Get("git.dialog.new_branch.name_label"), FontSize = 13 },
                nameBox,
                new TextBlock { Text = LocalizedText.Get("git.dialog.new_branch.start_label"), FontSize = 13 },
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
        var mergeRadio = new RadioButton { Content = LocalizedText.Get("git.dialog.pull.merge"), IsChecked = true, GroupName = "strategy" };
        var rebaseRadio = new RadioButton { Content = LocalizedText.Get("git.dialog.pull.rebase"), GroupName = "strategy" };

        var pullBtn = new Button { Content = LocalizedText.Get("git.dialog.pull.pull_btn"), HorizontalAlignment = HorizontalAlignment.Right };
        pullBtn.Click += (_, _) =>
            dialog.Close(new GitPullRequest(rebaseRadio.IsChecked == true ? "rebase" : "merge"));

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.pull.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = LocalizedText.Get("git.dialog.pull.strategy"), FontSize = 13 },
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
        var nameBox = new TextBox { PlaceholderText = LocalizedText.Get("git.dialog.register.name_placeholder") };
        var pathBox = new TextBox { PlaceholderText = LocalizedText.Get("git.dialog.register.path_placeholder") };

        var registerBtn = new Button { Content = LocalizedText.Get("git.dialog.register.register_btn"), HorizontalAlignment = HorizontalAlignment.Right };
        registerBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(pathBox.Text)) return;
            dialog.Close(new GitRepositoryRegistration(nameBox.Text!, pathBox.Text!));
        };

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.register.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = LocalizedText.Get("git.dialog.register.name_label"), FontSize = 13 },
                nameBox,
                new TextBlock { Text = LocalizedText.Get("git.dialog.register.path_label"), FontSize = 13 },
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
        var yesBtn = new Button { Content = LocalizedText.Get("git.dialog.confirm.yes"), HorizontalAlignment = HorizontalAlignment.Right };
        yesBtn.Click += (_, _) => dialog.Close(true);

        var noBtn = new Button { Content = LocalizedText.Get("git.dialog.confirm.no"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
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

    private static Control BuildMessageDialog(string message, ModalDialog<bool?> dialog)
    {
        var okBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.notice.ok"),
            Background = Brush.Parse("#1C3765"),
            Foreground = Brushes.White,
            Padding = new(20, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okBtn.Click += (_, _) => dialog.Close(true);

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { okBtn },
                },
            },
        };
    }

    private static Control BuildGitUnavailableDialog(GitClientViewModel vm, ModalDialog<bool> dialog)
    {
        var header = new TextBlock
        {
            Text = LocalizedText.Get("git.dialog.unavailable.message"),
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
            Content = LocalizedText.Get("git.dialog.unavailable.install"),
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

        var refreshBtn = new Button { Content = LocalizedText.Get("git.dialog.unavailable.refresh") };
        refreshBtn.Click += async (_, _) =>
        {
            await vm.RefreshEngineStatusCommand.ExecuteAsync(null);
            if (vm.IsGitAvailable) dialog.Close(true);
        };

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.unavailable.exit"), Margin = new Thickness(8, 0, 0, 0) };
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

    // ── 分支管理对话框构建器 ──

    private static Control BuildRenameBranchDialog(GitBranchDto branch, ModalDialog<string?> dialog)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.rename.placeholder"),
            Text = branch.Name,
        };
        nameBox.SelectAll();

        var renameBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.rename.rename_btn"),
            Background = Brush.Parse("#1C3765"),
            Foreground = Brushes.White,
            Padding = new(14, 6),
        };
        renameBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) return;
            var newName = nameBox.Text!.Trim();
            if (newName == branch.Name) return;
            dialog.Close(newName);
        };

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.rename.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        var currentSuffix = branch.IsCurrent ? LocalizedText.Get("git.dialog.rename.current_suffix") : string.Empty;

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = LocalizedText.Format("git.dialog.rename.current_branch_format", branch.Name, currentSuffix),
                    FontSize = 13,
                    Foreground = Brush.Parse("#666"),
                },
                new TextBlock { Text = LocalizedText.Get("git.dialog.rename.new_name_label"), FontSize = 13 },
                nameBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { renameBtn, cancelBtn },
                },
            },
        };
    }

    private static Control BuildMergeDialog(GitBranchDto sourceBranch, GitClientViewModel vm, ModalDialog<GitMergeRequest?> dialog)
    {
        var currentBranchName = vm.Status?.Branch ?? LocalizedText.Get("git.dialog.merge.unknown_branch");

        var mergeRadio = new RadioButton { Content = LocalizedText.Get("git.dialog.merge.merge_default"), GroupName = "strategy", IsChecked = true };
        var noFfRadio = new RadioButton { Content = LocalizedText.Get("git.dialog.merge.no_ff"), GroupName = "strategy" };
        var ffOnlyRadio = new RadioButton { Content = LocalizedText.Get("git.dialog.merge.ff_only"), GroupName = "strategy" };
        var squashRadio = new RadioButton { Content = LocalizedText.Get("git.dialog.merge.squash"), GroupName = "strategy" };

        var noCommitCheck = new CheckBox
        {
            Content = LocalizedText.Get("git.dialog.merge.no_commit"),
        };

        var messageBox = new TextBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.merge.message_placeholder"),
            MinHeight = 70,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
        };

        var mergeBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.merge.merge_btn"),
            Background = Brush.Parse("#1C3765"),
            Foreground = Brushes.White,
            Padding = new(14, 6),
        };
        mergeBtn.Click += (_, _) =>
        {
            string strategy;
            bool noCommit;
            if (noFfRadio.IsChecked == true) { strategy = "no-ff"; noCommit = noCommitCheck.IsChecked == true; }
            else if (ffOnlyRadio.IsChecked == true) { strategy = "ff-only"; noCommit = false; }
            else if (squashRadio.IsChecked == true) { strategy = "squash"; noCommit = true; }
            else { strategy = "merge"; noCommit = noCommitCheck.IsChecked == true; }

            dialog.Close(new GitMergeRequest(
                Source: sourceBranch.Name,
                Strategy: strategy,
                NoCommit: noCommit,
                Message: string.IsNullOrWhiteSpace(messageBox.Text) ? null : messageBox.Text!.Trim()));
        };

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.merge.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = LocalizedText.Format("git.dialog.merge.title_format_v2", sourceBranch.Name, currentBranchName),
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock { Text = LocalizedText.Get("git.dialog.merge.strategy_label"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) },
                new StackPanel
                {
                    Spacing = 6,
                    Children = { mergeRadio, noFfRadio, ffOnlyRadio, squashRadio },
                },
                noCommitCheck,
                new TextBlock { Text = LocalizedText.Get("git.dialog.merge.message_label"), FontSize = 13 },
                messageBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { mergeBtn, cancelBtn },
                },
            },
        };
    }

    private static Control BuildSetTrackingDialog(GitBranchDto localBranch, GitClientViewModel vm, ModalDialog<GitBranchTrackingRequest?> dialog)
    {
        var currentTracking = localBranch.Tracking ?? LocalizedText.Get("git.dialog.tracking.not_set");

        var unsetRadio = new RadioButton { Content = LocalizedText.Get("git.dialog.tracking.unset"), GroupName = "tracking" };
        var autoRadio = new RadioButton { Content = LocalizedText.Format("git.dialog.tracking.auto_format", localBranch.Name), GroupName = "tracking", IsChecked = true };
        var customRadio = new RadioButton { Content = LocalizedText.Get("git.dialog.tracking.custom"), GroupName = "tracking" };

        var remotes = vm.Remotes.ToList();
        var remoteBox = new ComboBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.tracking.remote_placeholder"),
            ItemsSource = remotes.Select(r => r.Name).ToList(),
        };
        if (remotes.Count > 0) remoteBox.SelectedIndex = 0;

        var branchBox = new TextBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.tracking.branch_placeholder"),
            Text = localBranch.Name,
        };

        // 自定义区域容器：选中 custom 时启用
        var customPanel = new StackPanel
        {
            IsEnabled = false,
            Spacing = 8,
        };
        var label1 = new TextBlock { Text = LocalizedText.Get("git.dialog.tracking.remote_label"), FontSize = 13 };
        var label2 = new TextBlock { Text = LocalizedText.Get("git.dialog.tracking.branch_label"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
        customPanel.Children.Add(label1);
        customPanel.Children.Add(remoteBox);
        customPanel.Children.Add(label2);
        customPanel.Children.Add(branchBox);

        // 点击 any Radio 时刷新 customPanel 启用状态
        void RefreshEnabled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            customPanel.IsEnabled = customRadio.IsChecked == true;
        }
        unsetRadio.Click += RefreshEnabled;
        autoRadio.Click += RefreshEnabled;
        customRadio.Click += RefreshEnabled;

        var setBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.tracking.set_btn"),
            Background = Brush.Parse("#1C3765"),
            Foreground = Brushes.White,
            Padding = new(14, 6),
        };
        setBtn.Click += (_, _) =>
        {
            if (unsetRadio.IsChecked == true)
            {
                dialog.Close(new GitBranchTrackingRequest(Upstream: null, Remote: null, Branch: null));
                return;
            }
            if (autoRadio.IsChecked == true)
            {
                dialog.Close(new GitBranchTrackingRequest(Remote: "origin", Branch: localBranch.Name));
                return;
            }
            // 自定义
            var remote = remoteBox.SelectedItem as string;
            var branch = string.IsNullOrWhiteSpace(branchBox.Text) ? null : branchBox.Text!.Trim();
            if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(branch)) return;
            dialog.Close(new GitBranchTrackingRequest(Remote: remote, Branch: branch));
        };

        var cancelBtn = new Button { Content = LocalizedText.Get("git.dialog.tracking.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        var currentSuffix = localBranch.IsCurrent ? LocalizedText.Get("git.dialog.tracking.current_suffix") : string.Empty;

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = LocalizedText.Format("git.dialog.tracking.local_branch_format", localBranch.Name, currentSuffix),
                    FontSize = 13,
                    Foreground = Brush.Parse("#666"),
                },
                new TextBlock
                {
                    Text = LocalizedText.Format("git.dialog.tracking.current_tracking_format", currentTracking),
                    FontSize = 13,
                    Foreground = Brush.Parse("#666"),
                },
                new Separator(),
                new StackPanel
                {
                    Spacing = 6,
                    Children = { unsetRadio, autoRadio, customRadio },
                },
                customPanel,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { setBtn, cancelBtn },
                },
            },
        };
    }

    // ── Push dialog ──

    public static Task<bool> ShowPushDialogAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm)
        => context.ShowDialogAsync<bool>(owner,
            LocalizedText.Format("git.dialog.push.title", vm.SelectedRepository?.Name ?? string.Empty),
            dialog => BuildPushDialog(vm, dialog),
            new RemoteOS.Core.Primitives.Size(820, 580));

    private static Control BuildPushDialog(GitClientViewModel vm, ModalDialog<bool> dialog)
    {
        var rootGrid = new Grid
        {
            RowDefinitions =
            {
                new GridRowDefinition(GridLength.Auto),
                new GridRowDefinition(new GridLength(1, GridUnitType.Star)),
                new GridRowDefinition(GridLength.Auto),
            },
            Margin = new Thickness(12),
            Spacing = new Thickness(0, 8),
        };

        // Row 0: Branch line + commit count
        var branchLine = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#1C3765"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Text = vm.PushBranchLineText,
        };
        branchLine.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
        {
            Path = nameof(GitClientViewModel.PushBranchLineText),
            Mode = Avalonia.Data.BindingMode.OneWay,
        });
        branchLine.PointerPressed += async (_, _) =>
        {
            if (vm.SelectPushRemoteBranchCommand.IsRunning) return;
            await vm.SelectPushRemoteBranchCommand.ExecuteAsync(null);
        };

        var commitCountText = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush.Parse("#666"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        commitCountText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
        {
            Path = nameof(GitClientViewModel.PushCommitCount),
            Mode = Avalonia.Data.BindingMode.OneWay,
            StringFormat = LocalizedText.Get("git.dialog.push.n_commits_format"),
        });

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { branchLine, commitCountText },
        };

        // Row 1: Two-column layout (commits | files)
        var mainSplit = new Grid
        {
            ColumnDefinitions =
            {
                new GridColumnDefinition(new GridLength(200)),
                new GridColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
        };

        // Left: commit list
        var commitList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemsSource = vm.PushCommits,
        };
        commitList.SelectionChanged += async (_, _) =>
        {
            if (commitList.SelectedItem is GitCommitDto commit)
                await vm.SelectPushCommitCommand.ExecuteAsync(commit);
        };

        var commitItemTemplate = new DataTemplate(() =>
        {
            var subject = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush.Parse("#122344"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            subject.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
            {
                Path = nameof(GitCommitDto.Subject),
                Mode = Avalonia.Data.BindingMode.OneWay,
            });

            var author = new TextBlock
            {
                FontSize = 10,
                Foreground = Brush.Parse("#888"),
            };
            author.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
            {
                Path = nameof(GitCommitDto.Author),
                Mode = Avalonia.Data.BindingMode.OneWay,
            });

            var itemPanel = new StackPanel
            {
                Spacing = 2,
                Padding = new Thickness(8, 6),
                Children = { subject, author },
            };
            return itemPanel;
        });
        commitList.ItemTemplate = commitItemTemplate;

        var leftBorder = new Border
        {
            Background = Brush.Parse("#F4F7FB"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = commitList,
        };
        Grid.SetColumn(leftBorder, 0);

        // Add "All commits" as first selectable item
        var allCommitsItem = new Button
        {
            Content = LocalizedText.Get("git.dialog.push.all_commits"),
            FontSize = 12,
            Padding = new Thickness(8, 6),
            Background = Brush.Parse("#E8EEF8"),
            Foreground = Brush.Parse("#1C3765"),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 4),
        };
        allCommitsItem.Click += async (_, _) =>
        {
            commitList.SelectedIndex = -1;
            await vm.SelectAllPushCommitsCommand.ExecuteAsync(null);
        };

        var leftPanel = new StackPanel
        {
            Children = { allCommitsItem, leftBorder },
        };
        Grid.SetColumn(leftPanel, 0);

        // Right: file changes
        var fileTree = new TreeView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            ItemsSource = vm.PushChangedFiles,
        };

        var fileItemTemplate = new DataTemplate(() =>
        {
            var icon = new TextBlock
            {
                Text = "📄",
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var path = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush.Parse("#122344"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            path.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
            {
                Path = nameof(GitFileChangeDto.Path),
                Mode = Avalonia.Data.BindingMode.OneWay,
            });

            var statusBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0),
                Margin = new Thickness(4, 0, 0, 0),
                Background = Brush.Parse("#0078D4"),
            };
            var statusText = new TextBlock
            {
                FontSize = 10,
                Foreground = Brushes.White,
            };
            statusText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
            {
                Path = nameof(GitFileChangeDto.Status),
                Mode = Avalonia.Data.BindingMode.OneWay,
            });
            statusBorder.Child = statusText;

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { icon, path, statusBorder },
            };
            return panel;
        });
        fileTree.ItemTemplate = fileItemTemplate;

        var fileCountText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush.Parse("#666"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        fileCountText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
        {
            Path = nameof(GitClientViewModel.PushFileCount),
            Mode = Avalonia.Data.BindingMode.OneWay,
            StringFormat = LocalizedText.Get("git.dialog.push.n_files_format"),
        });

        // Commit info panel (shown when single commit is selected)
        var commitInfoBorder = new Border
        {
            Background = Brush.Parse("#F8FAFC"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };
        commitInfoBorder.Bind(IsVisibleProperty, new Avalonia.Data.Binding
        {
            Path = nameof(GitClientViewModel.PushSingleCommitMode),
            Mode = Avalonia.Data.BindingMode.OneWay,
        });

        var commitSubject = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#122344"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        commitSubject.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
        {
            Path = $"{nameof(GitClientViewModel.PushSelectedCommit)}.{nameof(GitCommitDto.Subject)}",
            Mode = Avalonia.Data.BindingMode.OneWay,
        });

        var commitMeta = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush.Parse("#666"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        commitMeta.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding
        {
            Path = nameof(GitClientViewModel.PushSelectedCommit),
            Mode = Avalonia.Data.BindingMode.OneWay,
            Converter = new CommitMetaConverter(),
        });

        var commitInfoPanel = new StackPanel
        {
            Spacing = 2,
            Children = { commitSubject, commitMeta },
        };
        commitInfoBorder.Child = commitInfoPanel;

        var rightPanel = new StackPanel
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Spacing = 4,
            Children =
            {
                fileCountText,
                fileTree,
                commitInfoBorder,
            },
        };
        Grid.SetColumn(rightPanel, 1);

        mainSplit.Children.Add(leftPanel);
        mainSplit.Children.Add(rightPanel);

        // Row 2: Push button + Cancel
        var pushBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.push.push"),
            Background = Brush.Parse("#1C3765"),
            Foreground = Brushes.White,
            Padding = new Thickness(20, 6),
        };
        pushBtn.Click += (_, _) => dialog.Close(true);

        var cancelBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.push.cancel"),
            Padding = new Thickness(14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { pushBtn, cancelBtn },
        };

        rootGrid.Children.Add(headerPanel);
        Grid.SetRow(headerPanel, 0);
        rootGrid.Children.Add(mainSplit);
        Grid.SetRow(mainSplit, 1);
        rootGrid.Children.Add(footer);
        Grid.SetRow(footer, 2);

        return rootGrid;
    }

    // ── Remote / Branch picker dialog ──

    public static Task<(string Remote, string Branch)?> ShowRemoteBranchPickerDialogAsync(
        AppContext context, ManagedWindow owner, string? currentRemote, string? currentBranch, GitClientViewModel vm)
        => context.ShowDialogAsync<(string Remote, string Branch)?>(owner,
            LocalizedText.Format("git.dialog.push.dialog_title_format", currentRemote ?? LocalizedText.Get("git.dialog.push.not_selected")),
            dialog => BuildRemoteBranchPickerDialog(currentRemote, currentBranch, vm, dialog),
            new RemoteOS.Core.Primitives.Size(460, 320));

    private static Control BuildRemoteBranchPickerDialog(string? currentRemote, string? currentBranch,
        GitClientViewModel vm, ModalDialog<(string Remote, string Branch)?> dialog)
    {
        var remoteBox = new ComboBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.push.remote_placeholder"),
            ItemsSource = vm.Remotes.Select(r => r.Name).ToList(),
        };
        if (!string.IsNullOrWhiteSpace(currentRemote))
        {
            var match = vm.Remotes.FirstOrDefault(r => r.Name == currentRemote);
            if (match is not null) remoteBox.SelectedItem = match.Name;
            else remoteBox.Text = currentRemote;
        }
        else if (vm.Remotes.Count > 0)
        {
            remoteBox.SelectedIndex = 0;
        }

        var branchBox = new TextBox
        {
            PlaceholderText = LocalizedText.Get("git.dialog.push.branch_placeholder"),
            Text = currentBranch ?? string.Empty,
        };

        var statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush.Parse("#888"),
            Text = LocalizedText.Get("git.dialog.push.load_branches_failed"),
            IsVisible = false,
        };

        // Try to load remote branches
        _ = LoadRemoteBranchesAsync(vm, remoteBox, branchBox, statusText);

        var confirmBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.tracking.set_btn"),
            Background = Brush.Parse("#1C3765"),
            Foreground = Brushes.White,
            Padding = new Thickness(14, 6),
        };
        confirmBtn.Click += (_, _) =>
        {
            var remote = remoteBox.SelectedItem as string ?? remoteBox.Text;
            var branch = branchBox.Text;
            if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(branch)) return;
            dialog.Close((remote.Trim(), branch.Trim()));
        };

        var cancelBtn = new Button
        {
            Content = LocalizedText.Get("git.dialog.tracking.cancel"),
            Padding = new Thickness(14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        var result = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = LocalizedText.Get("git.dialog.push.select_remote"),
                    FontSize = 13,
                },
                remoteBox,
                new TextBlock
                {
                    Text = LocalizedText.Get("git.dialog.push.select_branch"),
                    FontSize = 13,
                },
                branchBox,
                statusText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { confirmBtn, cancelBtn },
                },
            },
        };

        return result;
    }

    private static async Task LoadRemoteBranchesAsync(GitClientViewModel vm, ComboBox remoteBox, TextBox branchBox, TextBlock statusText)
    {
        if (vm.SelectedRepository is null) return;
        try
        {
            var branches = await vm.GetRemoteBranchNamesAsync();
            if (branches.Count > 0)
            {
                branchBox.ItemsSource = branches;
                branchBox.IsReadOnly = false;
                statusText.IsVisible = false;
            }
        }
        catch
        {
            statusText.IsVisible = true;
        }
    }
}
