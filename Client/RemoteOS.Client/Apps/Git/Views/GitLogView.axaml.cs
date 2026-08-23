using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Client.Localization;
using RemoteOS.Protocol.Git;

namespace Client.Apps.Git.Views;

/// <summary>三栏统一日志视图：左分支树 + 中提交列表 + 右文件变更+详情。
/// 在 Loaded 时为 DataContext（GitClientViewModel）的 Branches 构造树形视图模型，同时响应搜索文本过滤。
/// 右侧面板复用 VM 中 SelectedCommit → OnSelectedCommitChanged 加载 CommitDetail/CommitChangedFiles。</summary>
internal partial class GitLogView : UserControl
{
    public ObservableCollection<BranchTreeNode> BranchTreeRoots { get; } = [];
    public ObservableCollection<CommitFileTreeNode> CommitFileTreeRoots { get; } = [];

    private GitClientViewModel? _vm;
    private bool _branchesHandlerAttached;
    private bool _commitFilesHandlerAttached;
    private bool _commitTreeRebuildPending;
    private bool _attached;
    private readonly Dictionary<string, bool> _branchExpansionState = new(StringComparer.Ordinal);

    public GitLogView() => InitializeComponent();
    public GitLogView(GitClientViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_attached) return;
        _attached = true;
        if (_vm is null && DataContext is GitClientViewModel vm) _vm = vm;
        if (_vm is null) return;

        // 把 ViewModel 的 Branches 扁平列表映射为「HEAD / 本地 / 远程 / 每个 remote」 树形结构
        RebuildBranchTree();

