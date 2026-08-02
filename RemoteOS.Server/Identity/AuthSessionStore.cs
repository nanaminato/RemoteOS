using System.Collections.Concurrent;

namespace Server.Identity;

/// <summary>Server 端会话/刷新令牌吊销簿。单例，线程安全。
/// 持有 refreshToken → 会话记录的映射，支持 refresh 时验旧 token、logout 时吊销。MVP 仅内存，重启丢失。</summary>
public sealed class AuthSessionStore
{
    private readonly ConcurrentDictionary<string, RefreshRecord> _refresh = new();

    /// <summary>注册一个新的刷新令牌。</summary>
    public void Register(Guid sessionId, string refreshToken, Guid userId, Guid workspaceId, Guid deviceId, DateTimeOffset expiresAt)
    {
        _refresh[refreshToken] = new RefreshRecord(sessionId, userId, workspaceId, deviceId, expiresAt);
    }

    /// <summary>校验刷新令牌是否有效（存在且未过期）。</summary>
    public bool IsValid(string refreshToken)
        => _refresh.TryGetValue(refreshToken, out var rec) && rec.ExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>查询刷新令牌对应的会话记录（不校验过期）。</summary>
    public bool TryGet(string refreshToken, out RefreshRecord record)
        => _refresh.TryGetValue(refreshToken, out record!);

    /// <summary>吊销刷新令牌（登出或刷新后旧 token 作废）。</summary>
    public bool Revoke(string refreshToken)
        => _refresh.TryRemove(refreshToken, out _);
}

/// <summary>刷新令牌对应的会话记录。</summary>
public sealed record RefreshRecord(Guid SessionId, Guid UserId, Guid WorkspaceId, Guid DeviceId, DateTimeOffset ExpiresAt);
