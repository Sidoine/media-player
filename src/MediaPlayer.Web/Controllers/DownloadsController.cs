using media_player.Services;
using Microsoft.AspNetCore.Mvc;

namespace media_player.Controllers;

[ApiController]
[Route("api/downloads")]
public sealed class DownloadsController(MediaDownloadService downloads, SubtitleService subtitles, TorrentCatalogStore catalogs,
    SelectedIndexerStore selectedIndexers, MediaWatchlistStore watchlist, ILogger<DownloadsController> logger) : ControllerBase
{
    [HttpGet("{tmdbId:int}/dht-diagnostic")]
    public IActionResult GetDhtDiagnostic(int tmdbId, [FromQuery] string? mediaType, [FromQuery] int? seasonNumber, [FromQuery] int? episodeNumber)
    {
        var diagnostic = downloads.GetDhtDiagnostic(tmdbId, mediaType ?? "movie", seasonNumber, episodeNumber);
        return diagnostic is null ? NotFound() : Ok(diagnostic);
    }

    [HttpGet("{tmdbId:int}")]
    public async Task<IActionResult> GetStatus(int tmdbId, [FromQuery] string? mediaType, [FromQuery] int? seasonNumber, [FromQuery] int? episodeNumber, CancellationToken ct)
    {
        var status = await downloads.GetStatusAsync(tmdbId, mediaType ?? "movie", seasonNumber, episodeNumber, ct);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpGet("{tmdbId:int}/subtitles")]
    public async Task<IActionResult> GetSubtitle(int tmdbId, [FromQuery] string? mediaType, [FromQuery] int? seasonNumber, [FromQuery] int? episodeNumber, CancellationToken ct)
    {
        var type = mediaType ?? "movie";
        var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
        if (status is null || string.Equals(status.OriginalLanguage, subtitles.ResolvedLanguage, StringComparison.OrdinalIgnoreCase)) return NotFound();
        var videoPath = GetVideoPath(tmdbId, type, seasonNumber, episodeNumber);
        if (videoPath is null) return NotFound();
        var content = await subtitles.GetSubtitleAsync(videoPath, status.Title, ct);
        return content is null ? NotFound() : File(content, "text/vtt; charset=utf-8");
    }

    [HttpGet("{tmdbId:int}/subtitles/options")]
    public async Task<IActionResult> GetSubtitleOptions(int tmdbId, [FromQuery] string? mediaType, [FromQuery] int? seasonNumber,
        [FromQuery] int? episodeNumber, [FromQuery] string? language, CancellationToken ct)
    {
        var type = mediaType ?? "movie";
        var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
        if (status is null) return NotFound();
        var videoPath = GetVideoPath(tmdbId, type, seasonNumber, episodeNumber);
        if (videoPath is null) return NotFound();
        var selectedLanguage = language is "en" or "fr" ? language : subtitles.ResolvedLanguage;
        var choices = await subtitles.GetChoicesAsync(videoPath, selectedLanguage, ct);
        return Ok(new { language = selectedLanguage, subtitlesConfigured = subtitles.IsConfigured, videoFileName = Path.GetFileName(videoPath), results = choices });
    }

    [HttpPost("{tmdbId:int}/subtitles/select")]
    public async Task<IActionResult> SelectSubtitle(int tmdbId, [FromQuery] string? mediaType, [FromQuery] int? seasonNumber,
        [FromQuery] int? episodeNumber, SubtitleSelectionInput input, CancellationToken ct)
    {
        var type = mediaType ?? "movie";
        var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
        if (status is null) return NotFound();
        var videoPath = GetVideoPath(tmdbId, type, seasonNumber, episodeNumber);
        if (videoPath is null) return NotFound();
        var language = string.IsNullOrWhiteSpace(input.Language) ? subtitles.ResolvedLanguage : input.Language;
        if (!MediaStorageSettingsStore.IsSupportedSubtitleLanguage(language)) return BadRequest();
        var selected = await subtitles.SelectAsync(videoPath, input.Key, language, ct);
        return selected ? Ok(new { key = input.Key }) : NotFound();
    }

    [HttpGet("{tmdbId:int}/subtitles/selected/{key}")]
    public async Task<IActionResult> GetSelectedSubtitle(int tmdbId, string key, [FromQuery] string? mediaType, [FromQuery] int? seasonNumber,
        [FromQuery] int? episodeNumber, [FromQuery] string? language, CancellationToken ct)
    {
        var type = mediaType ?? "movie";
        var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
        if (status is null) return NotFound();
        var videoPath = GetVideoPath(tmdbId, type, seasonNumber, episodeNumber);
        if (videoPath is null) return NotFound();
        var selectedLanguage = string.IsNullOrWhiteSpace(language) ? subtitles.ResolvedLanguage : language;
        var content = await subtitles.ReadSelectedAsync(videoPath, key, selectedLanguage, ct);
        return content is null ? NotFound() : File(content, "text/vtt; charset=utf-8");
    }

    [HttpGet("{tmdbId:int}/stream")]
    public async Task<IActionResult> Stream(int tmdbId, [FromQuery] string? mediaType, [FromQuery] int? seasonNumber, [FromQuery] int? episodeNumber, CancellationToken ct)
    {
        var type = mediaType ?? "movie";
        var cachedPath = downloads.GetCachedVideoPath(tmdbId, type, seasonNumber, episodeNumber);
        if (cachedPath is not null) return PhysicalFile(cachedPath, MediaDownloadService.ContentTypeFor(cachedPath), enableRangeProcessing: true);
        var videoPath = downloads.GetActiveVideoPath(tmdbId, type, seasonNumber, episodeNumber);
        if (videoPath is null) return Conflict(new { error = "La vidéo sera lisible dès que les métadonnées du torrent seront disponibles." });
        try
        {
            var stream = await downloads.CreateVideoStreamAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
            return File(stream, MediaDownloadService.ContentTypeFor(videoPath));
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet]
    public IActionResult GetActiveDownloads() => Ok(downloads.GetActiveDownloads());

    [HttpDelete("{tmdbId:int}")]
    public async Task<IActionResult> Cancel(int tmdbId, [FromQuery] string? mediaType, [FromQuery] int? seasonNumber, [FromQuery] int? episodeNumber, CancellationToken ct) =>
        await downloads.CancelAsync(tmdbId, mediaType ?? "movie", seasonNumber, episodeNumber, ct) ? NoContent() : NotFound();

    [HttpPost]
    public async Task<IActionResult> Start(MediaDownloadRequest input, CancellationToken ct)
    {
        var hasRights = (await catalogs.GetAll(ct)).Any(source => source.RightsConfirmed && string.Equals(source.Name, input.Source, StringComparison.OrdinalIgnoreCase))
            || (await selectedIndexers.GetAllAsync(ct)).Any(source => source.RightsConfirmed && string.Equals(source.Name, input.Source, StringComparison.OrdinalIgnoreCase));
        if (!hasRights) return BadRequest(new { error = "La source n’est pas configurée avec une attestation de droits." });
        try
        {
            var status = await downloads.StartAsync(input with { RightsConfirmed = true }, ct);
            var watchKey = MediaWatchlistEntry.CreateKey(input.TmdbId, input.MediaType, input.SeasonNumber, input.EpisodeNumber);
            try { await watchlist.RemoveAsync(watchKey, ct); }
            catch (IOException ex) { logger.LogWarning(ex, "Download started for {WatchKey}, but its watchlist entry could not be removed.", watchKey); }
            return Accepted($"/api/downloads/{input.TmdbId}?mediaType={Uri.EscapeDataString(input.MediaType)}&seasonNumber={input.SeasonNumber}&episodeNumber={input.EpisodeNumber}", status);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private string? GetVideoPath(int tmdbId, string type, int? seasonNumber, int? episodeNumber) =>
        downloads.GetCachedVideoPath(tmdbId, type, seasonNumber, episodeNumber) ?? downloads.GetActiveVideoPath(tmdbId, type, seasonNumber, episodeNumber);
}

public sealed record SubtitleSelectionInput(string Key, string? Language = null);
