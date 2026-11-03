using Microsoft.EntityFrameworkCore;

namespace VaultSync;

public class VaultDb(DbContextOptions<VaultDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<Audit> Audits => Set<Audit>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Device>().Property(x => x.Head).IsConcurrencyToken();
        b.Entity<Snapshot>().HasIndex(x => new { x.DeviceId, x.Key }).IsUnique();
    }
}

public class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? Head { get; set; }
    public bool Revoked { get; set; }
}

public class Chunk
{
    public string Id { get; set; } = "";
    public int Bytes { get; set; }
}

public class Snapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Parent { get; set; }
    public string Manifest { get; set; } = "";
    public string Digest { get; set; } = "";
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
    public long Bytes { get; set; }
    public int Files { get; set; }
}

public class Audit
{
    public long Id { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public long At { get; set; } = DateTime.UtcNow.Ticks;
}

public record FileEntry(string Path, long Bytes, string Hash, List<string> Chunks);

public record Commit(string Key, string? Parent, List<FileEntry> Files);

public class ApiFault(int status, string message) : Exception(message)
{
    public int Status => status;
}