        if (!_branchesHandlerAttached)
        {
            _vm.Branches.CollectionChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_attached) RebuildBranchTree();
            });
            _branchesHandlerAttached = true;
        }

        if (!_commitFilesHandlerAttached)
        {
            _vm.CommitChangedFiles.CollectionChanged += (_, _) => QueueCommitFileTreeRebuild();
            _commitFilesHandlerAttached = true;
        }

        // 搜索框变化时重建（过滤）分支树；Status 更新时 HEAD 文案可能变
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(GitClientViewModel.BranchSearchText)
                or nameof(GitClientViewModel.Status))
            {
                RebuildBranchTree();
            }
        };

        // 将 TreeView 绑定到本地树形集合（XAML 默认扁平，这里替换为分组树）
        BranchTree.ItemsSource = BranchTreeRoots;
        ChangedFileTree.ItemsSource = CommitFileTreeRoots;
        RebuildCommitFileTree();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _attached = false;
    }

    private void RebuildBranchTree()
    {
        if (_vm is null) return;
        CaptureBranchExpansionState();
        var filter = (_vm.BranchSearchText ?? string.Empty).Trim();
        bool Pass(string s) => string.IsNullOrEmpty(filter)
            || s.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var all = _vm.Branches.ToList();
        var currentBranchName = _vm.Status?.Branch;

        BranchTreeRoots.Clear();

        // 1. HEAD 伪节点
        var head = all.FirstOrDefault(b => b.IsCurrent);
        if (head is not null)
        {
            BranchTreeRoots.Add(new BranchTreeNode(
                displayName: string.IsNullOrEmpty(currentBranchName)
                    ? LocalizedText.Format("git.log.head_format", head.Name)
                    : LocalizedText.Format("git.log.head_current_branch_format", head.Name),
                icon: "⭐",
                foreground: "#122344",
                fontWeight: FontWeight.SemiBold,
                badge: string.Empty,
                branch: head,
                nodeKind: BranchNodeKind.Head));
        }
        else if (!string.IsNullOrEmpty(currentBranchName))
        {
            BranchTreeRoots.Add(new BranchTreeNode(
                displayName: LocalizedText.Format("git.log.head_current_branch_format", currentBranchName),
                icon: "⭐",
                foreground: "#122344",
                fontWeight: FontWeight.SemiBold,
                nodeKind: BranchNodeKind.Head));
        }

        // 2. 本地分支分组
        var locals = all.Where(b => !b.IsRemote).ToList();
        var localGroup = new BranchTreeNode(
            LocalizedText.Get("git.log.branch_tree_local"),
            icon: "📁",
            nodeKind: BranchNodeKind.Group,
            isExpanded: GetBranchExpansionState("local", defaultValue: true));
        foreach (var b in locals)
        {
            if (!Pass(b.Name)) continue;
            var fg = b.IsCurrent ? "#0A6F3E" : "#122344";
            var weight = b.IsCurrent ? FontWeight.SemiBold : FontWeight.Normal;
            var icon = b.IsCurrent ? "⭐" : "🌱";
            var badge = b.Ahead > 0 || b.Behind > 0
                ? $"{(b.Ahead > 0 ? $"↑{b.Ahead}" : "")}{(b.Behind > 0 ? $"↓{b.Behind}" : "")}"
                : string.Empty;
            localGroup.Children.Add(new BranchTreeNode(
                displayName: b.Name,
                icon: icon,
                foreground: fg,
                fontWeight: weight,
                badge: badge,
                branch: b,
                nodeKind: BranchNodeKind.LocalBranch,
                canDelete: !b.IsCurrent,
                isBranchAndNotCurrent: !b.IsCurrent,
                currentBranchName: currentBranchName));
        }
        BranchTreeRoots.Add(localGroup);

        // 3. 远程分组：按 remote 名（remote/name 中 slash 前）分组
        var remotes = all.Where(b => b.IsRemote).ToList();
        var remoteRoot = new BranchTreeNode(
            LocalizedText.Get("git.log.branch_tree_remote"),
            icon: "🌐",
            nodeKind: BranchNodeKind.Group,
            isExpanded: GetBranchExpansionState("remotes", defaultValue: true));
        var remoteGroups = new Dictionary<string, BranchTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in remotes)
        {
            var slash = b.Name.IndexOf('/');
            var remoteName = slash > 0 ? b.Name[..slash] : "origin";
            var shortName = slash > 0 ? b.Name[(slash + 1)..] : b.Name;
            if (!Pass(remoteName) && !Pass(b.Name) && !Pass(shortName)) continue;

            if (!remoteGroups.TryGetValue(remoteName, out var rg))
            {
                rg = new BranchTreeNode(
                    remoteName,
                    icon: "📂",
                    nodeKind: BranchNodeKind.RemoteGroup,
                    isExpanded: GetBranchExpansionState($"remote:{remoteName}", defaultValue: true));
                remoteGroups[remoteName] = rg;
                remoteRoot.Children.Add(rg);
            }
            rg.Children.Add(new BranchTreeNode(
                displayName: shortName,
                icon: "🔗",
                foreground: "#36506F",
                fontWeight: FontWeight.Normal,
                badge: string.Empty,
                branch: b,
                nodeKind: BranchNodeKind.RemoteBranch,
                canDelete: false,
                currentBranchName: currentBranchName));
        }
        BranchTreeRoots.Add(remoteRoot);
    }

    private void CaptureBranchExpansionState()
    {
        foreach (var root in BranchTreeRoots)
        {
            if (root.Kind == BranchNodeKind.Group && root.Icon == "📁")
                _branchExpansionState["local"] = root.IsExpanded;
            else if (root.Kind == BranchNodeKind.Group && root.Icon == "🌐")
            {
                _branchExpansionState["remotes"] = root.IsExpanded;
                foreach (var remote in root.Children.Where(node => node.Kind == BranchNodeKind.RemoteGroup))
                    _branchExpansionState[$"remote:{remote.DisplayName}"] = remote.IsExpanded;
            }
        }
    }

    private bool GetBranchExpansionState(string key, bool defaultValue)
        => _branchExpansionState.TryGetValue(key, out var isExpanded) ? isExpanded : defaultValue;

    private void QueueCommitFileTreeRebuild()
    {
        if (_commitTreeRebuildPending) return;
        _commitTreeRebuildPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _commitTreeRebuildPending = false;
            if (_attached) RebuildCommitFileTree();
        });
    }

    private void RebuildCommitFileTree()
    {
        if (_vm is null) return;

        _vm.SelectedCommitFile = null;
        CommitFileTreeRoots.Clear();
        var directories = new Dictionary<string, CommitFileTreeNode>(StringComparer.Ordinal);

        foreach (var file in _vm.CommitChangedFiles.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            var segments = file.Path
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0) continue;

            var parent = CommitFileTreeRoots;
            var directoryPath = string.Empty;
            for (var index = 0; index < segments.Length; index++)
            {
                var isFile = index == segments.Length - 1;
                if (isFile)
                {
                    parent.Add(new CommitFileTreeNode(segments[index], file));
                    continue;
                }

                directoryPath = string.IsNullOrEmpty(directoryPath)
                    ? segments[index]
                    : $"{directoryPath}/{segments[index]}";
                if (!directories.TryGetValue(directoryPath, out var directory))
                {
                    directory = new CommitFileTreeNode(segments[index]);
                    directories[directoryPath] = directory;
                    parent.Add(directory);
                }
                parent = directory.Children;
            }
        }
    }

    private void ChangedFileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.SelectedCommitFile = (ChangedFileTree.SelectedItem as CommitFileTreeNode)?.File;
    }
}

