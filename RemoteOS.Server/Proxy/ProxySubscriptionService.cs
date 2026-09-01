using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteOS.Protocol.Proxy;
using Server.Proxy.Mihomo;

namespace Server.Proxy;

public interface IProxySubscriptionService
{
    Task<IReadOnlyList<ProxySubscriptionDto>> ListAsync(CancellationToken cancellationToken);
    Task<ProxySubscriptionDto> ImportAsync(ImportProxySubscriptionRequest request, CancellationToken cancellationToken);
    Task<string?> RefreshAsync(Guid subscriptionId, CancellationToken cancellationToken);
    Task<string?> RefreshAllAsync(CancellationToken cancellationToken);
    Task<string?> ActivateAsync(Guid subscriptionId, CancellationToken cancellationToken);
    Task<ProxySubscriptionContentDto?> GetContentAsync(Guid subscriptionId, CancellationToken cancellationToken);
}

/// <summary>Subscription coordinator. URLs stay inside the protected repository and are never logged or returned.</summary>
public sealed class ProxySubscriptionService(
    IProxySubscriptionRepository subscriptions,
    IProxyProfileRepository profiles,
    IProxyConfigurationTransactionService configurations,
    IProxySubscriptionDownloader downloader) : IProxySubscriptionService
{
    public Task<IReadOnlyList<ProxySubscriptionDto>> ListAsync(CancellationToken cancellationToken) => subscriptions.ListAsync(cancellationToken);

    public async Task<ProxySubscriptionDto> ImportAsync(ImportProxySubscriptionRequest request, CancellationToken cancellationToken)
    {
        var source = await downloader.DownloadAsync(request.Url, cancellationToken);
        var name = NormalizeName(request.Name, source.Uri);
        var profile = await profiles.UpsertAsync(null, name, MihomoEngine.Id, null, cancellationToken);
        try
        {
            var problem = await configurations.StoreAsync(profile.Id, source.Content, cancellationToken);
            if (!string.IsNullOrEmpty(problem)) throw new ProxySubscriptionException(problem);
            return await subscriptions.CreateAsync(name, profile.Id, source.Uri.AbsoluteUri, cancellationToken);
        }
        catch
        {
            await profiles.DeleteAsync(profile.Id, CancellationToken.None);
            throw;
        }
    }

    public async Task<string?> RefreshAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var record = await subscriptions.GetAsync(subscriptionId, cancellationToken);
        if (record is null) return ProxyProblemCodes.SubscriptionInvalid;
        var source = await downloader.DownloadAsync(record.Url, cancellationToken);
        var problem = await configurations.StoreAsync(record.Subscription.ProfileId, source.Content, cancellationToken);
        if (!string.IsNullOrEmpty(problem)) return problem;
        if (record.Subscription.IsActive)
        {
            problem = await configurations.ActivateStoredAsync(record.Subscription.ProfileId, cancellationToken);
            if (!string.IsNullOrEmpty(problem)) return problem;
        }
        await subscriptions.SetLastUpdatedAsync(subscriptionId, DateTimeOffset.UtcNow, cancellationToken);
        return null;
    }

    public async Task<string?> RefreshAllAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in await subscriptions.ListAsync(cancellationToken))
        {
            var problem = await RefreshAsync(subscription.Id, cancellationToken);
            if (!string.IsNullOrEmpty(problem)) return problem;
        }
        return null;
    }

    public async Task<string?> ActivateAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var record = await subscriptions.GetAsync(subscriptionId, cancellationToken);
        if (record is null) return ProxyProblemCodes.SubscriptionInvalid;
        var problem = await configurations.ActivateStoredAsync(record.Subscription.ProfileId, cancellationToken);
        if (!string.IsNullOrEmpty(problem)) return problem;
        return await profiles.SetActiveAsync(record.Subscription.ProfileId, cancellationToken) is null ? ProxyProblemCodes.SubscriptionInvalid : null;
    }

    public async Task<ProxySubscriptionContentDto?> GetContentAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var record = await subscriptions.GetAsync(subscriptionId, cancellationToken);
        if (record is null) return null;
        var source = await downloader.DownloadAsync(record.Url, cancellationToken);
        return new ProxySubscriptionContentDto(subscriptionId, source.Content, DateTimeOffset.UtcNow);
    }

    private static string NormalizeName(string? requestedName, Uri source)
    {
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? source.Host + " · " + Guid.NewGuid().ToString("N")[..6]
            : requestedName.Trim();
        if (name.Length is 0 or > 128 || name.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);
        return name;
    }
}

public interface IProxySubscriptionDownloader
{
    Task<ProxySubscriptionDownload> DownloadAsync(string url, CancellationToken cancellationToken);
}

public sealed record ProxySubscriptionDownload(Uri Uri, string Content);

/// <summary>Bounded HTTPS downloader that rejects loopback, private and link-local targets.</summary>
public sealed class ProxySubscriptionDownloader(HttpClient http) : IProxySubscriptionDownloader
{
    private const int MaximumBytes = 1_048_576;

    public async Task<ProxySubscriptionDownload> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.IsLoopback || IPAddress.TryParse(uri.Host, out _))
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);

        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken); }
        catch (SocketException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed); }
        if (addresses.Length == 0 || addresses.Any(ProxySubscriptionNetworkPolicy.IsPrivateAddress))
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed); }
        using (response)
        {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaximumBytes)
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[16_384];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > MaximumBytes) throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed);
                await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
            }
            string content;
            try { content = new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
            catch (DecoderFallbackException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid); }
            if (string.IsNullOrWhiteSpace(content) || content.IndexOf('\0') >= 0)
                throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);
            return new ProxySubscriptionDownload(uri, content);
        }
        catch (HttpRequestException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed); }
        catch (IOException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionFetchFailed); }
        }
    }

}

internal static class ProxySubscriptionNetworkPolicy
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken); }
        catch (SocketException exception) { throw new HttpRequestException("Subscription host could not be resolved.", exception); }
        var address = addresses.FirstOrDefault(candidate => !IsPrivateAddress(candidate));
        if (address is null) throw new HttpRequestException("Subscription host resolves to a prohibited network.");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.GetAddressBytes()[0] is 0xfc or 0xfd;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
