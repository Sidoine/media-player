using System.Text.Json;

namespace media_player.Services;

/// <summary>In-memory API fixtures used only by the isolated Playwright environment.</summary>
public sealed class PlaywrightApiMockMiddleware(RequestDelegate next)
{
    private static readonly DateTimeOffset UpdatedAt = DateTimeOffset.UtcNow;
    private static readonly MediaFixture[] Media =
    [
        Fixture(1283515, "Verity", "movie", 2026, "en", 7.0, 100),
        Fixture(125988, "Silo", "tv", 2023, "en", 8.2, 90),
        Fixture(2316, "The Office", "tv", 2005, "en", 8.6, 80),
        Fixture(2190, "South Park", "tv", 1997, "en", 8.3, 70)
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", out var path))
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";
        var value = await GetResponseAsync(context, path.Value ?? "/", context.RequestAborted);
        if (value is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("{}", context.RequestAborted);
            return;
        }

        if (value is MockResponse result)
        {
            context.Response.StatusCode = result.StatusCode;
            if (result.Json is not null) await context.Response.WriteAsync(result.Json, context.RequestAborted);
            return;
        }

        await JsonSerializer.SerializeAsync(context.Response.Body, value, cancellationToken: context.RequestAborted);
    }

    private static async Task<object?> GetResponseAsync(HttpContext context, string path, CancellationToken ct)
    {
        var method = context.Request.Method;
        if (path == "/configuration/credentials" && method == HttpMethods.Get)
            return new { tmdbConfigured = true, tmdbSource = "local", openSubtitlesConfigured = true, openSubtitlesSource = "local", allConfigured = true };
        if (path == "/configuration/media" && method == HttpMethods.Get)
            return new { cacheRoot = "C:\\mock\\media", activeCacheRoot = "C:\\mock\\media", cacheMaxBytes = 536870912000L, usedBytes = 0L, shareRatioLimit = (double?)1, preferredResolution = 1080, enableUpnp = false, activeEnableUpnp = false, requiresRestart = false };
        if (path == "/configuration/media" && method == HttpMethods.Put)
            return new MockResponse(StatusCodes.Status200OK, "{\"cacheRoot\":\"C:\\\\mock\\\\media\",\"activeCacheRoot\":\"C:\\\\mock\\\\media\",\"cacheMaxBytes\":536870912000,\"usedBytes\":0,\"shareRatioLimit\":1,\"preferredResolution\":1080,\"enableUpnp\":false,\"activeEnableUpnp\":false,\"requiresRestart\":false}");
        if (path == "/bittorrent/trackers" && method == HttpMethods.Get) return new { urls = Array.Empty<string>() };
        if (path == "/indexer-definitions/status") return new { available = true, definitionCount = 1, fetchedAtUtc = UpdatedAt, archiveSha256 = "fixture", sourceUrl = "mock://indexers", definitionVersion = "playwright" };
        if (path == "/indexer-definitions" || path == "/selected-indexers" || path == "/torrent-catalogs" || path == "/downloads" || path == "/watchlist")
            return method == HttpMethods.Get ? Array.Empty<object>() : new MockResponse(StatusCodes.Status200OK, "{}");
        if (path == "/library") return new { items = Array.Empty<object>() };
        if (path == "/catalog/indexer-popular") return new
        {
            results = new[] { new { id = 1283515, mediaType = "movie", title = "Verity", originalTitle = "Verity", overview = "Un thriller psychologique.", poster = (string?)null, backdrop = (string?)null, originalLanguage = "en", year = 2026, rating = 7d, tmdbPopularity = 100d, availableSources = 1, seeders = 8, grabs = 0, popularityScore = 8L, indexerNames = new[] { "Indexeur test" } } },
            lastUpdated = UpdatedAt, isRefreshing = false, error = (string?)null, refreshIntervalHours = 8
        };
        if (path == "/catalog/local-status") return new
        {
            downloads = new[]
            {
                new { tmdbId = 1283515, mediaType = "movie", title = "Verity", originalLanguage = "en", state = "Disponible", progress = 100d, downloadRate = 0L, directory = "", completed = true, sourceTitle = "Indexeur test", availablePeers = 0, trackerCount = 0, dhtState = "indisponible", seasonNumber = (int?)null, episodeNumber = (int?)null, bufferedBytes = 0L, requiredBufferBytes = 0L, canStream = true },
                new { tmdbId = 2316, mediaType = "tv", title = "The Office — S01E02", originalLanguage = "en", state = "Téléchargement en cours", progress = 36d, downloadRate = 0L, directory = "", completed = false, sourceTitle = "Indexeur test", availablePeers = 3, trackerCount = 1, dhtState = "active", seasonNumber = (int?)1, episodeNumber = (int?)2, bufferedBytes = 0L, requiredBufferBytes = 0L, canStream = false }
            },
            watchlist = new[]
            {
                new { key = "tv-125988-s01e01", tmdbId = 125988, mediaType = "tv", title = "Silo", originalLanguage = "en", year = (int?)2023, seasonNumber = (int?)1, episodeNumber = (int?)1, episodeTitle = "La Flamme", runtimeMinutes = (int?)45, addedAt = UpdatedAt, lastCheckedAt = (DateTimeOffset?)null }
            }
        };
        if (path == "/watch-progress") return new[]
        {
            new { tmdbId = 1283515, mediaType = "movie", title = "Verity", currentTimeSeconds = 419d, durationSeconds = 7257d, progressPercent = 5.78d, updatedAt = UpdatedAt, posterUrl = (string?)null, seasonNumber = (int?)null, episodeNumber = (int?)null, isCompleted = false },
            new { tmdbId = 125988, mediaType = "tv", title = "Silo — S01E02 · Holston", currentTimeSeconds = 600d, durationSeconds = 2700d, progressPercent = 22.22d, updatedAt = UpdatedAt, posterUrl = (string?)null, seasonNumber = (int?)1, episodeNumber = (int?)2, isCompleted = false }
        };
        if (path.StartsWith("/catalog/tv/125988/season/1", StringComparison.Ordinal)) return new
        {
            seasonNumber = 1,
            episodes = new[]
            {
                new { id = 7001, seasonNumber = 1, episodeNumber = 1, title = "La Flamme", overview = "Un premier secret est révélé.", still = (string?)null, airDate = "2023-05-05", runtimeMinutes = 45 },
                new { id = 7002, seasonNumber = 1, episodeNumber = 2, title = "Holston", overview = "L’enquête continue.", still = (string?)null, airDate = "2023-05-05", runtimeMinutes = 43 }
            }
        };
        if (path.StartsWith("/catalog/genre/", StringComparison.Ordinal))
        {
            var mediaType = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) ?? "movie";
            var results = Media.Where(item => mediaType == "movie" ? item.MediaType == "movie" : item.MediaType == "tv").ToArray();
            return new { configured = true, results };
        }
        if (path == "/catalog") return new { configured = true, results = Media };
        if (path.StartsWith("/catalog/movie/1283515", StringComparison.Ordinal)) return MovieDetails;
        if (path.StartsWith("/catalog/tv/125988", StringComparison.Ordinal)) return SeriesDetails;
        if (path.StartsWith("/catalog/", StringComparison.Ordinal))
        {
            var id = path.Split('/').LastOrDefault();
            if (id == "2316") return SeriesDetails with { Id = 2316, Title = "The Office", Year = 2005, OriginalTitle = "The Office" };
            if (id == "2190") return SeriesDetails with { Id = 2190, Title = "South Park", Year = 1997, OriginalTitle = "South Park" };
        }
        if (path.StartsWith("/downloads/", StringComparison.Ordinal) && method == HttpMethods.Get)
            return new MockResponse(StatusCodes.Status404NotFound, null);
        if (path.StartsWith("/torrent-catalogs/search", StringComparison.Ordinal)) return new { results = Array.Empty<object>(), errors = Array.Empty<string>() };
        if (path == "/configuration/credentials" && method == HttpMethods.Put) return new MockResponse(StatusCodes.Status200OK, "{\"tmdbConfigured\":true,\"tmdbSource\":\"local\",\"openSubtitlesConfigured\":true,\"openSubtitlesSource\":\"local\",\"allConfigured\":true}");
        if (path == "/bittorrent/trackers" && method == HttpMethods.Put) return new { urls = Array.Empty<string>() };
        if (path.StartsWith("/torrent-catalogs", StringComparison.Ordinal) && method == HttpMethods.Post) return new MockResponse(StatusCodes.Status201Created, "{}");
        if (path.StartsWith("/selected-indexers", StringComparison.Ordinal) && method == HttpMethods.Post) return new MockResponse(StatusCodes.Status201Created, "{}");
        if (method is "DELETE" or "POST" or "PUT") return new MockResponse(StatusCodes.Status204NoContent, null);

