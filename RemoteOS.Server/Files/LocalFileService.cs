// 目录枚举逻辑移植自 Jaya FileSystemService.GetDirectoryAsync（BSD-3）。
// Copyright (c) 2020, Rubal Walia. 原始许可见 Apps/Explorer/LICENSE-jaya.txt 与 THIRD_PARTY_NOTICES.md。
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using RemoteOS.Protocol.Files;

namespace Server.Files;

/// <summary>宿主 OS 本地文件系统服务。移植自 Jaya <c>FileSystemService.GetDirectoryAsync</c> 的枚举逻辑，
/// 扩展 create/delete/rename/move/copy/upload/download 操作。以宿主 OS 进程身份运行，复用宿主用户/权限。
/// 平台感知：Windows 列盘符；Linux 返回单条 "/" 根。<see cref="UnauthorizedAccessException"/> 在列举时吞并（部分目录不可访问不应导致整列失败）。</summary>
public sealed class LocalFileService : IFileService
{
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public IReadOnlyList<DriveDto> GetDrives()
    {
        var list = new List<DriveDto>();
        foreach (var d in DriveInfo.GetDrives())
        {
            long? total = d.IsReady ? d.TotalSize : null;
            list.Add(new DriveDto(
                Name: d.Name,
                Path: d.RootDirectory.FullName,
                TotalSize: total,
                IsReady: d.IsReady));
        }
        return list;
    }

