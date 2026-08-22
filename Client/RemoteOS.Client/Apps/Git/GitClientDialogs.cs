using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
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

    // ── 项目选择 / 探测 / 远程管理 ──

    /// <summary>Confirms whether to initialize a Git repository at the supplied path.</summary>
    public static async Task<bool> ShowInitConfirmAsync(AppContext context, ManagedWindow owner, string path)
    {
        var result = await context.ShowDialogAsync<bool?>(owner, "初始化 Git 仓库",
            dialog => BuildInitConfirmDialog(path, dialog),
            new RemoteOS.Core.Primitives.Size(440, 220));
        return result ?? false;
    }

    /// <summary>Prompts the user for a new/edited remote. <paramref name="existing"/> null = add; non-null = edit.</summary>
    public static Task<GitRemoteRequest?> ShowRemoteDialogAsync(AppContext context, ManagedWindow owner, GitRemoteDto? existing)
        => context.ShowDialogAsync<GitRemoteRequest?>(owner, existing is null ? "添加远程仓库" : $"编辑远程仓库 - {existing.Name}",
            dialog => BuildRemoteDialog(existing, dialog),
            new RemoteOS.Core.Primitives.Size(480, 240));

    /// <summary>Prompts for a new branch name when renaming. Returns null on cancel.</summary>
    public static Task<string?> ShowRenameBranchDialogAsync(AppContext context, ManagedWindow owner, GitBranchDto branch)
        => context.ShowDialogAsync<string?>(owner, $"重命名分支 - {branch.Name}",
            dialog => BuildRenameBranchDialog(branch, dialog),
            new RemoteOS.Core.Primitives.Size(420, 180));

    /// <summary>Prompts for merge strategy (merge/no-ff/ff-only/squash) + optional message. Returns null on cancel.</summary>
    public static Task<GitMergeRequest?> ShowMergeDialogAsync(AppContext context, ManagedWindow owner, GitBranchDto sourceBranch, GitClientViewModel vm)
        => context.ShowDialogAsync<GitMergeRequest?>(owner, $"合并分支 - {sourceBranch.Name}",
            dialog => BuildMergeDialog(sourceBranch, vm, dialog),
            new RemoteOS.Core.Primitives.Size(460, 340));

    /// <summary>Prompts for an upstream (remote/branch) to track, or choose "Unset". Returns null on cancel.</summary>
    public static Task<GitBranchTrackingRequest?> ShowSetTrackingDialogAsync(AppContext context, ManagedWindow owner, GitBranchDto localBranch, GitClientViewModel vm)
        => context.ShowDialogAsync<GitBranchTrackingRequest?>(owner, $"设置跟踪分支 - {localBranch.Name}",
            dialog => BuildSetTrackingDialog(localBranch, vm, dialog),
            new RemoteOS.Core.Primitives.Size(460, 260));

    // ── Dialog builders (programmatic — no separate AXAML files needed) ──

    private static Control BuildInitConfirmDialog(string path, ModalDialog<bool?> dialog)
    {
        var msg = new TextBlock
        {
            Text = $"所选目录不是 Git 仓库：\n{path}\n\n是否在此目录初始化一个新的 Git 仓库？",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };
        var initBtn = new Button { Content = "🔧 初始化", Background = Brush.Parse("#1C3765"), Foreground = Brushes.White, Padding = new(14, 6) };
        initBtn.Click += (_, _) => dialog.Close(true);
        var cancelBtn = new Button { Content = "取消", Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
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
            PlaceholderText = "origin / upstream …",
            Text = existing?.Name ?? string.Empty,
        };
        var urlBox = new TextBox
        {
            PlaceholderText = "https://example.com/repo.git 或 git@example.com:repo.git",
            Text = existing?.FetchUrl ?? string.Empty,
        };
        var pushBox = new TextBox
        {
            PlaceholderText = "（可选，与 fetch 相同时留空）",
            Text = existing?.PushUrl ?? string.Empty,
        };

        var saveBtn = new Button
        {
            Content = existing is null ? "添加" : "保存",
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

        var cancelBtn = new Button { Content = "取消", Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "名称", FontSize = 13 },
                nameBox,
                new TextBlock { Text = "Fetch URL", FontSize = 13 },
                urlBox,
                new TextBlock { Text = "Push URL（可选）", FontSize = 13 },
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

    // ── 旧的对话框构建器（沿用现有实现）──

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

    // ── 分支管理对话框构建器 ──

    private static Control BuildRenameBranchDialog(GitBranchDto branch, ModalDialog<string?> dialog)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "输入新分支名称…",
            Text = branch.Name,
        };
        nameBox.SelectAll();

        var renameBtn = new Button
        {
            Content = "重命名",
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

        var cancelBtn = new Button { Content = "取消", Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"当前分支：{branch.Name}{(branch.IsCurrent ? "（当前）" : "")}",
                    FontSize = 13,
                    Foreground = Brush.Parse("#666"),
                },
                new TextBlock { Text = "新分支名称", FontSize = 13 },
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
        var currentBranchName = vm.Status?.Branch ?? "(未知)";

        var mergeRadio = new RadioButton { Content = "默认 Merge (merge)", GroupName = "strategy", IsChecked = true };
        var noFfRadio = new RadioButton { Content = "强制生成合并提交 (no-ff)", GroupName = "strategy" };
        var ffOnlyRadio = new RadioButton { Content = "仅快进 (ff-only)", GroupName = "strategy" };
        var squashRadio = new RadioButton { Content = "压缩合并 (squash, 不自动提交)", GroupName = "strategy" };

        var noCommitCheck = new CheckBox
        {
            Content = "不自动提交 (--no-commit)",
        };

        var messageBox = new TextBox
        {
            PlaceholderText = "合并提交消息（可选，留空使用默认）",
            MinHeight = 70,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
        };

        var mergeBtn = new Button
        {
            Content = "合并",
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

        var cancelBtn = new Button { Content = "取消", Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"将「{sourceBranch.Name}」合并到当前分支「{currentBranchName}」",
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock { Text = "合并策略", FontSize = 13, Margin = new Thickness(0, 4, 0, 0) },
                new StackPanel
                {
                    Spacing = 6,
                    Children = { mergeRadio, noFfRadio, ffOnlyRadio, squashRadio },
                },
                noCommitCheck,
                new TextBlock { Text = "合并提交消息（可选）", FontSize = 13 },
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
        var currentTracking = localBranch.Tracking ?? "(未设置)";

        var unsetRadio = new RadioButton { Content = "取消跟踪 (--unset-upstream)", GroupName = "tracking" };
        var autoRadio = new RadioButton { Content = $"自动推断 (origin/{localBranch.Name})", GroupName = "tracking", IsChecked = true };
        var customRadio = new RadioButton { Content = "自定义远程 / 分支", GroupName = "tracking" };

        var remotes = vm.Remotes.ToList();
        var remoteBox = new ComboBox
        {
            PlaceholderText = "选择远程仓库…",
            ItemsSource = remotes.Select(r => r.Name).ToList(),
        };
        if (remotes.Count > 0) remoteBox.SelectedIndex = 0;

        var branchBox = new TextBox
        {
            PlaceholderText = "远程分支名称…",
            Text = localBranch.Name,
        };

        // 自定义区域容器：选中 custom 时启用
        var customPanel = new StackPanel
        {
            IsEnabled = false,
            Spacing = 8,
        };
        var label1 = new TextBlock { Text = "远程仓库", FontSize = 13 };
        var label2 = new TextBlock { Text = "远程分支", FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
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
            Content = "设置",
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

        var cancelBtn = new Button { Content = "取消", Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"本地分支：{localBranch.Name}{(localBranch.IsCurrent ? "（当前）" : "")}",
                    FontSize = 13,
                    Foreground = Brush.Parse("#666"),
                },
                new TextBlock
                {
                    Text = $"当前跟踪：{currentTracking}",
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
}
