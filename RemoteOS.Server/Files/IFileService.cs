using RemoteOS.Protocol.Files;

namespace Server.Files;

/// <summary>文件管理服务：以宿主 OS 进程身份执行 IO（复用宿主用户/权限，不另建 ACL——见 project_memory 硬约束）。
/// 实现方负责平台感知（Windows 盘符 / Linux "/" 根）与 <see cref="UnauthorizedAccessException"/> 吞并。
/// 移植自 Jaya FileSystemService 的目录列举逻辑并扩展为完整文件操作。</summary>
public interface IFileService
{
    /// <summary>列举驱动器/根挂载点。Windows 返回 C:\ 等盘符；Linux 返回单条 "/"。</summary>
    IReadOnlyList<DriveDto> GetDrives();

    /// <summary>列举目录内容（目录自身元数据 + 子目录列表 + 文件列表）。
    /// path 为空/未提供时返回盘符根的聚合视图（Windows 顶层为各盘符目录项，Linux 为 "/" 根目录列举）。</summary>
    DirectoryDto GetDirectory(string? path);

    /// <summary>获取单个条目元数据。不存在返回 null。</summary>
    FileSystemEntryDto? GetInfo(string path);

    /// <summary>打开文件用于下载。返回 (stream, contentType, fileName)。不存在返回 null。</summary>
    (Stream Stream, string ContentType, string FileName)? OpenRead(string path);

    /// <summary>创建目录。已存在时抛 <see cref="IOException"/>（端点映射 409 already-exists）。</summary>
    void CreateDirectory(string path);

    /// <summary>删除文件或目录（目录递归）。不存在抛 <see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/>。</summary>
    void Delete(string path);

    /// <summary>同目录内重命名。源不存在抛 <see cref="FileNotFoundException"/>。</summary>
    FileSystemEntryDto Rename(string sourcePath, string newName);

    /// <summary>移动（可跨目录）。目标存在且 overwrite=false 时抛 <see cref="IOException"/>（端点映射 409）。</summary>
    FileSystemEntryDto Move(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>复制（可跨目录）。目标存在且 overwrite=false 时抛 <see cref="IOException"/>（端点映射 409）。</summary>
    FileSystemEntryDto Copy(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>上传文件到目标目录。返回新建文件条目。</summary>
    FileEntryDto Upload(string targetDirectoryPath, string fileName, Stream content);
}
