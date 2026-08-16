using RemoteOS.Protocol.ImageMirrors;

namespace Server.Domain;

/// <summary>User-owned image registry mirror configuration. Selection is unique per user and target.</summary>
public sealed class ImageMirror
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ImageMirrorTarget Target { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
