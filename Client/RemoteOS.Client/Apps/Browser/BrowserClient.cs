using System.Net.Http.Headers;
using System.Net.Http.Json;
using Client.Services.Auth;
using RemoteOS.Protocol.Browser;
using RemoteOS.Protocol.Common;

namespace Client.Apps.Browser;

/// <summary>IBrowserClient 的 typed HttpClient 实现。
/// 不 mutate HttpClient.BaseAddress，每个请求用 <see cref="IAuthSession.ServerUrl"/> 构造绝对 URI（避免共享实例并发竞态）。
/// Authorization 头从 <see cref="IAuthSession.Tokens"/> 取；未登录抛 <see cref="InvalidOperationException"/>。
/// 失败读 ProblemDetails 抛 <see cref="RemoteOsAuthException"/>（与 <see cref="RemoteOsClient"/> / ExplorerClient 同源）。</summary>
public sealed class BrowserClient : IBrowserClient
{
    private readonly HttpClient _http;
    private readonly IAuthSession _session;

    public BrowserClient(HttpClient http, IAuthSession session)
    {
        _http = http;
        _session = session;
    }

    public Task<BrowserSettingsDto> GetSettingsAsync(CancellationToken ct = default)
        => SendAsync<BrowserSettingsDto>(HttpMethod.Get, BrowserApiRoutes.Settings, ct: ct);

    public Task<BrowserSettingsDto> SaveSettingsAsync(BrowserSettingsDto settings, CancellationToken ct = default)
        => SendAsync<BrowserSettingsDto>(HttpMethod.Put, BrowserApiRoutes.Settings, body: settings, ct: ct);

    public Uri CreateLocalPortForwardingUri(Uri target)
    {
        if (!IsLoopbackTarget(target))
            throw new ArgumentException("Only http(s) localhost or 127.0.0.1 targets can be forwarded.", nameof(target));

        var serverUrl = RequireSession();
        var targetPath = target.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimStart('/');
        var route = $"{BrowserApiRoutes.LocalPortForwardingPrefix}/{target.Host}/{target.Scheme}/{target.Port}/{targetPath}";
        var token = Uri.EscapeDataString(_session.Tokens!.AccessToken);
        var targetQuery = target.Query.TrimStart('?');
        return new UriBuilder(new Uri(new Uri(serverUrl), route.TrimStart('/')))
        {
            Query = string.IsNullOrEmpty(targetQuery)
                ? $"{BrowserApiRoutes.LocalPortForwardingTokenQuery}={token}"
                : $"{BrowserApiRoutes.LocalPortForwardingTokenQuery}={token}&{targetQuery}"
        }.Uri;
    }

    public Uri? TryGetLocalPortForwardingTarget(Uri proxyUri)
    {
        var parts = proxyUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // api/v1/browser/local/{host}/{scheme}/{port}/{path...}
        if (parts.Length < 7 || !parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("browser", StringComparison.OrdinalIgnoreCase)
            || !parts[3].Equals("local", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[6], out var port))
            return null;

        var host = parts[4];
        var scheme = parts[5];
        if (!IsLoopbackHost(host) || !IsSupportedScheme(scheme) || port is < 1 or > 65535)
            return null;

        var path = parts.Length == 7 ? "/" : "/" + string.Join('/', parts.Skip(7));
        var query = string.Join("&", proxyUri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith(BrowserApiRoutes.LocalPortForwardingTokenQuery + "=", StringComparison.OrdinalIgnoreCase)));
        return new UriBuilder(scheme, host, port, path) { Query = query }.Uri;
    }

    public Task<IReadOnlyList<BookmarkDto>> ListBookmarksAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<BookmarkDto>>(HttpMethod.Get, BrowserApiRoutes.Bookmarks, ct: ct);

    public Task<BookmarkDto> AddBookmarkAsync(string title, string url, CancellationToken ct = default)
        => SendAsync<BookmarkDto>(HttpMethod.Post, BrowserApiRoutes.BookmarksCreate,
            body: new CreateBookmarkRequest(title, url), ct: ct);

    public async Task DeleteBookmarkAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete,
            BrowserApiRoutes.BookmarksDelete.Replace("{id}", id.ToString("D")), ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task ClearBookmarksAsync(CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, BrowserApiRoutes.BookmarksClear, ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public Task<IReadOnlyList<HistoryEntryDto>> ListHistoryAsync(int limit = 100, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<HistoryEntryDto>>(HttpMethod.Get, BrowserApiRoutes.History,
            query: ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct: ct);

    public Task<HistoryEntryDto> RecordVisitAsync(string title, string url, CancellationToken ct = default)
        => SendAsync<HistoryEntryDto>(HttpMethod.Post, BrowserApiRoutes.HistoryCreate,
            body: new CreateHistoryEntryRequest(title, url), ct: ct);

    public async Task DeleteHistoryAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete,
            BrowserApiRoutes.HistoryDelete.Replace("{id}", id.ToString("D")), ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(HttpMethod.Delete, BrowserApiRoutes.HistoryClear, ct: ct);
        await EnsureSuccessAsync(resp, ct);
    }

    // ---- helpers（与 ExplorerClient 同模式，保持一致的错误处理与 query 拼接）----

    private async Task<T> SendAsync<T>(HttpMethod method, string route,
        (string Key, string? Value)? query = null, object? body = null, CancellationToken ct = default)
    {
        using var resp = await SendRawAsync(method, route, query, body, ct);
        return await ReadAsync<T>(resp, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string route,
        (string Key, string? Value)? query = null, object? body = null, CancellationToken ct = default)
    {
        var serverUrl = RequireSession();
        using var req = new HttpRequestMessage(method, BuildUri(serverUrl, route, query));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Tokens!.AccessToken);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: RemoteOsJsonOptions.Default);
        return await _http.SendAsync(req, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode) await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, ct)
            ?? throw new RemoteOsAuthException(NoBodyProblem());
    }

    private string RequireSession()
    {
        if (_session.State != AuthSessionState.Authenticated || _session.Tokens is null || _session.ServerUrl is null)
            throw new InvalidOperationException("未登录，无法调用浏览器服务。");
        return _session.ServerUrl;
    }

    private static bool IsLoopbackTarget(Uri target)
        => target.IsAbsoluteUri && IsLoopbackHost(target.Host) && IsSupportedScheme(target.Scheme)
            && target.Port is > 0 and <= 65535;

    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("127.0.0.1", StringComparison.Ordinal);

    private static bool IsSupportedScheme(string scheme)
        => scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static Uri BuildUri(string serverUrl, string route, (string Key, string? Value)? query = null)
    {
        var baseUri = new Uri(serverUrl, UriKind.Absolute);
        var uri = new Uri(baseUri, route.TrimStart('/'));
        if (query is null || string.IsNullOrEmpty(query.Value.Value)) return uri;
        var qb = Uri.EscapeDataString(query.Value.Key) + "=" + Uri.EscapeDataString(query.Value.Value!);
        return new UriBuilder(uri) { Query = qb }.Uri;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        ProblemDetails? problem = null;
        try { problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(RemoteOsJsonOptions.Default, ct); }
        catch { /* 非 JSON 错误体回退通用错误 */ }
        throw problem is null
            ? new RemoteOsAuthException(new ProblemDetails(
                "https://remoteos.app/problems/http-error", $"HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode, resp.ReasonPhrase, null))
            : new RemoteOsAuthException(problem);
    }

    private static ProblemDetails NoBodyProblem()
        => new("https://remoteos.app/problems/empty-response", "空响应", 500, "服务器返回空响应体", null);
}
