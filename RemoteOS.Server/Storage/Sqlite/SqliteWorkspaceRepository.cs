using Microsoft.EntityFrameworkCore;
using RemoteOS.Protocol.Browser;
using RemoteOS.Protocol.Workspace;
using Server.Domain;

namespace Server.Storage.Sqlite;

/// <summary>Workspace 仓储的 EF Core + SQLite 实现。Scoped。TerminalSettings 随 Workspace 以 JSON 列持久化。
/// 对应 InMemoryWorkspaceRepository。读取时若 TerminalSettings 为空（防御旧数据）回退 Default。</summary>
public sealed class SqliteWorkspaceRepository : IWorkspaceRepository
{
    private readonly RemoteOsDbContext _db;

    public SqliteWorkspaceRepository(RemoteOsDbContext db) => _db = db;

    public Workspace? FindByUserId(Guid userId)
        => Normalize(_db.Workspaces.AsNoTracking().FirstOrDefault(w => w.UserId == userId));

    public Workspace? FindById(Guid id)
        => Normalize(_db.Workspaces.AsNoTracking().FirstOrDefault(w => w.Id == id));

    public Workspace Add(Workspace workspace)
    {
        _db.Workspaces.Add(workspace);
        _db.SaveChanges();
        return workspace;
    }

    public void Update(Workspace workspace)
    {
        _db.Workspaces.Update(workspace);
        _db.SaveChanges();
    }

    /// <summary>TerminalSettings 在领域模型默认非空，但旧数据/异常情况下 JSON 列可能为 null——读取时兜底。</summary>
    private static Workspace? Normalize(Workspace? w)
    {
        if (w is not null && w.TerminalSettings is null)
            w.TerminalSettings = TerminalSettingsDto.Default;
        if (w is not null && w.BrowserSettings is null)
            w.BrowserSettings = BrowserSettingsDto.Default;
        if (w is not null && w.Preferences is null)
            w.Preferences = WorkspacePreferencesDto.Default;
        if (w is not null && w.WindowLayouts is null)
            w.WindowLayouts = WorkspaceWindowLayoutDto.Default;
        return w;
    }
}