/// <summary>分支/分组树节点（用于 TreeView）。</summary>
public sealed class BranchTreeNode
{
    public string DisplayName { get; }
    public string Icon { get; }
    public string Foreground { get; }
    public FontWeight FontWeight { get; }
    public string Badge { get; }
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public GitBranchDto? Branch { get; }
    public BranchNodeKind Kind { get; }
    public bool CanDelete { get; }
    public bool IsBranchAndNotCurrent { get; }
    public bool IsExpanded { get; set; }

    public bool IsBranch => Kind is BranchNodeKind.LocalBranch or BranchNodeKind.RemoteBranch or BranchNodeKind.Head;
    public bool IsLocalBranch => Kind == BranchNodeKind.LocalBranch;
    public bool IsRemoteBranch => Kind == BranchNodeKind.RemoteBranch;
    public bool IsActionableBranch => Kind is BranchNodeKind.LocalBranch or BranchNodeKind.RemoteBranch;
    public bool IsCurrentLocalBranch => IsLocalBranch && Branch?.IsCurrent == true;
    public bool CanCheckout => IsActionableBranch && !IsCurrentLocalBranch;
    public bool CanCheckoutAndUpdate => IsLocalBranch && !IsCurrentLocalBranch;
    public bool CanMergeIntoCurrent => IsActionableBranch && !IsCurrentLocalBranch;
    public bool CanPush => IsCurrentLocalBranch;
    public bool CanUpdate => IsCurrentLocalBranch || IsRemoteBranch;
    public bool CanRename => IsLocalBranch;
    public string NewBranchMenuText => IsActionableBranch
        ? LocalizedText.Format("git.log.menu.create_branch_from_format", Branch!.Name)
        : LocalizedText.Get("git.log.menu.new_branch");
    public string CompareMenuText => LocalizedText.Format("git.log.menu.compare_with_current_format",
        string.IsNullOrWhiteSpace(CurrentBranchName) ? LocalizedText.Get("git.status.unknown_branch") : CurrentBranchName);
    public string MergeMenuText => LocalizedText.Format("git.log.menu.merge_into_current_format", Branch?.Name ?? DisplayName,
        string.IsNullOrWhiteSpace(CurrentBranchName) ? LocalizedText.Get("git.status.unknown_branch") : CurrentBranchName);
    public string PullMenuText => IsRemoteBranch
        ? LocalizedText.Format("git.log.menu.pull_remote_into_current_format", Branch?.Name ?? DisplayName,
            string.IsNullOrWhiteSpace(CurrentBranchName) ? LocalizedText.Get("git.status.unknown_branch") : CurrentBranchName)
        : LocalizedText.Get("git.log.menu.update");
    public string? CurrentBranchName { get; }

    public ObservableCollection<BranchTreeNode> Children { get; } = [];

