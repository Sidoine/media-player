using System.Text.Json;
using media_player.Services;
using Microsoft.AspNetCore.Mvc;

namespace media_player.Controllers;

[ApiController]
[Route("api/watchlist")]
public sealed class WatchlistController(MediaWatchlistStore store, MediaAvailabilityWorker worker, WatchProgressStore progress,
    IHttpClientFactory clients, ILogger<WatchlistController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await store.GetAllAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Add(MediaWatchlistRequest input, CancellationToken ct)
    {
        if (input.TmdbId <= 0 || input.MediaType is not ("movie" or "tv") || string.IsNullOrWhiteSpace(input.Title)) return BadRequest();
        if (input.MediaType == "tv" && (input.SeasonNumber is null || input.EpisodeNumber is null))
        {
            try
            {
                var client = clients.CreateClient("tmdb");
                if (input.SeasonNumber is null)
                {
                    using var seriesResponse = await client.GetAsync($"tv/{input.TmdbId}?language=fr-FR", ct);
                    if (!seriesResponse.IsSuccessStatusCode) return StatusCode(502);
                    using var seriesJson = JsonDocument.Parse(await seriesResponse.Content.ReadAsStreamAsync(ct));
                    input = input with
                    {
                        SeasonNumber = seriesJson.RootElement.GetProperty("seasons").EnumerateArray()
                            .Where(season => season.TryGetProperty("episode_count", out var count) && count.TryGetInt32(out var number) && number > 0)
                            .Select(season => season.GetProperty("season_number").GetInt32()).Where(number => number > 0).Order().Select(number => (int?)number).FirstOrDefault()
                    };
                }
                if (input.SeasonNumber is null) return BadRequest(new { error = "Aucune saison avec des épisodes n’est disponible sur TMDB." });
                using var seasonResponse = await client.GetAsync($"tv/{input.TmdbId}/season/{input.SeasonNumber}?language=fr-FR", ct);
                if (!seasonResponse.IsSuccessStatusCode) return StatusCode(502);
                using var seasonJson = JsonDocument.Parse(await seasonResponse.Content.ReadAsStreamAsync(ct));
                var firstEpisode = seasonJson.RootElement.GetProperty("episodes").EnumerateArray()
                    .Where(episode => episode.TryGetProperty("episode_number", out var number) && number.TryGetInt32(out var episodeNumber) && episodeNumber > 0)
                    .OrderBy(episode => episode.GetProperty("episode_number").GetInt32()).FirstOrDefault();
                if (firstEpisode.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "Aucun épisode n’est disponible dans cette saison." });
                input = input with
                {
                    EpisodeNumber = firstEpisode.GetProperty("episode_number").GetInt32(),
                    EpisodeTitle = firstEpisode.TryGetProperty("name", out var episodeTitle) && episodeTitle.ValueKind == JsonValueKind.String ? episodeTitle.GetString() : input.EpisodeTitle,
                    RuntimeMinutes = firstEpisode.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : input.RuntimeMinutes
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not resolve the first episode for watchlist request {TmdbId}.", input.TmdbId);
                return StatusCode(502);
            }
        }
        var entry = new MediaWatchlistEntry(MediaWatchlistEntry.CreateKey(input.TmdbId, input.MediaType, input.SeasonNumber, input.EpisodeNumber),
            input.TmdbId, input.MediaType, input.Title, input.OriginalLanguage ?? "", input.Year, input.SeasonNumber, input.EpisodeNumber,
            input.EpisodeTitle, input.RuntimeMinutes, DateTimeOffset.UtcNow);
        var saved = await store.AddAsync(entry, ct);
        await worker.CheckNowAsync(saved, ct);
        return Ok(saved);
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Remove(string key, CancellationToken ct) =>
        await store.RemoveAsync(key, ct) ? NoContent() : NotFound();

    [HttpPost("next-episode")]
    public async Task<IActionResult> AddNextEpisode(NextEpisodeRequest input, CancellationToken ct)
    {
        if (input.TmdbId <= 0 || input.SeasonNumber < 0 || input.EpisodeNumber <= 0) return BadRequest();
        var client = clients.CreateClient("tmdb");
        try
        {
            using var seriesResponse = await client.GetAsync($"tv/{input.TmdbId}?language=fr-FR", ct);
            if (!seriesResponse.IsSuccessStatusCode) return StatusCode(502);
            using var seriesJson = JsonDocument.Parse(await seriesResponse.Content.ReadAsStreamAsync(ct));
            var seasons = seriesJson.RootElement.GetProperty("seasons").EnumerateArray().Where(season => season.GetProperty("episode_count").GetInt32() > 0)
                .Select(season => season.GetProperty("season_number").GetInt32()).OrderBy(number => number).ToArray();
            var watched = await progress.GetEpisodesAsync(input.TmdbId, User.Identity?.Name, ct);
            foreach (var seasonNumber in seasons.Where(number => number >= input.SeasonNumber))
            {
                using var seasonResponse = await client.GetAsync($"tv/{input.TmdbId}/season/{seasonNumber}?language=fr-FR", ct);
                if (!seasonResponse.IsSuccessStatusCode) continue;
                using var seasonJson = JsonDocument.Parse(await seasonResponse.Content.ReadAsStreamAsync(ct));
                foreach (var episode in seasonJson.RootElement.GetProperty("episodes").EnumerateArray().OrderBy(ep => ep.GetProperty("episode_number").GetInt32()))
                {
                    var episodeNumber = episode.GetProperty("episode_number").GetInt32();
                    if (seasonNumber < input.SeasonNumber || (seasonNumber == input.SeasonNumber && episodeNumber <= input.EpisodeNumber)) continue;
                    if (watched.Any(item => item.SeasonNumber == seasonNumber && item.EpisodeNumber == episodeNumber && item.IsCompleted)) continue;
                    var title = episode.TryGetProperty("name", out var titleValue) ? titleValue.GetString() : null;
                    var runtime = episode.TryGetProperty("runtime", out var runtimeValue) && runtimeValue.TryGetInt32(out var minutes) ? minutes : (int?)null;
                    var entry = new MediaWatchlistEntry(MediaWatchlistEntry.CreateKey(input.TmdbId, "tv", seasonNumber, episodeNumber),
                        input.TmdbId, "tv", input.Title, input.OriginalLanguage ?? "", input.Year, seasonNumber, episodeNumber, title, runtime, DateTimeOffset.UtcNow);
                    var saved = await store.AddAsync(entry, ct);
                    await worker.CheckNowAsync(saved, ct);
                    return Ok(saved);
                }
            }
            return NoContent();
        }
        catch (HttpRequestException) { return StatusCode(502); }
    }
}

public sealed record NextEpisodeRequest(int TmdbId, int SeasonNumber, int EpisodeNumber, string Title, string? OriginalLanguage, int? Year);
