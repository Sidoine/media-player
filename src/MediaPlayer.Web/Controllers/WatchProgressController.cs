using media_player.Services;
using Microsoft.AspNetCore.Mvc;

namespace media_player.Controllers;

[ApiController]
[Route("api/watch-progress")]
public sealed class WatchProgressController(WatchProgressStore progress, MediaDownloadService downloads, CachedMediaAssociationStore associationStore) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var associations = await associationStore.GetLibraryAssociationsAsync(downloads.GetCompletedMediaFiles(), ct);
        var userName = User.Identity?.Name;
        await progress.ReconcileLibraryProgressAsync(associations, userName, ct);
        return Ok(await progress.GetAllAsync(userName, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Save(PlaybackProgressRequest input, CancellationToken ct)
    {
        if (input.TmdbId <= 0 || input.MediaType is not ("movie" or "tv") || string.IsNullOrWhiteSpace(input.Title)) return BadRequest();
        await progress.SaveAsync(input.TmdbId, input.MediaType, input.Title, input.CurrentTimeSeconds, input.DurationSeconds, input.PosterUrl,
            input.SeasonNumber, input.EpisodeNumber, ct, input.LibraryPath, User.Identity?.Name);
        return NoContent();
    }
}

public sealed record PlaybackProgressRequest(int TmdbId, string? MediaType, string Title, double CurrentTimeSeconds, double DurationSeconds,
    string? PosterUrl = null, int? SeasonNumber = null, int? EpisodeNumber = null, string? LibraryPath = null);
