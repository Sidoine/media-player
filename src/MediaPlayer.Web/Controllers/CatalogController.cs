using System.Text.Json;
using media_player.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace media_player.Controllers;

[ApiController]
[Route("api")]
public sealed class CatalogController(
    IHttpClientFactory clients,
    ApiCredentialStore credentials,
    TmdbImageCacheService images,
    TorrentCatalogSearchService search,
    IndexerPopularityService popularity,
    MediaDownloadService downloads,
    MediaWatchlistStore watchlist) : ControllerBase
{
    [HttpGet("catalog")]
    [OutputCache(PolicyName = "Catalog")]
    public async Task<IActionResult> GetCatalog([FromQuery] string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credentials.TmdbToken))
            return Ok(new { configured = false, results = Array.Empty<object>() });
        var route = string.IsNullOrWhiteSpace(query)
            ? $"trending/all/week?language={TmdbApiHelpers.Language()}"
            : $"search/multi?query={Uri.EscapeDataString(query)}&include_adult=false&language={TmdbApiHelpers.Language()}";
        try
        {
            using var response = await clients.CreateClient("tmdb").GetAsync(route, ct);
            if (!response.IsSuccessStatusCode) return Problem("TMDB n’a pas répondu correctement.", statusCode: (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var results = json.RootElement.GetProperty("results").EnumerateArray()
                .Where(item => !item.TryGetProperty("media_type", out var type) || type.GetString() != "person")
                .Select(item => new
                {
                    id = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                    title = item.TryGetProperty("title", out var title) ? title.GetString() : item.TryGetProperty("name", out var name) ? name.GetString() : "Sans titre",
                    overview = item.TryGetProperty("overview", out var overview) ? overview.GetString() : "",
                    poster = item.TryGetProperty("poster_path", out var poster) && poster.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(poster.GetString(), "w500") : null,
                    backdrop = item.TryGetProperty("backdrop_path", out var backdrop) && backdrop.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(backdrop.GetString(), "original") : null,
                    mediaType = item.TryGetProperty("media_type", out var mediaType) ? mediaType.GetString() : "movie",
                    rating = item.TryGetProperty("vote_average", out var rating) ? rating.GetDouble() : 0,
                    originalLanguage = item.TryGetProperty("original_language", out var language) ? language.GetString() : null,
                    year = TmdbApiHelpers.ReadMediaYear(item)
                }).ToArray();
            return Ok(new { configured = true, results });
        }
        catch (HttpRequestException) { return Problem("Impossible de joindre TMDB.", statusCode: 502); }
    }

    [HttpGet("catalog/genre/{mediaType}/{genreId:int}")]
    [OutputCache(PolicyName = "Catalog")]
    public async Task<IActionResult> GetGenre(string mediaType, int genreId, CancellationToken ct)
    {
        if (mediaType is not ("movie" or "tv") || genreId <= 0) return BadRequest();
        if (string.IsNullOrWhiteSpace(credentials.TmdbToken)) return Ok(new { configured = false, results = Array.Empty<object>() });
        var route = $"discover/{mediaType}?with_genres={genreId}&sort_by=popularity.desc&include_adult=false&language={TmdbApiHelpers.Language()}&page=1";
        try
        {
            using var response = await clients.CreateClient("tmdb").GetAsync(route, ct);
            if (!response.IsSuccessStatusCode) return Problem("TMDB n’a pas répondu correctement.", statusCode: (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var results = json.RootElement.GetProperty("results").EnumerateArray().Select(item => new
            {
                id = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                title = item.TryGetProperty("title", out var title) ? title.GetString() : item.TryGetProperty("name", out var name) ? name.GetString() : "Sans titre",
                overview = item.TryGetProperty("overview", out var overview) ? overview.GetString() : "",
                poster = item.TryGetProperty("poster_path", out var poster) && poster.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(poster.GetString(), "w500") : null,
                backdrop = item.TryGetProperty("backdrop_path", out var backdrop) && backdrop.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(backdrop.GetString(), "original") : null,
                mediaType,
                rating = item.TryGetProperty("vote_average", out var rating) ? rating.GetDouble() : 0,
                popularity = item.TryGetProperty("popularity", out var popularity) ? popularity.GetDouble() : 0,
                originalLanguage = item.TryGetProperty("original_language", out var language) ? language.GetString() : null,
                year = TmdbApiHelpers.ReadMediaYear(item)
            }).ToArray();
            return Ok(new { configured = true, results });
        }
        catch (HttpRequestException) { return Problem("Impossible de joindre le catalogue TMDB.", statusCode: 502); }
    }

    [HttpGet("catalog/{mediaType}/{id:int}")]
    [OutputCache(PolicyName = "Catalog")]
    public async Task<IActionResult> GetDetails(string mediaType, int id, CancellationToken ct)
    {
        if (mediaType is not ("movie" or "tv") || id <= 0) return BadRequest();
        if (string.IsNullOrWhiteSpace(credentials.TmdbToken)) return StatusCode(503, new { configured = false });
        try
        {
            using var response = await clients.CreateClient("tmdb").GetAsync($"{mediaType}/{id}?language={TmdbApiHelpers.Language()}&append_to_response=videos,credits", ct);
            if (!response.IsSuccessStatusCode) return Problem("TMDB n’a pas répondu correctement.", statusCode: (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var item = json.RootElement;
            string? Text(string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            int? Number(string property) => item.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
            var genres = item.TryGetProperty("genres", out var genreList) && genreList.ValueKind == JsonValueKind.Array
                ? genreList.EnumerateArray().Select(genre => genre.TryGetProperty("name", out var name) ? name.GetString() : null).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray()
                : Array.Empty<string?>();
            var runtime = Number("runtime");
            if (runtime is null && item.TryGetProperty("episode_run_time", out var runtimes) && runtimes.ValueKind == JsonValueKind.Array && runtimes.GetArrayLength() > 0 && runtimes[0].TryGetInt32(out var episodeRuntime)) runtime = episodeRuntime;
            var title = Text("title") ?? Text("name") ?? "Sans titre";
            var poster = Text("poster_path");
            var backdrop = Text("backdrop_path");
            var credits = item.TryGetProperty("credits", out var creditData) ? creditData : default;
            var directors = credits.ValueKind == JsonValueKind.Object && credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array
                ? crew.EnumerateArray().Where(person => person.TryGetProperty("job", out var job) && job.GetString() == "Director")
                    .Select(person => person.TryGetProperty("name", out var name) ? name.GetString() : null).Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray() : [];
            var creators = item.TryGetProperty("created_by", out var creatorList) && creatorList.ValueKind == JsonValueKind.Array
                ? creatorList.EnumerateArray().Select(person => person.TryGetProperty("name", out var name) ? name.GetString() : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray() : [];
            var cast = credits.ValueKind == JsonValueKind.Object && credits.TryGetProperty("cast", out var castList) && castList.ValueKind == JsonValueKind.Array
                ? castList.EnumerateArray().Select(person => person.TryGetProperty("name", out var name) ? name.GetString() : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray() : [];
            var seasons = mediaType == "tv" && item.TryGetProperty("seasons", out var seasonList) && seasonList.ValueKind == JsonValueKind.Array
                ? seasonList.EnumerateArray().Select(season => new
                {
                    seasonNumber = season.TryGetProperty("season_number", out var seasonNumber) ? seasonNumber.GetInt32() : 0,
                    name = season.TryGetProperty("name", out var seasonName) ? seasonName.GetString() : "Saison",
                    episodeCount = season.TryGetProperty("episode_count", out var count) ? count.GetInt32() : 0,
                    poster = season.TryGetProperty("poster_path", out var seasonPoster) && seasonPoster.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(seasonPoster.GetString(), "w500") : null
                }).ToArray() : [];
            string? trailerUrl = null;
            if (item.TryGetProperty("videos", out var videos) && videos.TryGetProperty("results", out var videoList) && videoList.ValueKind == JsonValueKind.Array)
            {
                var trailer = videoList.EnumerateArray().Where(video => video.TryGetProperty("site", out var site) && site.GetString() == "YouTube"
                    && video.TryGetProperty("type", out var videoType) && videoType.GetString() == "Trailer"
                    && video.TryGetProperty("key", out var key) && !string.IsNullOrWhiteSpace(key.GetString()))
                    .OrderByDescending(video => video.TryGetProperty("iso_639_1", out var language) && language.GetString() == "fr")
                    .ThenByDescending(video => video.TryGetProperty("official", out var official) && official.ValueKind == JsonValueKind.True).FirstOrDefault();
                if (trailer.ValueKind == JsonValueKind.Object && trailer.TryGetProperty("key", out var trailerKey))
                    trailerUrl = $"https://www.youtube-nocookie.com/embed/{Uri.EscapeDataString(trailerKey.GetString()!)}?autoplay=1&rel=0";
            }
            return Ok(new
            {
                id, title, originalTitle = Text(mediaType == "movie" ? "original_title" : "original_name"), overview = Text("overview"),
                poster = TmdbImageCacheService.ToLocalUrl(poster, "w500"), backdrop = TmdbImageCacheService.ToLocalUrl(backdrop, "original"), mediaType,
                rating = item.TryGetProperty("vote_average", out var rating) && rating.TryGetDouble(out var score) ? score : 0,
                originalLanguage = Text("original_language"), year = TmdbApiHelpers.ReadMediaYear(item), genres, runtimeMinutes = runtime,
                seasonCount = Number("number_of_seasons"), episodeCount = Number("number_of_episodes"), seasons, trailerUrl, directors, creators, cast
            });
        }
        catch (HttpRequestException) { return Problem("Impossible de joindre TMDB.", statusCode: 502); }
    }

    [HttpGet("catalog/tv/{id:int}/season/{seasonNumber:int}")]
    [OutputCache(PolicyName = "Catalog")]
    public async Task<IActionResult> GetSeason(int id, int seasonNumber, CancellationToken ct)
    {
        if (id <= 0 || seasonNumber < 0) return BadRequest();
        if (string.IsNullOrWhiteSpace(credentials.TmdbToken)) return StatusCode(503, new { configured = false });
        try
        {
            using var response = await clients.CreateClient("tmdb").GetAsync($"tv/{id}/season/{seasonNumber}?language={TmdbApiHelpers.Language()}", ct);
            if (!response.IsSuccessStatusCode) return Problem("TMDB n’a pas répondu correctement.", statusCode: (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var season = json.RootElement;
            var episodes = season.TryGetProperty("episodes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(episode => new
                {
                    id = episode.TryGetProperty("id", out var episodeId) ? episodeId.GetInt32() : 0,
                    seasonNumber = episode.TryGetProperty("season_number", out var seasonValue) ? seasonValue.GetInt32() : seasonNumber,
                    episodeNumber = episode.TryGetProperty("episode_number", out var episodeValue) ? episodeValue.GetInt32() : 0,
                    title = episode.TryGetProperty("name", out var name) ? name.GetString() : "Épisode",
                    overview = episode.TryGetProperty("overview", out var overview) ? overview.GetString() : null,
                    still = episode.TryGetProperty("still_path", out var still) && still.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(still.GetString(), "w500") : null,
                    airDate = episode.TryGetProperty("air_date", out var date) ? date.GetString() : null,
                    runtimeMinutes = episode.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : (int?)null
                }).ToArray() : [];
            return Ok(new { seasonNumber, episodes });
        }
        catch (HttpRequestException) { return Problem("Impossible de récupérer les épisodes sur TMDB.", statusCode: 502); }
    }

    [HttpGet("catalog/image/{size}/{fileName}")]
    [OutputCache(PolicyName = "CatalogImages")]
    public async Task<IActionResult> GetImage(string size, string fileName, CancellationToken ct)
    {
        var image = await images.GetCachedImageAsync(size, fileName, ct);
        return image is null ? NotFound() : PhysicalFile(image.Value.Path, image.Value.ContentType);
    }

    [HttpGet("torrent-catalogs/search")]
    public async Task<IActionResult> Search(string query, string? sourceType, Guid? sourceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) return BadRequest(new { error = "Saisissez un titre (200 caractères maximum)." });
        if (sourceType is not null && (sourceType is not ("torznab" or "cardigann") || sourceId is null))
            return BadRequest(new { error = "L’indexeur à tester est invalide." });
        return Ok(await search.SearchAsync(query, ct, sourceType, sourceId));
    }

    [HttpGet("catalog/indexer-popular")]
    public async Task<IActionResult> GetPopular(CancellationToken ct)
    {
        var snapshot = await popularity.GetSnapshotAsync(ct);
        return Ok(snapshot with { Results = snapshot.Results.Select(item => item with
        {
            Poster = TmdbImageCacheService.ToLocalUrlFromRemote(item.Poster),
            Backdrop = TmdbImageCacheService.ToLocalUrlFromRemote(item.Backdrop)
        }).ToList() });
    }

    [HttpGet("catalog/local-status")]
    public async Task<IActionResult> GetLocalStatus(CancellationToken ct) =>
        Ok(new { downloads = downloads.GetMediaStatuses(), watchlist = await watchlist.GetAllAsync(ct) });
}

internal static class TmdbApiHelpers
{
    public static string Language() => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr-FR" : "en-US";

    public static int? ReadMediaYear(JsonElement media)
    {
        var date = media.TryGetProperty("release_date", out var release) ? release.GetString()
            : media.TryGetProperty("first_air_date", out var firstAir) ? firstAir.GetString() : null;
        return date is { Length: >= 4 } && int.TryParse(date[..4], out var year) ? year : null;
    }
}
