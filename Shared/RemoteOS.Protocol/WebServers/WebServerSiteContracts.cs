using System.Text.Json.Serialization;

namespace RemoteOS.Protocol.WebServers;

/// <summary>RemoteOS-owned Nginx virtual-host types. RemoteOS never edits unmanaged server blocks.</summary>
public enum WebServerSiteKind { ReverseProxy, Static }

public sealed record WebServerSiteDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] WebServerSiteKind Kind,
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("listenPort")] int ListenPort,
    [property: JsonPropertyName("upstream")] string? Upstream,
    [property: JsonPropertyName("rootPath")] string? RootPath,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId,
    [property: JsonPropertyName("httpsEnabled")] bool HttpsEnabled,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>Creates a site when Id is empty, otherwise updates that RemoteOS-owned site.</summary>
public sealed record UpsertWebServerSiteRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] WebServerSiteKind Kind,
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("listenPort")] int ListenPort = 80,
    [property: JsonPropertyName("upstream")] string? Upstream = null,
    [property: JsonPropertyName("siteDirectory")] string? SiteDirectory = null,
    [property: JsonPropertyName("certificateId")] Guid? CertificateId = null,
    [property: JsonPropertyName("httpsEnabled")] bool HttpsEnabled = false);
