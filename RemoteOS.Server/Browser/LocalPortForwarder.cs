using System.Net;
using Microsoft.AspNetCore.Http;
using RemoteOS.Protocol.Browser;

namespace Server.Browser;

/// <summary>
/// Copies an authenticated browser request to a loopback-only HTTP service on the server and
/// streams its response back through RemoteOS. It intentionally does not implement a general
/// purpose proxy: only <c>localhost</c> and <c>127.0.0.1</c> are accepted.
/// </summary>
public sealed class LocalPortForwarder
{
    public const string HttpClientName = "RemoteOS.LocalPortForwarding";

    private static readonly HashSet<string> RequestHeadersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Connection", "Content-Length", "Cookie", "Host", "Proxy-Authorization",
        "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    };

    private static readonly HashSet<string> ResponseHeadersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    };

    private readonly IHttpClientFactory _clients;

    public LocalPortForwarder(IHttpClientFactory clients) => _clients = clients;

    public async Task ForwardAsync(
        HttpContext context, string host, string scheme, int port, string? path, CancellationToken ct)
    {
        if (!IsLoopbackHost(host) || !IsSupportedScheme(scheme) || port is < 1 or > 65535)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Only http(s) localhost and 127.0.0.1 ports can be forwarded.", ct);
            return;
        }

        var target = BuildTargetUri(context, host, scheme, port, path);
        using var request = CreateRequest(context, target);

        try
        {
            var client = _clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(context, response, host, scheme, port);
            await response.Content.CopyToAsync(context.Response.Body, ct);
        }
        catch (HttpRequestException ex)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync($"Unable to reach remote loopback service at {target.Authority}: {ex.Message}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The browser disconnected or cancelled navigation; there is no response left to send.
        }
    }

    private static HttpRequestMessage CreateRequest(HttpContext context, Uri target)
    {
        var hasBody = context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding");
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target)
        {
            Content = hasBody ? new StreamContent(context.Request.Body) : null,
        };

        foreach (var header in context.Request.Headers)
        {
            if (RequestHeadersToSkip.Contains(header.Key))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        // Forward only the proxied application's cookies. The RemoteOS authentication cookie
        // must never be exposed to the loopback service.
        var cookies = context.Request.Cookies
            .Where(cookie => !cookie.Key.Equals(BrowserApiRoutes.LocalPortForwardingAuthCookie, StringComparison.Ordinal))
            .Select(cookie => $"{cookie.Key}={cookie.Value}");
        var cookieHeader = string.Join("; ", cookies);
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        return request;
    }

    private static void CopyResponseHeaders(HttpContext context, HttpResponseMessage response,
        string host, string scheme, int port)
    {
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (ResponseHeadersToSkip.Contains(header.Key))
                continue;

            if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var value in header.Value)
                {
                    var forwarded = RewriteSetCookie(value, host, scheme, port);
                    if (forwarded is not null)
                        context.Response.Headers.Append("Set-Cookie", forwarded);
                }
                continue;
            }

            var values = header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)
                ? header.Value.Select(value => RewriteLocation(context, value, host, scheme, port)).ToArray()
                : header.Value.ToArray();
            context.Response.Headers[header.Key] = values;
        }
    }

    private static string? RewriteSetCookie(string value, string host, string scheme, int port)
    {
        var pieces = value.Split(';', StringSplitOptions.TrimEntries).ToList();
        if (pieces.Count == 0 || pieces[0].StartsWith(BrowserApiRoutes.LocalPortForwardingAuthCookie + "=", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = $"Path={BrowserApiRoutes.LocalPortForwardingPrefix}/{host}/{scheme}/{port}/";
        var replacedPath = false;
        for (var i = pieces.Count - 1; i >= 1; i--)
        {
            if (pieces[i].StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                pieces.RemoveAt(i);
            else if (pieces[i].StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
            {
                pieces[i] = path;
                replacedPath = true;
            }
        }
        if (!replacedPath)
            pieces.Add(path);
        return string.Join("; ", pieces);
    }

    private static string RewriteLocation(HttpContext context, string location, string currentHost, string currentScheme, int currentPort)
    {
        if (!Uri.TryCreate(location, UriKind.RelativeOrAbsolute, out var redirect))
            return location;

        if (redirect.IsAbsoluteUri)
        {
            if (!IsLoopbackHost(redirect.Host) || !IsSupportedScheme(redirect.Scheme))
                return location;
            return BuildProxyUri(context, redirect.Host, redirect.Scheme, redirect.Port, redirect.PathAndQuery);
        }

        if (location.StartsWith('/'))
            return BuildProxyUri(context, currentHost, currentScheme, currentPort, location);

        return location;
    }

    private static Uri BuildTargetUri(HttpContext context, string host, string scheme, int port, string? path)
    {
        var query = context.Request.Query
            .Where(pair => !pair.Key.Equals(BrowserApiRoutes.LocalPortForwardingTokenQuery, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)));
        return new UriBuilder(scheme, host, port, "/" + (path ?? string.Empty))
        {
            Query = QueryString.Create(query).Value?.TrimStart('?')
        }.Uri;
    }

    private static string BuildProxyUri(HttpContext context, string host, string scheme, int port, string pathAndQuery)
    {
        var path = pathAndQuery;
        var query = string.Empty;
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = path[queryIndex..];
            path = path[..queryIndex];
        }

        var proxyPath = $"{BrowserApiRoutes.LocalPortForwardingPrefix}/{host}/{scheme}/{port}/{path.TrimStart('/')}";
        return new UriBuilder(context.Request.Scheme, context.Request.Host.Host, context.Request.Host.Port ?? -1)
        {
            Path = proxyPath,
            Query = query.TrimStart('?')
        }.Uri.ToString();
    }

    private static bool IsLoopbackHost(string? host)
        => host is not null && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal));

    private static bool IsSupportedScheme(string? scheme)
        => scheme is not null && (scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}
