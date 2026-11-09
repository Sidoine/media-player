using System.Text.Json;

namespace media_player.Services;

public sealed class MediaWatchlistStore(IWebHostEnvironment environment)
{
    private readonly string file = Path.Combine(environment.ContentRootPath, "App_Data", "media-watchlist.json");
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<List<MediaWatchlistEntry>> GetAllAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await ReadUnlockedAsync(ct); }
        finally { gate.Release(); }
    }

    public async Task<MediaWatchlistEntry> AddAsync(MediaWatchlistEntry entry, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnlockedAsync(ct);
            var existing = items.FirstOrDefault(item => item.Key == entry.Key);
            if (existing is not null) return existing;
            items.Add(entry);
            await WriteUnlockedAsync(items, ct);
            return entry;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnlockedAsync(ct);
            var removed = items.RemoveAll(item => item.Key == key) > 0;
            if (removed) await WriteUnlockedAsync(items, ct);
            return removed;
        }
        finally { gate.Release(); }
    }

    public async Task MarkCheckedAsync(string key, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnlockedAsync(ct);
            var index = items.FindIndex(item => item.Key == key);
            if (index < 0) return;
            items[index] = items[index] with { LastCheckedAt = DateTimeOffset.UtcNow };
            await WriteUnlockedAsync(items, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<List<MediaWatchlistEntry>> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(file)) return [];
        try
        {
            await using var input = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<MediaWatchlistEntry>>(input, cancellationToken: ct) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private async Task WriteUnlockedAsync(List<MediaWatchlistEntry> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        await using (var output = File.Create(temp))
            await JsonSerializer.SerializeAsync(output, items.OrderBy(item => item.AddedAt).ToArray(), cancellationToken: ct);
        File.Move(temp, file, overwrite: true);
    }
}

public sealed record MediaWatchlistEntry(string Key, int TmdbId, string MediaType, string Title, string OriginalLanguage,
    int? Year, int? SeasonNumber, int? EpisodeNumber, string? EpisodeTitle, int? RuntimeMinutes, DateTimeOffset AddedAt,
    DateTimeOffset? LastCheckedAt = null)
{
    public static string CreateKey(int tmdbId, string mediaType, int? seasonNumber, int? episodeNumber) =>
        string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) && seasonNumber is not null && episodeNumber is not null
            ? $"tv-{tmdbId}-s{seasonNumber:00}e{episodeNumber:00}" : $"{mediaType}-{tmdbId}";
}

public sealed record MediaWatchlistRequest(int TmdbId, string MediaType, string Title, string? OriginalLanguage, int? Year,
    int? SeasonNumber = null, int? EpisodeNumber = null, string? EpisodeTitle = null, int? RuntimeMinutes = null);