    public IReadOnlyList<SpecialLocationDto> GetSpecialLocations()
    {
        // 跨平台获取家目录：Environment.SpecialFolder.UserProfile 在 Linux 上由 .NET 运行时映射到 $HOME，
        // 但 headless/服务进程可能未设置 → 回退读 HOME 环境变量。
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        if (string.IsNullOrEmpty(home))
            return Array.Empty<SpecialLocationDto>();

        // 候选列表：(协议枚举, 显示名, 路径)。
        // Downloads 不在 SpecialFolder 枚举中，手动拼接 $HOME/Downloads（Linux 也可读 $XDG_DOWNLOAD_DIR，但 $HOME/Downloads 是合理默认）。
        var candidates = new[]
        {
            (SpecialFolderKind.Home,      "主目录", home),
            (SpecialFolderKind.Desktop,   "桌面",   Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            (SpecialFolderKind.Documents, "文档",   Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            (SpecialFolderKind.Downloads, "下载",   System.IO.Path.Combine(home, "Downloads")),
            (SpecialFolderKind.Pictures,  "图片",   Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            (SpecialFolderKind.Music,      "音乐",   Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            (SpecialFolderKind.Videos,     "视频",   Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };

        var list = new List<SpecialLocationDto>();
        foreach (var (kind, name, path) in candidates)
        {
            // 仅返回真实存在的目录；headless Linux 上 Downloads/Pictures 等可能缺失，过滤后不返回。
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                list.Add(new SpecialLocationDto(kind, name, path));
        }
        return list;
    }

    public DirectoryDto GetDirectory(string? path)
    {
        // path 为空：返回盘符根聚合视图
        if (string.IsNullOrWhiteSpace(path))
        {
            if (IsLinux)
            {
                // Linux：把 "/" 根当作一个目录列举返回
                return ListDirectory("/");
            }

            // Windows：顶层 directories = 各盘符（与 Jaya 一致）
            var dirs = new List<FileSystemEntryDto>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                dirs.Add(new FileSystemEntryDto(
                    Path: d.RootDirectory.FullName,
                    Name: d.Name,
                    Size: d.TotalSize,
                    Type: FileSystemEntryType.Drive,
                    Created: null,
                    Modified: null,
                    Accessed: null,
                    IsHidden: false,
                    IsSystem: false));
            }
            return new DirectoryDto(
                Path: string.Empty,
                Name: "Computer",
                Type: FileSystemEntryType.Directory,
                Directories: dirs,
                Files: Array.Empty<FileEntryDto>(),
                Created: null,
                Modified: null);
        }

        return ListDirectory(path);
    }

    private static DirectoryDto ListDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        var dirs = new List<FileSystemEntryDto>();
        var files = new List<FileEntryDto>();

        // 子目录
        try
        {
            foreach (var di in info.EnumerateDirectories())
            {
                dirs.Add(new FileSystemEntryDto(
                    Path: di.FullName,
                    Name: di.Name,
                    Size: null,
                    Type: FileSystemEntryType.Directory,
                    Created: di.CreationTimeUtc,
                    Modified: di.LastWriteTimeUtc,
                    Accessed: di.LastAccessTimeUtc,
                    IsHidden: di.Attributes.HasFlag(FileAttributes.Hidden),
                    IsSystem: di.Attributes.HasFlag(FileAttributes.System)));
            }
        }
        catch (UnauthorizedAccessException) { /* 部分子目录不可访问：跳过 */ }
        catch (DirectoryNotFoundException) { throw; }

        // 文件
        try
        {
            foreach (var fi in info.EnumerateFiles())
            {
                var ext = string.IsNullOrEmpty(fi.Extension) ? null : fi.Extension.Substring(1).ToLowerInvariant();
                files.Add(new FileEntryDto(
                    Path: fi.FullName,
                    Name: fi.Name,
                    Extension: ext,
                    Size: fi.Length,
                    Created: fi.CreationTimeUtc,
                    Modified: fi.LastWriteTimeUtc,
                    Accessed: fi.LastAccessTimeUtc,
                    IsHidden: fi.Attributes.HasFlag(FileAttributes.Hidden),
                    IsSystem: fi.Attributes.HasFlag(FileAttributes.System)));
            }
        }
        catch (UnauthorizedAccessException) { }

        return new DirectoryDto(
            Path: info.FullName,
            Name: info.Name,
            Type: FileSystemEntryType.Directory,
            Directories: dirs,
            Files: files,
            Created: info.CreationTimeUtc,
            Modified: info.LastWriteTimeUtc);
    }

    public FileSystemEntryDto? GetInfo(string path)
    {
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            return new FileSystemEntryDto(
                Path: di.FullName,
                Name: di.Name,
                Size: null,
                Type: FileSystemEntryType.Directory,
                Created: di.CreationTimeUtc,
                Modified: di.LastWriteTimeUtc,
                Accessed: di.LastAccessTimeUtc,
                IsHidden: di.Attributes.HasFlag(FileAttributes.Hidden),
                IsSystem: di.Attributes.HasFlag(FileAttributes.System));
        }
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            var ext = string.IsNullOrEmpty(fi.Extension) ? null : fi.Extension.Substring(1).ToLowerInvariant();
            return new FileSystemEntryDto(
                Path: fi.FullName,
                Name: fi.Name,
                Size: fi.Length,
                Type: FileSystemEntryType.File,
                Created: fi.CreationTimeUtc,
                Modified: fi.LastWriteTimeUtc,
                Accessed: fi.LastAccessTimeUtc,
                IsHidden: fi.Attributes.HasFlag(FileAttributes.Hidden),
                IsSystem: fi.Attributes.HasFlag(FileAttributes.System));
        }
        return null;
    }

    public (Stream Stream, string ContentType, string FileName)? OpenRead(string path)
    {
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        return (fi.OpenRead(), "application/octet-stream", fi.Name);
    }

    public FileEntryDto WriteFile(string path, Stream content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Target directory does not exist: {directory}");

        using (var output = File.Create(path))
            content.CopyTo(output);

        return ToFileEntry(new FileInfo(path));
    }

    public FilePropertiesDto? GetProperties(string path)
    {
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return new FilePropertiesDto(directory.FullName, directory.Name, FileSystemEntryType.Directory,
                null, directory.CreationTimeUtc, directory.LastWriteTimeUtc, directory.LastAccessTimeUtc,
                GetPermissions(path, directory.Attributes), directory.Attributes.ToString());
        }

        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return new FilePropertiesDto(file.FullName, file.Name, FileSystemEntryType.File,
                file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, file.LastAccessTimeUtc,
                GetPermissions(path, file.Attributes), file.Attributes.ToString());
        }

