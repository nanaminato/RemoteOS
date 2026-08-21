namespace Client.Apps.Git;

/// <summary>远程文件夹选择器中显示的条目（盘符或子目录）。</summary>
internal sealed record RemoteFolderEntry(string Name, string Path);
