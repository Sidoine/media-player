using System.Text.RegularExpressions;

namespace media_player.Services;

public sealed class MediaAvailabilityWorker(
    MediaWatchlistStore watchlist,
    TorrentCatalogSearchService search,
    MediaDownloadService downloads,
    IConfiguration configuration,
    ILogger<MediaAvailabilityWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = Math.Clamp(configuration.GetValue("Media:WatchlistCheckIntervalHours", 8), 1, 168);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckWatchlistAsync(stoppingToken);
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public Task CheckNowAsync(MediaWatchlistEntry entry, CancellationToken ct)
    {
        _ = Task.Run(() => CheckEntryAsync(entry, CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task CheckWatchlistAsync(CancellationToken ct)
    {
        foreach (var item in await watchlist.GetAllAsync(ct))
            await CheckEntryAsync(item, ct);
    }

    private async Task CheckEntryAsync(MediaWatchlistEntry item, CancellationToken ct)
    {
        try
        {
            var alreadyAvailable = await downloads.GetStatusAsync(item.TmdbId, item.MediaType, item.SeasonNumber, item.EpisodeNumber, ct);
            if (alreadyAvailable is not null)
            {
                await watchlist.RemoveAsync(item.Key, ct);
                return;
            }

            var response = await search.SearchAsync(BuildQuery(item), ct);
            var choice = Rank(item, response.Results.Where(result => result.RightsConfirmed)).FirstOrDefault();
            await watchlist.MarkCheckedAsync(item.Key, ct);
            if (choice is null) return;

            var request = new MediaDownloadRequest(item.TmdbId, item.Title, item.MediaType, item.OriginalLanguage, item.Year,
                choice.Source, choice.Title, choice.Link, choice.Size, true, item.SeasonNumber, item.EpisodeNumber, item.RuntimeMinutes);
            await downloads.StartAsync(request, ct);
            await watchlist.RemoveAsync(item.Key, ct);
            logger.LogInformation("A watched media became available; started download for {WatchKey} from {Indexer}.", item.Key, choice.Source);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Availability check failed for {WatchKey}; it will be retried later.", item.Key); }
    }

    private static string BuildQuery(MediaWatchlistEntry item) => item.SeasonNumber is not null && item.EpisodeNumber is not null
        ? $"{item.Title} S{item.SeasonNumber:00}E{item.EpisodeNumber:00} {item.EpisodeTitle}".Trim()
        : item.Year is null ? item.Title : $"{item.Title} {item.Year}";

    private static IEnumerable<TorrentSearchResult> Rank(MediaWatchlistEntry item, IEnumerable<TorrentSearchResult> results)
    {
        var tokens = Normalize(item.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return results
            .Where(result => !string.IsNullOrWhiteSpace(result.Link) && MatchesMedia(item, tokens, result.Title)
                && (item.MediaType != "tv" || item.SeasonNumber is null || MatchesSeason(item, result.Title)))
            .OrderByDescending(result => IsOriginal(item.OriginalLanguage, result.Title))
            .ThenByDescending(result => Regex.IsMatch(result.Title, @"(?i)(?<!\d)1080p?(?!\d)"))
            .ThenByDescending(result => ParsePeers(result.Seeders) + ParsePeers(result.Leechers));
    }

    private static bool MatchesMedia(MediaWatchlistEntry item, string[] tokens, string title)
    {
        var normalized = Normalize(title);
        var matched = tokens.Count(token => normalized.Contains(token, StringComparison.Ordinal));
        if (tokens.Length == 0 || matched == 0 || (tokens.Length > 1 && matched * 2 < tokens.Length)) return false;
        return item.Year is null || title.Contains(item.Year.Value.ToString(), StringComparison.OrdinalIgnoreCase)
            || item.MediaType == "tv";
    }

    private static bool MatchesSeason(MediaWatchlistEntry item, string title)
    {
        var season = item.SeasonNumber!.Value.ToString();
        if (item.EpisodeNumber is not null && Regex.IsMatch(title,
            $@"(?i)(?:\bS0*{season}\s*E0*{item.EpisodeNumber}\b|\b{season}\s*[xX]\s*0*{item.EpisodeNumber}\b)")) return true;
        if (Regex.IsMatch(title, $@"(?i)\b(?:S|Season|Saison)\s*0*{season}\b")) return true;
        if (Regex.IsMatch(title, @"(?i)\b(?:complete|full|integrale|intégrale)\s+(?:series|show|collection)\b|\ball\s+seasons\b|\btoutes\s+les\s+saisons\b")) return true;
        var range = Regex.Match(title, @"(?i)\bS0*(\d+)\s*[-–]\s*S?0*(\d+)\b");
        return range.Success && int.TryParse(range.Groups[1].Value, out var first) && int.TryParse(range.Groups[2].Value, out var last)
            && item.SeasonNumber >= first && item.SeasonNumber <= last;
    }

    private static bool IsOriginal(string language, string title)
    {
        var french = Regex.IsMatch(title, @"(?i)\b(FRENCH|TRUEFRENCH|VFF|VFQ|DUBBED)\b");
        return string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase) ? french : !french;
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    private static int ParsePeers(string? value) => int.TryParse(value, out var peers) ? peers : 0;
}
