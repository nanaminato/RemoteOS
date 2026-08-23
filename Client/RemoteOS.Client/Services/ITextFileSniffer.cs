using Client.Apps.Explorer;

namespace Client.Services;

/// <summary>
/// 嗅探远程文件是否为文本。仅在前 N KB 字节上做内容判断，不下载完整文件。
/// 由桌面层在没有任何应用显式声明支持该扩展名时按需调用，作为把
/// <see cref="RemoteOS.Core.Applications.ApplicationManifest.SupportsTextFiles"/>
/// 应用加入"打开方式"候选的前置条件。
/// </summary>
public interface ITextFileSniffer
{
    /// <summary>返回 <c>true</c> 当远程文件被判定为文本（可被文本编辑器安全打开）。</summary>
    Task<bool> IsTextFileAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// 默认实现：通过 <see cref="IExplorerClient.DownloadAsync"/> 读取文件首 8KB，
/// 应用经典 binary-detection 算法：
/// <list type="bullet">
/// <item>UTF-8 / UTF-16 / UTF-32 BOM → 文本。</item>
/// <item>包含 NULL 字节 (0x00) 且不是 UTF-16/32 BOM 开头 → 二进制。</item>
/// <item>可打印 ASCII + 常见空白字符占比 &lt; 70% → 二进制。</item>
/// <item>其余情况 → 文本。</item>
/// </list>
/// </summary>
public sealed class TextFileSniffer(IExplorerClient files) : ITextFileSniffer
{
    private const int SniffByteCount = 8 * 1024;

    public async Task<bool> IsTextFileAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        Stream? stream = null;
        try
        {
            var download = await files.DownloadAsync(path, ct);
            if (download is null) return false;
            stream = download.Value.Stream;
            var buffer = new byte[Math.Min(SniffByteCount, 4096)];
            var total = 0;
            int read;
            while (total < SniffByteCount && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total == buffer.Length && total < SniffByteCount)
                    Array.Resize(ref buffer, Math.Min(SniffByteCount, buffer.Length * 2));
            }
            if (total == 0) return true; // 空文件视为空文本。
            return IsTextContent(buffer, total);
        }
        catch
        {
            // 任何 IO/网络错误都视为"无法确认是文本"，保守返回 false 以避免误打开二进制。
            return false;
        }
        finally
        {
            await (stream?.DisposeAsync() ?? ValueTask.CompletedTask);
        }
    }

    private static bool IsTextContent(byte[] buffer, int length)
    {
        // BOM 判定。
        if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return true;       // UTF-8
        if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return true;                              // UTF-16 LE
        if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) return true;                              // UTF-16 BE
        if (length >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF) return true; // UTF-32 BE
        if (length >= 4 && buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00) return true; // UTF-32 LE

        int printable = 0;
        bool hasNull = false;
        for (var i = 0; i < length; i++)
        {
            var b = buffer[i];
            if (b == 0x00) { hasNull = true; break; }
            // 可打印 ASCII (0x20–0x7E) + 常见空白与控制字符。
            if ((b >= 0x20 && b <= 0x7E) || b == '\t' || b == '\n' || b == '\r' || b == '\f' || b == 0x0B) printable++;
            // 高位字节可能是 UTF-8 多字节序列起始字节，宽松计为"可能是文本"。
            else if (b >= 0xC2) printable++;
        }
        if (hasNull) return false;
        return printable >= (length * 7 / 10); // ≥ 70% 视为文本。
    }
}
