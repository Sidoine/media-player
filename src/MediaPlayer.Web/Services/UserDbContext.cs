using Microsoft.EntityFrameworkCore;

namespace media_player.Services;

public sealed class UserAccount
{
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "User";
}

public sealed class WatchProgressRow
{
    public string OwnerKey { get; set; } = "";
    public string ProgressKey { get; set; } = "";
    public string? UserName { get; set; }
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "movie";
    public string Title { get; set; } = "";
    public double CurrentTimeSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public double ProgressPercent { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? PosterUrl { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public bool IsCompleted { get; set; }
    public string? LibraryPath { get; set; }
}

public sealed class AppMigration
{
    public string Name { get; set; } = "";
}

public sealed class UserDbContext(DbContextOptions<UserDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<WatchProgressRow> WatchProgress => Set<WatchProgressRow>();
    public DbSet<AppMigration> Migrations => Set<AppMigration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.UserName);
            entity.Property(user => user.UserName).HasMaxLength(64).UseCollation("NOCASE");
            entity.Property(user => user.PasswordHash).IsRequired();
            entity.Property(user => user.Role).HasMaxLength(32).IsRequired();
        });
        modelBuilder.Entity<WatchProgressRow>(entity =>
        {
            entity.ToTable("WatchProgress");
            entity.HasKey(row => new { row.OwnerKey, row.ProgressKey });
            entity.Property(row => row.OwnerKey).HasMaxLength(64);
            entity.Property(row => row.ProgressKey).HasMaxLength(300);
            entity.Property(row => row.UserName).HasMaxLength(64);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(row => row.UserName).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(row => new { row.UserName, row.UpdatedAt });
        });
        modelBuilder.Entity<AppMigration>(entity =>
        {
            entity.ToTable("AppMigrations");
            entity.HasKey(migration => migration.Name);
        });
    }
}