        await Task.CompletedTask;
        return null;
    }

    private static MediaFixture Fixture(int id, string title, string mediaType, int year, string language, double rating, double popularity) =>
        new(id, title, mediaType == "movie" ? "Un thriller psychologique." : "Dans un monde souterrain.", null, null,
            mediaType, rating, language, year, title, ["Drame"], mediaType == "movie" ? 121 : 45,
            mediaType == "movie" ? null : 1, mediaType == "movie" ? null : 2,
            mediaType == "movie" ? [] : [new SeasonFixture(1, "Saison 1", 2, null)], null, popularity);

    private static readonly MediaFixture MovieDetails = Fixture(1283515, "Verity", "movie", 2026, "en", 7.0, 100);
    private static readonly MediaFixture SeriesDetails = new(125988, "Silo", "Dans un monde souterrain.", null, null, "tv", 8.2, "en", 2023, "Silo", ["Science-fiction"], 45, 1, 10, [new SeasonFixture(1, "Saison 1", 2, null)], null);

    private sealed record MockResponse(int StatusCode, string? Json);
    private sealed record SeasonFixture(int SeasonNumber, string Name, int EpisodeCount, string? Poster);
    private sealed record MediaFixture(int Id, string Title, string? Overview, string? Poster, string? Backdrop, string MediaType,
        double Rating, string? OriginalLanguage, int? Year, string? OriginalTitle, List<string> Genres, int? RuntimeMinutes,
        int? SeasonCount, int? EpisodeCount, List<SeasonFixture> Seasons, string? TrailerUrl, double Popularity = 0);
}