        return null;
    }

    public void CreateDirectory(string path)
    {
        // Directory.CreateDirectory 对已存在目录是 no-op；为产生 409 我们先检查
        if (Directory.Exists(path))
            throw new IOException($"目录已存在: {path}");
        Directory.CreateDirectory(path);
    }

    public void Delete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
        else
            throw new FileNotFoundException($"路径不存在: {path}", path);
    }

    public FileSystemEntryDto Rename(string sourcePath, string newName)
    {
        var info = GetInfo(sourcePath) ?? throw new FileNotFoundException($"源路径不存在: {sourcePath}", sourcePath);
        var parent = System.IO.Path.GetDirectoryName(sourcePath);
        var dest = string.IsNullOrEmpty(parent)
            ? newName
            : System.IO.Path.Combine(parent, newName);

        if (Directory.Exists(sourcePath))
        {
            var di = new DirectoryInfo(sourcePath);
            di.MoveTo(dest);
        }
        else
        {
            File.Move(sourcePath, dest);
        }
        return GetInfo(dest)!;
    }

    public FileSystemEntryDto Move(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!Exists(sourcePath))
            throw new FileNotFoundException($"源路径不存在: {sourcePath}", sourcePath);
        if (Exists(destinationPath) && !overwrite)
            throw new IOException($"目标已存在: {destinationPath}");

        if (Directory.Exists(sourcePath))
        {
            if (Exists(destinationPath) && overwrite) Directory.Delete(destinationPath, recursive: true);
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath, overwrite);
        }
        return GetInfo(destinationPath)!;
    }

    public FileSystemEntryDto Copy(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!Exists(sourcePath))
            throw new FileNotFoundException($"源路径不存在: {sourcePath}", sourcePath);
        if (Exists(destinationPath) && !overwrite)
            throw new IOException($"目标已存在: {destinationPath}");

        if (Directory.Exists(sourcePath))
        {
            // DirectoryInfo.CreateSubdirectory 不支持覆盖；CopyTo 同样不支持 overwrite 参数语义，这里手动实现
            if (Exists(destinationPath) && overwrite) Directory.Delete(destinationPath, recursive: true);
            CopyDirectory(sourcePath, destinationPath);
        }
        else
        {
            File.Copy(sourcePath, destinationPath, overwrite);
        }
        return GetInfo(destinationPath)!;
    }

    public FileEntryDto Upload(string targetDirectoryPath, string fileName, Stream content)
    {
        if (!Directory.Exists(targetDirectoryPath))
            throw new DirectoryNotFoundException($"目标目录不存在: {targetDirectoryPath}");
        var dest = System.IO.Path.Combine(targetDirectoryPath, fileName);
        using (var fs = File.Create(dest))
        {
            content.CopyTo(fs);
        }
        return ToFileEntry(new FileInfo(dest));
    }

    private static FileEntryDto ToFileEntry(FileInfo file)
    {
        var ext = string.IsNullOrEmpty(file.Extension) ? null : file.Extension[1..].ToLowerInvariant();
        return new FileEntryDto(file.FullName, file.Name, ext, file.Length, file.CreationTimeUtc,
            file.LastWriteTimeUtc, file.LastAccessTimeUtc, file.Attributes.HasFlag(FileAttributes.Hidden),
            file.Attributes.HasFlag(FileAttributes.System));
    }

    private static string GetPermissions(string path, FileAttributes attributes)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                FileSystemSecurity security = Directory.Exists(path)
                    ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                    : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
                return "Windows ACL (SDDL): " + security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            }
            catch
            {
                return attributes.HasFlag(FileAttributes.ReadOnly) ? "Read-only" : "Read/write";
            }
        }

        return File.GetUnixFileMode(path).ToString();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void CopyDirectory(string source, string destination)
    {
        var src = new DirectoryInfo(source);
        Directory.CreateDirectory(destination);
        foreach (var fi in src.EnumerateFiles())
            fi.CopyTo(System.IO.Path.Combine(destination, fi.Name), overwrite: false);
        foreach (var di in src.EnumerateDirectories())
            CopyDirectory(di.FullName, System.IO.Path.Combine(destination, di.Name));
    }
}
