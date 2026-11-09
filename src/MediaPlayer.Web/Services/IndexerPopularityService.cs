using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace media_player.Services;

public sealed class IndexerPopularityService(
    IHttpClientFactory clients,
    TorrentCatalogSearchService search,
    TorrentCatalogStore catalogs,
    SelectedIndexerStore selectedIndexers,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    ILogger<IndexerPopularityService> logger) : BackgroundService
{
    private readonly string cachePath = Path.Combine(environment.ContentRootPath, "App_Data", "indexer-popularity-cache.json");
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private List<IndexerPopularMedia> media = [];
    private DateTimeOffset? lastUpdated;
    private string? cachedSourceSignature;
    private bool cacheLoaded;
    private volatile bool isRefreshing;
    private string? lastError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = Math.Clamp(configuration.GetValue("Media:IndexerPopularityRefreshIntervalHours", 8), 1, 168);
        await LoadCacheAsync(stoppingToken);
        await RefreshIfNeededAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await RefreshIfNeededAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<IndexerPopularitySnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        await LoadCacheAsync(ct);
        var signature = await GetSourceSignatureAsync(ct);
        if ((lastUpdated is null || DateTimeOffset.UtcNow - lastUpdated >= RefreshInterval || signature != cachedSourceSignature)
            && !isRefreshing)
            _ = Task.Run(() => RefreshIfNeededAsync(CancellationToken.None), CancellationToken.None);
        return new IndexerPopularitySnapshot(media, lastUpdated, isRefreshing, lastError, RefreshInterval.TotalHours);
    }

    private TimeSpan RefreshInterval => TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Media:IndexerPopularityRefreshIntervalHours", 8), 1, 168));

    private async Task RefreshIfNeededAsync(CancellationToken ct)
    {
        await LoadCacheAsync(ct);
        var signature = await GetSourceSignatureAsync(ct);
        if (lastUpdated is not null && DateTimeOffset.UtcNow - lastUpdated < RefreshInterval && signature == cachedSourceSignature) return;
        await RefreshAsync(ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!await refreshGate.WaitAsync(0, ct)) return;
        isRefreshing = true;
        lastError = null;
        try
        {
            var signature = await GetSourceSignatureAsync(ct);
            var client = clients.CreateClient("tmdb");
            using var response = await client.GetAsync("trending/all/week?language=fr-FR&page=1", ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("TMDB returned no trending media list.");

            var trending = results.EnumerateArray()
                .Select(ParseTrendingMedia)
                .Where(item => item is not null)
                .Select(item => item!)
                .Take(10)
                .ToArray();
            using var concurrency = new SemaphoreSlim(3, 3);
            var lookups = trending.Select(async item =>
            {
                await concurrency.WaitAsync(ct);
                try
                {
                    var query = string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.Title : item.OriginalTitle;
                    if (item.Year is not null) query += $" {item.Year}";
                    var response = await search.SearchAsync(query, ct);
                    var matches = response.Results.Where(result => result.RightsConfirmed && MatchesTitle(item, result.Title))
                        .ToArray();
                    if (matches.Length == 0) return null;
                    var seeders = matches.Sum(result => ParseCount(result.Seeders));
                    var grabs = matches.Sum(result => ParseCount(result.Grabs));
                    var leechers = matches.Sum(result => ParseCount(result.Leechers));
                    var sourceCount = matches.Select(result => result.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    var score = grabs * 5L + seeders * 2L + leechers + sourceCount * 100L;
                    return item with { AvailableSources = sourceCount, Seeders = seeders, Grabs = grabs, PopularityScore = score,
                        IndexerNames = matches.Select(result => result.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                }
                finally { concurrency.Release(); }
            });
            var found = (await Task.WhenAll(lookups)).Where(item => item is not null).Select(item => item!)
                .OrderByDescending(item => item.PopularityScore)
                .ThenByDescending(item => item.TmdbPopularity)
                .Take(30).ToList();
            media = found;
            lastUpdated = DateTimeOffset.UtcNow;
            cachedSourceSignature = signature;
            await SaveCacheAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lastError = "Les tendances des indexeurs sont temporairement indisponibles.";
            logger.LogWarning(ex, "Could not refresh popular media from configured indexers.");
        }
        finally
        {
            isRefreshing = false;
            refreshGate.Release();
        }
    }

    private async Task LoadCacheAsync(CancellationToken ct)
    {
        if (cacheLoaded) return;
        await loadGate.WaitAsync(ct);
        try
        {
            if (cacheLoaded) return;
            if (File.Exists(cachePath))
            {
                try
                {
                    await using var input = File.OpenRead(cachePath);
                    var cached = await JsonSerializer.DeserializeAsync<IndexerPopularityCache>(input, cancellationToken: ct);
                    if (cached is not null)
                    {
                        media = cached.Media ?? [];
                        lastUpdated = cached.LastUpdated;
                        cachedSourceSignature = cached.SourceSignature;
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                { logger.LogWarning(ex, "Could not read the indexer popularity cache; a fresh one will be generated."); }
            }
            cacheLoaded = true;
        }
        finally { loadGate.Release(); }
    }

    private async Task SaveCacheAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temp = cachePath + ".tmp";
        await using (var output = File.Create(temp))
            await JsonSerializer.SerializeAsync(output, new IndexerPopularityCache(media, lastUpdated, cachedSourceSignature), cancellationToken: ct);
        File.Move(temp, cachePath, overwrite: true);
    }

    private async Task<string> GetSourceSignatureAsync(CancellationToken ct)
    {
        var torznab = await catalogs.GetAll(ct);
        var cardigann = await selectedIndexers.GetAllAsync(ct);
        var raw = string.Join("\n", torznab.Select(item => $"t|{item.Name}|{item.Url}|{item.RightsConfirmed}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.ApiKey)))}")
            .Concat(cardigann.Select(item => $"c|{item.DefinitionId}|{item.Name}|{item.RightsConfirmed}"))
            .Order(StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static IndexerPopularMedia? ParseTrendingMedia(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id)) return null;
        var type = item.TryGetProperty("media_type", out var typeValue) ? typeValue.GetString() : null;
        if (type is not ("movie" or "tv")) return null;
        var title = ReadText(item, type == "movie" ? "title" : "name");
        var original = ReadText(item, type == "movie" ? "original_title" : "original_name");
        if (string.IsNullOrWhiteSpace(title)) return null;
        var date = ReadText(item, type == "movie" ? "release_date" : "first_air_date");
        int? year = date is { Length: >= 4 } && int.TryParse(date[..4], out var parsed) ? parsed : null;
        return new IndexerPopularMedia(id, type, title, original, ReadText(item, "overview"),
            ImageUrl(ReadText(item, "poster_path"), "w500"), ImageUrl(ReadText(item, "backdrop_path"), "w1280"),
            ReadText(item, "original_language"), year,
            item.TryGetProperty("vote_average", out var rating) && rating.TryGetDouble(out var score) ? score : 0,
            item.TryGetProperty("popularity", out var popularity) && popularity.TryGetDouble(out var rank) ? rank : 0,
            0, 0, 0, 0, []);
    }

    private static bool MatchesTitle(IndexerPopularMedia item, string resultTitle)
    {
        var normalized = Normalize(resultTitle);
        var tokens = Normalize(item.OriginalTitle ?? item.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) tokens = Normalize(item.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matched = tokens.Count(token => normalized.Contains(token, StringComparison.Ordinal));
        return matched > 0 && (tokens.Length == 1 || matched * 2 >= tokens.Length)
            && (item.Year is null || resultTitle.Contains(item.Year.Value.ToString(), StringComparison.OrdinalIgnoreCase)
                || item.MediaType == "tv");
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    private static string? ReadText(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? ImageUrl(string? path, string size) => string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/{size}{path}";
    private static int ParseCount(string? value) => int.TryParse(value, out var result) ? Math.Max(0, result) : 0;

    private sealed record IndexerPopularityCache(List<IndexerPopularMedia>? Media, DateTimeOffset? LastUpdated, string? SourceSignature);
}

public sealed record IndexerPopularMedia(int Id, string MediaType, string Title, string? OriginalTitle, string? Overview,
    string? Poster, string? Backdrop, string? OriginalLanguage, int? Year, double Rating, double TmdbPopularity,
    int AvailableSources, int Seeders, int Grabs, long PopularityScore, string[] IndexerNames);
public sealed record IndexerPopularitySnapshot(List<IndexerPopularMedia> Results, DateTimeOffset? LastUpdated,
    bool IsRefreshing, string? Error, double RefreshIntervalHours);
