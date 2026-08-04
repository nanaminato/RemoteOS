using Microsoft.EntityFrameworkCore;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Workspace;
using Server.Domain;

namespace Server.Storage.Sqlite;

/// <summary>EF Core DbContext。持久化 User / Workspace（含 TerminalSettings）/ Device 三个「持久实体」。
/// Session / refresh token / PTY 进程不在本上下文（维持内存，见 docs/RemoteOS.Storage.md）。</summary>
public sealed class RemoteOsDbContext : DbContext
{
    public RemoteOsDbContext(DbContextOptions<RemoteOsDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Device> Devices => Set<Device>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── users ──
        mb.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnType("TEXT");
            e.Property(u => u.Username).IsRequired().HasMaxLength(128);
            // Platform 为枚举，存字符串与线协议（camelCase）一致
            e.Property(u => u.Platform).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(u => u.PlatformIdentity).HasMaxLength(256);
            e.Property(u => u.CreatedAt).HasColumnType("TEXT");
            e.Property(u => u.LastLoginAt).HasColumnType("TEXT");
            // 按 (username, platform) 索引——对应 InMemoryUserRepository._byName
            e.HasIndex(u => new { u.Username, u.Platform }).IsUnique();
        });

        // ── workspaces ──
        mb.Entity<Workspace>(e =>
        {
            e.ToTable("workspaces");
            e.HasKey(w => w.Id);
            e.Property(w => w.Id).HasColumnType("TEXT");
            e.Property(w => w.UserId).HasColumnType("TEXT");
            e.Property(w => w.Name).IsRequired().HasMaxLength(256);
            e.Property(w => w.State).HasConversion<string>().HasMaxLength(32);
            e.Property(w => w.CreatedAt).HasColumnType("TEXT");
            e.Property(w => w.ControllerDeviceId).HasColumnType("TEXT");
            e.Property(w => w.ControllerGrantedAt).HasColumnType("TEXT");
            e.Property(w => w.ControllerLeaseExpiresAt).HasColumnType("TEXT");
            // One User One Persistent Workspace——按 UserId 唯一索引，对应 InMemoryWorkspaceRepository._byUserId
            e.HasIndex(w => w.UserId).IsUnique();

            // TerminalSettings 作为 JSON 列：EF Core 9+ 的 ToJson，把 TerminalSettingsDto（6 字段 record）
            // 序列化为单列 JSON 文本。配置可演进——新增外观字段无需改 schema。
            e.OwnsOne(w => w.TerminalSettings, t =>
            {
                t.ToJson("terminal_settings");
            });
        });

        // ── devices ──
        mb.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasColumnType("TEXT");
            e.Property(d => d.Name).IsRequired().HasMaxLength(128);
            e.Property(d => d.Platform).IsRequired().HasMaxLength(32);
            e.Property(d => d.ClientVersion).HasMaxLength(64);
            e.Property(d => d.LastLoginAt).HasColumnType("TEXT");
            // 按 (name, platform) 索引——对应 InMemoryDeviceRepository._byKey
            e.HasIndex(d => new { d.Name, d.Platform }).IsUnique();
        });
    }
}
