using RemoteOS.Protocol.Browser;

namespace Client.Apps.Browser;

/// <summary>RemoteOS Server 浏览器书签/历史记录 HTTP 客户端抽象。typed HttpClient 实现（见 <see cref="BrowserClient"/>）。
/// 所有方法从 <c>IAuthSession</c> 取 <c>serverUrl</c> + <c>accessToken</c> 构造绝对 URI 与 Authorization 头。
/// 路由常量见 <see cref="BrowserApiRoutes"/>。错误统一为 <see cref="RemoteOsAuthException"/>（含 ProblemDetails）。</summary>
public interface IBrowserClient
{
    // ── bookmarks ──
    Task<IReadOnlyList<BookmarkDto>> ListBookmarksAsync(CancellationToken ct = default);
    Task<BookmarkDto> AddBookmarkAsync(string title, string url, CancellationToken ct = default);
    Task DeleteBookmarkAsync(Guid id, CancellationToken ct = default);
    Task ClearBookmarksAsync(CancellationToken ct = default);

    // ── history ──
    Task<IReadOnlyList<HistoryEntryDto>> ListHistoryAsync(int limit = 100, CancellationToken ct = default);
    Task<HistoryEntryDto> RecordVisitAsync(string title, string url, CancellationToken ct = default);
    Task DeleteHistoryAsync(Guid id, CancellationToken ct = default);
    Task ClearHistoryAsync(CancellationToken ct = default);
}