    public BranchTreeNode(string displayName, string icon = "📄",
        string foreground = "#122344", FontWeight fontWeight = default, string badge = "",
        GitBranchDto? branch = null, BranchNodeKind nodeKind = BranchNodeKind.Item,
        bool canDelete = false, bool isBranchAndNotCurrent = false, bool isExpanded = false,
        string? currentBranchName = null)
    {
        DisplayName = displayName;
        Icon = icon;
        Foreground = foreground;
        FontWeight = fontWeight == default ? FontWeight.Normal : fontWeight;
        Badge = badge;
        Branch = branch;
        Kind = nodeKind;
        CanDelete = canDelete;
        IsBranchAndNotCurrent = isBranchAndNotCurrent;
        IsExpanded = isExpanded;
        CurrentBranchName = currentBranchName;
    }
}

public enum BranchNodeKind { Item, Group, Head, LocalBranch, RemoteBranch, RemoteGroup }

/// <summary>A node in the selected commit's changed-file tree. Directory nodes have no associated file.</summary>
public sealed class CommitFileTreeNode
{
    public CommitFileTreeNode(string name, GitFileChangeDto? file = null)
    {
        Name = name;
        File = file;
    }

    public string Name { get; }
    public GitFileChangeDto? File { get; }
    public bool IsFile => File is not null;
    public string Icon => IsFile ? "📄" : "📁";
    public string Status => File?.Status ?? string.Empty;
    public ObservableCollection<CommitFileTreeNode> Children { get; } = [];
}

/// <summary>GitLogView XAML 中用到的一组值转换器（单例，x:Static 引用）。</summary>
public static class GitLogConverters
{
    public static readonly IValueConverter StatusBgConverter = new FileStatusBgConverter();
    public static readonly IValueConverter StatusLabelConverter = new FileStatusLabelConverter();
    public static readonly IValueConverter BranchBadgesConverter = new BranchBadgesConverter();
}

public sealed class FileStatusBgConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string) ?? string.Empty;
        return s.ToLowerInvariant() switch
        {
            "added" => Brush.Parse("#1A7F37"),
            "modified" => Brush.Parse("#1F6FEB"),
            "deleted" => Brush.Parse("#CF222E"),
            "renamed" => Brush.Parse("#8250DF"),
            "copied" => Brush.Parse("#8250DF"),
            "untracked" => Brush.Parse("#6E7781"),
            "conflicted" => Brush.Parse("#D1242B"),
            _ => Brush.Parse("#6E7781"),
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FileStatusLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string) ?? string.Empty;
        return s.ToLowerInvariant() switch
        {
            "added" => "A",
            "modified" => "M",
            "deleted" => "D",
            "renamed" => "R",
            "copied" => "C",
            "untracked" => "U",
            "conflicted" => "!",
            _ => "·",
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把 GitStatusDto → 当前选中提交匹配的分支徽章列表（HEAD / 当前分支 / upstream）。
/// 注意：此处简化输出固定 3 个徽章（与参考截图一致），不依赖实际传入的提交，只看当前工作区状态。</summary>
public sealed class BranchBadgesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GitStatusDto st) return Array.Empty<BranchBadge>();
        var list = new List<BranchBadge>(capacity: 4);
        if (!string.IsNullOrEmpty(st.Branch))
            list.Add(new BranchBadge("🟡", st.Branch, "#FFF4D6", "#8A5A00"));
        if (!string.IsNullOrEmpty(st.Upstream))
            list.Add(new BranchBadge("🔷", st.Upstream, "#DCE9FF", "#1F4787"));
        // 如果上游形如 origin/master，则额外显示 master
        if (!string.IsNullOrEmpty(st.Upstream))
        {
            var slash = st.Upstream.IndexOf('/');
            if (slash > 0)
            {
                var localLike = st.Upstream[(slash + 1)..];
                list.Add(new BranchBadge("🟣", localLike, "#F0E0FF", "#6D2FA6"));
            }
        }
        return list;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed record BranchBadge(string Icon, string Label, string Bg, string Fg);
