using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Apps.Explorer;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Protocol.Files;
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

    /// <summary>Browses the remote file system (via IExplorerClient) and lets the user pick a server-side folder.
    /// Returns the absolute path, or null if cancelled.</summary>
    public static Task<string?> ShowRemotePathPickerAsync(AppContext context, ManagedWindow owner, IExplorerClient files)
        => context.ShowDialogAsync<string?>(owner, "选择项目文件夹",
            dialog => BuildRemotePathPicker(files, dialog),
            new RemoteOS.Core.Primitives.Size(640, 460));

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

    // ── Dialog builders (programmatic — no separate AXAML files needed) ──

    private static Control BuildRemotePathPicker(IExplorerClient files, ModalDialog<string?> dialog)
    {
        var pathBox = new TextBox
        {
            PlaceholderText = "/absolute/path/to/folder",
            MinHeight = 32,
        };
        var status = new TextBlock { FontSize = 11, Foreground = Brush.Parse("#888"), Text = "正在加载根目录…" };

        var list = new ListBox { MinHeight = 240, MaxHeight = 320 };
        list.DoubleTapped += async (_, _) =>
        {
            if (list.SelectedItem is RemoteFolderEntry entry)
            {
                pathBox.Text = entry.Path;
                await LoadDirectoryAsync(files, pathBox, status, list);
            }
        };

        var goBtn = new Button { Content = "进入", Padding = new(8, 2) };
        goBtn.Click += async (_, _) => await LoadDirectoryAsync(files, pathBox, status, list);

        var upBtn = new Button { Content = "↑ 上级", Padding = new(8, 2) };
        upBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(pathBox.Text))
            {
                await LoadDrivesAsync(files, list, status, pathBox);
                return;
            }
            // 计算上级目录：用平台无关的字符串处理
            var current = pathBox.Text.TrimEnd('\\').TrimEnd('/');
            var idx = current.LastIndexOfAny(['\\', '/']);
            var parent = idx < 0 ? null : current[..idx];
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                // 已到根：显示盘符列表
                await LoadDrivesAsync(files, list, status, pathBox);
                return;
            }
            pathBox.Text = parent;
            await LoadDirectoryAsync(files, pathBox, status, list);
        };

        var refreshBtn = new Button { Content = "⟳ 刷新", Padding = new(8, 2) };
        refreshBtn.Click += async (_, _) => await LoadDirectoryAsync(files, pathBox, status, list);

        var selectBtn = new Button { Content = "选择此文件夹", Background = Brush.Parse("#1C3765"), Foreground = Brushes.White, Padding = new(14, 6) };
        selectBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(pathBox.Text))
                dialog.Close(pathBox.Text!.Trim());
        };

        var cancelBtn = new Button { Content = "取消", Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        // 初始加载：默认进入用户家目录
        _ = InitializePickerAsync(files, pathBox, status, list);

        var addressBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { pathBox, goBtn, upBtn, refreshBtn },
        };
        addressBar.SetValue(Grid.RowProperty, 0);

        status.SetValue(Grid.RowProperty, 1);
        status.Margin = new(0, 4, 0, 4);

        list.SetValue(Grid.RowProperty, 2);
        list.Margin = new(0, 4, 0, 4);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 10, 0, 0),
            Spacing = 4,
            Children = { selectBtn, cancelBtn },
        };
        footer.SetValue(Grid.RowProperty, 3);

        return new Grid
        {
            Margin = new(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children = { addressBar, status, list, footer },
        };
    }

    private static async Task InitializePickerAsync(IExplorerClient files, TextBox pathBox, TextBlock status, ListBox list)
    {
        try
        {
            var specials = await files.GetSpecialLocationsAsync();
            var home = specials.FirstOrDefault(s => s.Kind == SpecialFolderKind.Home)
                       ?? specials.FirstOrDefault();
            if (home is not null)
                pathBox.Text = home.Path;
            await LoadDirectoryAsync(files, pathBox, status, list);
        }
        catch (Exception ex)
        {
            status.Text = $"加载家目录失败：{ex.Message}";
            await LoadDrivesAsync(files, list, status, pathBox);
        }
    }

    private static async Task LoadDirectoryAsync(IExplorerClient files, TextBox pathBox, TextBlock status, ListBox? list = null)
    {
        var path = pathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            if (list is not null) await LoadDrivesAsync(files, list, status, pathBox);
            return;
        }
        status.Text = $"正在加载 {path} …";
        try
        {
            var dir = await files.GetDirectoryAsync(path);
            list ??= new ListBox();
            // 只显示子目录（项目只允许选目录）；驱动器根也走 GetDirectoryAsync 时返回的 Directories 是子目录列表
            var folders = dir.Directories
                .Select(e => new RemoteFolderEntry(e.Name, e.Path))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            list.ItemsSource = folders;
            status.Text = $"共 {folders.Count} 个子目录";
        }
        catch (Exception ex)
        {
            status.Text = $"加载失败：{ex.Message}";
        }
    }

    private static async Task LoadDrivesAsync(IExplorerClient files, ListBox list, TextBlock status, TextBox pathBox)
    {
        status.Text = "正在加载驱动器列表…";
        try
        {
            var drives = await files.GetDrivesAsync();
            var entries = drives
                .Where(d => !string.IsNullOrEmpty(d.Path))
                .Select(d => new RemoteFolderEntry(d.Name, d.Path!))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            list.ItemsSource = entries;
            status.Text = $"共 {entries.Count} 个驱动器";
        }
        catch (Exception ex)
        {
            status.Text = $"加载驱动器失败：{ex.Message}";
        }
    }

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
}
