using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace media_player.Services;

public sealed class WatchProgressStore(
    IDbContextFactory<UserDbContext> contexts,
    MediaDownloadService downloads,
    IWebHostEnvironment environment,
    ILogger<WatchProgressStore> logger)
{
    private readonly string filePath = Path.Combine(environment.ContentRootPath, "App_Data", "watch-progress.json");
    private readonly string legacyFilePath = Path.Combine(downloads.CacheRoot, "watch-progress.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private const string JsonImportMigration = "watch-progress-json-import-v1";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        // EnsureCreated does not add tables to an existing database created by an earlier app version.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "WatchProgress" (
                "OwnerKey" TEXT NOT NULL,
                "ProgressKey" TEXT NOT NULL,
                "UserName" TEXT NULL,
                "TmdbId" INTEGER NOT NULL,
                "MediaType" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "CurrentTimeSeconds" REAL NOT NULL,
                "DurationSeconds" REAL NOT NULL,
                "ProgressPercent" REAL NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "PosterUrl" TEXT NULL,
                "SeasonNumber" INTEGER NULL,
                "EpisodeNumber" INTEGER NULL,
                "IsCompleted" INTEGER NOT NULL,
                "LibraryPath" TEXT NULL,
                PRIMARY KEY ("OwnerKey", "ProgressKey"),
                FOREIGN KEY ("UserName") REFERENCES "Users" ("UserName") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_WatchProgress_UserName_UpdatedAt" ON "WatchProgress" ("UserName", "UpdatedAt");
            CREATE TABLE IF NOT EXISTS "AppMigrations" ("Name" TEXT NOT NULL CONSTRAINT "PK_AppMigrations" PRIMARY KEY);
            """, ct);
        await ImportLegacyProgressAsync(db, ct);
    }

    public async Task<IReadOnlyList<WatchProgressEntry>> GetAllAsync(string? userName = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entries = await GetVisibleRows(db, userName).Where(row => row.TmdbId > 0
            && (row.MediaType == "movie" || row.MediaType == "tv")).ToListAsync(ct);
        return PreferOwnedRows(entries, userName).Where(row => !row.IsCompleted).Select(ToEntry)
            .OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public async Task<double?> GetPositionAsync(int tmdbId, string mediaType, int? seasonNumber = null,
        int? episodeNumber = null, string? userName = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        var row = await db.WatchProgress.AsNoTracking().FirstOrDefaultAsync(item => item.OwnerKey == Owner(userName)
            && item.ProgressKey == key, ct);
        if (row is not null) return row.IsCompleted ? null : row.CurrentTimeSeconds;
        if (userName is not null)
        {
            row = await db.WatchProgress.AsNoTracking().FirstOrDefaultAsync(item => item.OwnerKey == "" && item.ProgressKey == key, ct);
        }
        return row is { IsCompleted: false } ? row.CurrentTimeSeconds : null;
    }

    public async Task<IReadOnlyList<WatchProgressEntry>> GetEpisodesAsync(int tmdbId, string? userName = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await GetVisibleRows(db, userName).Where(item => item.TmdbId == tmdbId && item.MediaType == "tv"
            && item.SeasonNumber != null && item.EpisodeNumber != null).ToListAsync(ct);
        return PreferOwnedRows(rows, userName).Select(ToEntry).ToArray();
    }

    public async Task SaveAsync(int tmdbId, string mediaType, string title, double currentTimeSeconds, double durationSeconds,
        string? posterUrl = null, int? seasonNumber = null, int? episodeNumber = null, CancellationToken ct = default,
        string? libraryPath = null, string? userName = null)
    {
        if (tmdbId <= 0 || !double.IsFinite(currentTimeSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0) return;
        var episodic = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) && seasonNumber >= 0 && episodeNumber > 0;
        if (episodic) mediaType = "tv";
        else { seasonNumber = null; episodeNumber = null; }
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        var ownerKey = Owner(userName);
        var current = Math.Clamp(currentTimeSeconds, 0, durationSeconds);
        var completed = current >= durationSeconds * .95 || durationSeconds - current <= 30;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.WatchProgress.FindAsync([ownerKey, key], ct);
        if (completed && !episodic)
        {
            if (existing is not null) db.WatchProgress.Remove(existing);
        }
        else
        {
            var row = existing ?? new WatchProgressRow { OwnerKey = ownerKey, ProgressKey = key, UserName = userName };
            row.TmdbId = tmdbId;
            row.MediaType = NormalizeMediaType(mediaType);
            row.Title = title;
            row.CurrentTimeSeconds = completed ? durationSeconds : current;
            row.DurationSeconds = durationSeconds;
            row.ProgressPercent = completed ? 100 : Math.Round(current / durationSeconds * 100, 2);
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.PosterUrl = posterUrl ?? existing?.PosterUrl;
            row.SeasonNumber = seasonNumber;
            row.EpisodeNumber = episodeNumber;
            row.IsCompleted = completed;
            row.LibraryPath = libraryPath;
            if (existing is null) db.WatchProgress.Add(row);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task ReconcileLibraryProgressAsync(IReadOnlyList<CachedMediaAssociation> associations,
        string? userName = null, CancellationToken ct = default)
    {
        var byPath = associations.Where(item => item.IsLibraryItem).ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var legacyRows = await GetVisibleRows(db, userName).Where(item => item.TmdbId <= 0 || item.MediaType == "library").ToListAsync(ct);
        foreach (var legacy in legacyRows)
        {
            CachedMediaAssociation? association = null;
            if (legacy.LibraryPath is not null && TryDecodeCachedPath(legacy.LibraryPath, out var path)) byPath.TryGetValue(path, out association);
            db.WatchProgress.Remove(legacy);
            if (association is null) continue;
            var newKey = Key(association.TmdbId, association.MediaType, association.SeasonNumber, association.EpisodeNumber);
            var migrated = await db.WatchProgress.FindAsync([legacy.OwnerKey, newKey], ct);
            if (migrated is null || legacy.UpdatedAt >= migrated.UpdatedAt)
            {
                if (migrated is not null) db.WatchProgress.Remove(migrated);
                db.WatchProgress.Add(new WatchProgressRow
                {
                    OwnerKey = legacy.OwnerKey, ProgressKey = newKey, UserName = legacy.UserName,
                    TmdbId = association.TmdbId, MediaType = association.MediaType, Title = association.Name,
                    CurrentTimeSeconds = legacy.CurrentTimeSeconds, DurationSeconds = legacy.DurationSeconds,
                    ProgressPercent = legacy.ProgressPercent, UpdatedAt = legacy.UpdatedAt, PosterUrl = legacy.PosterUrl,
                    SeasonNumber = association.SeasonNumber, EpisodeNumber = association.EpisodeNumber,
                    IsCompleted = legacy.IsCompleted, LibraryPath = legacy.LibraryPath
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPosterAsync(int tmdbId, string mediaType, string? posterUrl, int? seasonNumber = null,
        int? episodeNumber = null, CancellationToken ct = default, string? userName = null)
    {
        if (string.IsNullOrWhiteSpace(posterUrl)) return;
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.WatchProgress.FindAsync([Owner(userName), key], ct)
            ?? (userName is null ? null : await db.WatchProgress.FindAsync(["", key], ct));
        if (row is null || string.Equals(row.PosterUrl, posterUrl, StringComparison.Ordinal)) return;
        row.PosterUrl = posterUrl;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetLibraryPosterAsync(string libraryPath, string? posterUrl, CancellationToken ct = default, string? userName = null)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || string.IsNullOrWhiteSpace(posterUrl)) return;
        var key = LibraryKey(libraryPath);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.WatchProgress.FindAsync([Owner(userName), key], ct)
            ?? (userName is null ? null : await db.WatchProgress.FindAsync(["", key], ct));
        if (row is null || string.Equals(row.PosterUrl, posterUrl, StringComparison.Ordinal)) return;
        row.PosterUrl = posterUrl;
        await db.SaveChangesAsync(ct);
    }

    private async Task ImportLegacyProgressAsync(UserDbContext db, CancellationToken ct)
    {
        if (await db.Migrations.AnyAsync(item => item.Name == JsonImportMigration, ct)) return;
        var sourcePath = File.Exists(filePath) ? filePath : legacyFilePath;
        var saved = new List<WatchProgressEntry>();
        if (File.Exists(sourcePath))
        {
            try
            {
                await using var stream = File.OpenRead(sourcePath);
                saved = await JsonSerializer.DeserializeAsync<List<WatchProgressEntry>>(stream, cancellationToken: ct) ?? [];
            }
            catch (JsonException ex) { logger.LogWarning(ex, "The watch progress file is invalid; no progress rows were imported."); }
            catch (IOException ex) { logger.LogWarning(ex, "Could not read the legacy watch progress file."); }
        }
        var validEntries = saved.Where(item => (item.TmdbId > 0 || !string.IsNullOrWhiteSpace(item.LibraryPath))
            && double.IsFinite(item.CurrentTimeSeconds) && double.IsFinite(item.DurationSeconds) && item.DurationSeconds > 0)
            .GroupBy(EntryKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First());
        foreach (var item in validEntries)
        {
            var current = Math.Clamp(item.CurrentTimeSeconds, 0, item.DurationSeconds);
            var percent = Math.Round(current / item.DurationSeconds * 100, 2);
            if (!item.IsCompleted && !(percent is > 0 and < 95 && item.DurationSeconds - current > 30)) continue;
            var key = EntryKey(item);
            db.WatchProgress.Add(new WatchProgressRow
            {
                OwnerKey = "", ProgressKey = key, UserName = null, TmdbId = item.TmdbId,
                MediaType = item.MediaType, Title = item.Title,
                CurrentTimeSeconds = item.IsCompleted ? item.DurationSeconds : current,
                DurationSeconds = item.DurationSeconds, ProgressPercent = item.IsCompleted ? 100 : percent,
                UpdatedAt = item.UpdatedAt, PosterUrl = item.PosterUrl, SeasonNumber = item.SeasonNumber,
                EpisodeNumber = item.EpisodeNumber, IsCompleted = item.IsCompleted, LibraryPath = item.LibraryPath
            });
        }
        db.Migrations.Add(new AppMigration { Name = JsonImportMigration });
        await db.SaveChangesAsync(ct);
    }

    private static IQueryable<WatchProgressRow> GetVisibleRows(UserDbContext db, string? userName) => userName is null
        ? db.WatchProgress.Where(row => row.OwnerKey == "")
        : db.WatchProgress.Where(row => row.OwnerKey == userName || row.OwnerKey == "");

    private static IEnumerable<WatchProgressRow> PreferOwnedRows(IEnumerable<WatchProgressRow> rows, string? userName) =>
        rows.GroupBy(row => row.ProgressKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => userName is null ? group.First() : group.OrderByDescending(row => row.OwnerKey == userName).First());

    private static WatchProgressEntry ToEntry(WatchProgressRow row) => new(row.TmdbId, row.MediaType, row.Title,
        row.CurrentTimeSeconds, row.DurationSeconds, row.ProgressPercent, row.UpdatedAt, row.PosterUrl,
        row.SeasonNumber, row.EpisodeNumber, row.IsCompleted, row.LibraryPath);

    private static string Owner(string? userName) => userName ?? "";
    private static string NormalizeMediaType(string? mediaType) => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
    private static string EntryKey(WatchProgressEntry item) => !string.IsNullOrWhiteSpace(item.LibraryPath)
        ? LibraryKey(item.LibraryPath) : Key(item.TmdbId, item.MediaType, item.SeasonNumber, item.EpisodeNumber);
    private static string LibraryKey(string path) => "library-" + path;
    private static bool TryDecodeCachedPath(string path, out string decoded)
    {
        decoded = "";
        if (!path.StartsWith("cache.", StringComparison.Ordinal)) return false;
        try
        {
            var token = path[6..].Replace('-', '+').Replace('_', '/');
            token += new string('=', (4 - token.Length % 4) % 4);
            decoded = Path.GetFullPath(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token)));
            return true;
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
    private static string Key(int tmdbId, string? mediaType, int? seasonNumber = null, int? episodeNumber = null) =>
        string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) && seasonNumber is >= 0 && episodeNumber is > 0
            ? $"tv-{tmdbId}-s{seasonNumber:00}e{episodeNumber:00}" : $"{NormalizeMediaType(mediaType)}-{tmdbId}";
}

public sealed record WatchProgressEntry(int TmdbId, string MediaType, string Title, double CurrentTimeSeconds,
    double DurationSeconds, double ProgressPercent, DateTimeOffset UpdatedAt, string? PosterUrl = null,
    int? SeasonNumber = null, int? EpisodeNumber = null, bool IsCompleted = false, string? LibraryPath = null);
