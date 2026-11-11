using System.Collections.Concurrent;
using System.Text.Json;

namespace media_player.Services;

public sealed class WatchProgressStore(MediaDownloadService downloads, IWebHostEnvironment environment, ILogger<WatchProgressStore> logger)
{
    private readonly string filePath = Path.Combine(environment.ContentRootPath, "App_Data", "watch-progress.json");
    private readonly string legacyFilePath = Path.Combine(downloads.CacheRoot, "watch-progress.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, WatchProgressEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool loaded;

    public async Task<IReadOnlyList<WatchProgressEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return entries.Values.Where(item => !item.IsCompleted).OrderByDescending(item => item.UpdatedAt).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<double?> GetPositionAsync(int tmdbId, string mediaType, int? seasonNumber = null, int? episodeNumber = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return entries.TryGetValue(Key(tmdbId, mediaType, seasonNumber, episodeNumber), out var entry) && !entry.IsCompleted ? entry.CurrentTimeSeconds : null;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<WatchProgressEntry>> GetEpisodesAsync(int tmdbId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return entries.Values.Where(item => item.TmdbId == tmdbId && item.MediaType == "tv"
                && item.SeasonNumber is not null && item.EpisodeNumber is not null).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(int tmdbId, string mediaType, string title, double currentTimeSeconds, double durationSeconds,
        string? posterUrl = null, int? seasonNumber = null, int? episodeNumber = null, CancellationToken ct = default)
    {
        if (tmdbId <= 0 || !double.IsFinite(currentTimeSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0) return;
        var episodic = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) && seasonNumber >= 0 && episodeNumber > 0;
        if (episodic) mediaType = "tv";
        else { seasonNumber = null; episodeNumber = null; }
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        await gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var current = Math.Clamp(currentTimeSeconds, 0, durationSeconds);
            var completed = current >= durationSeconds * .95 || durationSeconds - current <= 30;
            if (completed && !episodic)
                entries.TryRemove(key, out _);
            else
                entries[key] = new WatchProgressEntry(tmdbId, NormalizeMediaType(mediaType), title,
                    completed ? durationSeconds : current, durationSeconds, completed ? 100 : Math.Round(current / durationSeconds * 100, 2), DateTimeOffset.UtcNow,
                    posterUrl ?? (entries.TryGetValue(key, out var previous) ? previous.PosterUrl : null), seasonNumber, episodeNumber, completed);
            await SaveFileAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task SaveLibraryAsync(string libraryPath, string title, double currentTimeSeconds, double durationSeconds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || libraryPath.Length > 4096 || !double.IsFinite(currentTimeSeconds)
            || !double.IsFinite(durationSeconds) || durationSeconds <= 0) return;
        var key = LibraryKey(libraryPath);
        await gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var current = Math.Clamp(currentTimeSeconds, 0, durationSeconds);
            var completed = current >= durationSeconds * .95 || durationSeconds - current <= 30;
            if (completed) entries.TryRemove(key, out _);
            else entries[key] = new WatchProgressEntry(0, "library", title, current, durationSeconds,
                Math.Round(current / durationSeconds * 100, 2), DateTimeOffset.UtcNow, LibraryPath: libraryPath);
            await SaveFileAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task SetPosterAsync(int tmdbId, string mediaType, string? posterUrl, int? seasonNumber = null, int? episodeNumber = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(posterUrl)) return;
        await gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
            if (!entries.TryGetValue(key, out var entry) || string.Equals(entry.PosterUrl, posterUrl, StringComparison.Ordinal)) return;
            entries[key] = entry with { PosterUrl = posterUrl };
            await SaveFileAsync(ct);
        }
        finally { gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (loaded) return;
        loaded = true;
        var sourcePath = File.Exists(filePath) ? filePath : legacyFilePath;
        if (!File.Exists(sourcePath)) return;
        try
        {
            await using var stream = File.OpenRead(sourcePath);
            var saved = await JsonSerializer.DeserializeAsync<List<WatchProgressEntry>>(stream, cancellationToken: ct) ?? [];
            foreach (var item in saved.Where(item => (item.TmdbId > 0 || !string.IsNullOrWhiteSpace(item.LibraryPath)) && double.IsFinite(item.CurrentTimeSeconds)
                && double.IsFinite(item.DurationSeconds) && item.DurationSeconds > 0))
            {
                var current = Math.Clamp(item.CurrentTimeSeconds, 0, item.DurationSeconds);
                var percent = Math.Round(current / item.DurationSeconds * 100, 2);
                if (item.IsCompleted || percent is > 0 and < 95 && item.DurationSeconds - current > 30)
                    entries[EntryKey(item)] = item with
                    { CurrentTimeSeconds = item.IsCompleted ? item.DurationSeconds : current, ProgressPercent = item.IsCompleted ? 100 : percent };
            }
        }
        catch (JsonException ex) { logger.LogWarning(ex, "The watch progress file is invalid; starting with an empty list."); }
        catch (IOException ex) { logger.LogWarning(ex, "Could not read saved watch progress."); }
    }

    private async Task SaveFileAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporary = filePath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, entries.Values.OrderByDescending(item => item.UpdatedAt).ToArray(), cancellationToken: ct);
        File.Move(temporary, filePath, overwrite: true);
    }

    private static string NormalizeMediaType(string? mediaType) => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
    private static string EntryKey(WatchProgressEntry item) => !string.IsNullOrWhiteSpace(item.LibraryPath)
        ? LibraryKey(item.LibraryPath) : Key(item.TmdbId, item.MediaType, item.SeasonNumber, item.EpisodeNumber);
    private static string LibraryKey(string path) => "library-" + path;
    private static string Key(int tmdbId, string? mediaType, int? seasonNumber = null, int? episodeNumber = null) =>
        string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) && seasonNumber is >= 0 && episodeNumber is > 0
            ? $"tv-{tmdbId}-s{seasonNumber:00}e{episodeNumber:00}" : $"{NormalizeMediaType(mediaType)}-{tmdbId}";
}

public sealed record WatchProgressEntry(int TmdbId, string MediaType, string Title, double CurrentTimeSeconds,
    double DurationSeconds, double ProgressPercent, DateTimeOffset UpdatedAt, string? PosterUrl = null,
    int? SeasonNumber = null, int? EpisodeNumber = null, bool IsCompleted = false, string? LibraryPath = null);
