using System.Net.Http.Headers;
using System.Text.Json;
using media_player.Components;
using media_player.Services;

var builder = WebApplication.CreateBuilder(args);
var playwrightMode = builder.Environment.IsEnvironment("Playwright") || builder.Configuration.GetValue("Playwright:Enabled", false);
builder.Services.AddSingleton<TorrentCatalogStore>();
builder.Services.AddSingleton<SelectedIndexerStore>();
builder.Services.AddSingleton<BittorrentTrackerStore>();
builder.Services.AddSingleton<MediaDownloadService>();
builder.Services.AddSingleton<WatchProgressStore>();
builder.Services.AddSingleton<TorrentCatalogSearchService>();
builder.Services.AddSingleton<MediaWatchlistStore>();
builder.Services.AddSingleton<MediaAvailabilityWorker>();
builder.Services.AddSingleton<IndexerPopularityService>();
builder.Services.AddSingleton<MediaPropertiesService>();
builder.Services.AddSingleton<ApiCredentialStore>();
if (!playwrightMode)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaAvailabilityWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerPopularityService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaDownloadService>());
}
builder.Services.AddHttpClient("opensubtitles", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<FrenchSubtitleService>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().BaseUri) });
builder.Services.AddHttpClient("indexer-definitions", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaPlayer/1.0 (+https://github.com/)");
});
builder.Services.AddHttpClient("cardigann", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaPlayer/1.0 (Cardigann-compatible search)");
});
builder.Services.AddSingleton(sp => new IndexerDefinitionCacheService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("indexer-definitions"),
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "indexer-definitions"),
    TimeSpan.FromDays(Math.Clamp(builder.Configuration.GetValue("IndexerDefinitions:RefreshIntervalDays", 7), 1, 365)),
    sp.GetRequiredService<ILogger<IndexerDefinitionCacheService>>()));
builder.Services.AddSingleton<CardigannSearchService>();
if (!builder.Environment.IsEnvironment("Testing") && !playwrightMode && builder.Configuration.GetValue("IndexerDefinitions:Enabled", true))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerDefinitionCacheService>());
builder.Services.AddHttpClient("tmdb", (sp, client) =>
{
    client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    var token = sp.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
});
var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
if (playwrightMode) app.UseMiddleware<PlaywrightApiMockMiddleware>();

app.MapGet("/api/configuration/credentials", (ApiCredentialStore credentials) => Results.Json(credentials.GetStatus()));
app.MapPut("/api/configuration/credentials", async (ApiCredentialsInput input, ApiCredentialStore credentials, CancellationToken ct) =>
{
    await credentials.SaveLocalAsync(input.TmdbReadAccessToken, input.OpenSubtitlesApiKey, ct);
    return Results.Json(credentials.GetStatus());
});

app.MapGet("/api/indexer-definitions/status", (IndexerDefinitionCacheService cache, CancellationToken ct) => cache.GetStatusAsync(ct));
app.MapPost("/api/indexer-definitions/refresh", (IndexerDefinitionCacheService cache, CancellationToken ct) => cache.RefreshAsync(ct));
app.MapGet("/api/indexer-definitions", (IndexerDefinitionCacheService cache, string? query, CancellationToken ct) => cache.GetDefinitionsAsync(query, ct));
app.MapGet("/api/bittorrent/trackers", async (BittorrentTrackerStore trackers, CancellationToken ct) => Results.Json(new { urls = await trackers.GetAllAsync(ct) }));
app.MapPut("/api/bittorrent/trackers", async (TrackerInput input, BittorrentTrackerStore trackers, CancellationToken ct) =>
{
    if (input.Urls.Count > 50) return Results.BadRequest(new { error = "Vous pouvez configurer jusqu’à 50 trackers." });
    var normalized = new List<string>();
    foreach (var value in input.Urls.Select(url => url.Trim()).Where(url => url.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("udp" or "http" or "https"))
            return Results.BadRequest(new { error = $"Adresse de tracker invalide : {value}" });
        normalized.Add(uri.ToString());
    }
    await trackers.SaveAsync(normalized, ct);
    return Results.Json(new { urls = normalized });
});
app.MapGet("/api/selected-indexers", async (SelectedIndexerStore store, CancellationToken ct) => Results.Json(await store.GetAllAsync(ct)));
app.MapPost("/api/selected-indexers", async (SelectedIndexerInput input, IndexerDefinitionCacheService cache, SelectedIndexerStore store, CancellationToken ct) =>
{
    if (!input.RightsConfirmed) return Results.BadRequest(new { error = "Confirmez que vous pouvez utiliser le média obtenu via cette source." });
    var definition = (await cache.GetDefinitionsAsync(cancellationToken: ct)).FirstOrDefault(x => string.Equals(x.Id, input.DefinitionId, StringComparison.OrdinalIgnoreCase));
    if (definition is null) return Results.NotFound(new { error = "Cette définition n’est pas présente dans le cache local." });
    var source = await store.AddAsync(new SelectedIndexer(Guid.NewGuid(), definition.Id, definition.Name, true, DateTimeOffset.UtcNow), ct);
    return Results.Created($"/api/selected-indexers/{source.Id}", source);
});
app.MapDelete("/api/selected-indexers/{id:guid}", async (Guid id, SelectedIndexerStore store, CancellationToken ct) =>
    await store.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());

app.MapGet("/api/catalog", async (IHttpClientFactory clients, string? query, CancellationToken ct) =>
{
    var token = app.Services.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { configured = false, results = Array.Empty<object>() });
    var route = string.IsNullOrWhiteSpace(query) ? "trending/all/week?language=fr-FR" : $"search/multi?query={Uri.EscapeDataString(query)}&include_adult=false&language=fr-FR";
    try
    {
        using var response = await clients.CreateClient("tmdb").GetAsync(route, ct);
        if (!response.IsSuccessStatusCode) return Results.Problem("TMDB n’a pas répondu correctement.", statusCode: (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var results = json.RootElement.GetProperty("results").EnumerateArray()
            .Where(x => !x.TryGetProperty("media_type", out var mt) || mt.GetString() != "person")
            .Select(x => new {
                id = x.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                title = x.TryGetProperty("title", out var t) ? t.GetString() : x.TryGetProperty("name", out var n) ? n.GetString() : "Sans titre",
                overview = x.TryGetProperty("overview", out var o) ? o.GetString() : "",
                poster = x.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String ? "https://image.tmdb.org/t/p/w500" + p.GetString() : null,
                backdrop = x.TryGetProperty("backdrop_path", out var b) && b.ValueKind == JsonValueKind.String ? "https://image.tmdb.org/t/p/original" + b.GetString() : null,
                mediaType = x.TryGetProperty("media_type", out var m) ? m.GetString() : "movie",
                rating = x.TryGetProperty("vote_average", out var v) ? v.GetDouble() : 0,
                originalLanguage = x.TryGetProperty("original_language", out var language) ? language.GetString() : null,
                year = ReadMediaYear(x)
            }).ToArray();
        return Results.Json(new { configured = true, results });
    }
    catch (HttpRequestException) { return Results.Problem("Impossible de joindre TMDB.", statusCode: 502); }
});
app.MapGet("/api/catalog/genre/{mediaType}/{genreId:int}", async (string mediaType, int genreId, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (mediaType is not ("movie" or "tv") || genreId <= 0) return Results.BadRequest();
    var token = app.Services.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { configured = false, results = Array.Empty<object>() });
    var route = $"discover/{mediaType}?with_genres={genreId}&sort_by=popularity.desc&include_adult=false&language=fr-FR&page=1";
    try
    {
        using var response = await clients.CreateClient("tmdb").GetAsync(route, ct);
        if (!response.IsSuccessStatusCode) return Results.Problem("TMDB n’a pas répondu correctement.", statusCode: (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var results = json.RootElement.GetProperty("results").EnumerateArray().Select(x => new
        {
            id = x.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            title = x.TryGetProperty("title", out var t) ? t.GetString() : x.TryGetProperty("name", out var n) ? n.GetString() : "Sans titre",
            overview = x.TryGetProperty("overview", out var o) ? o.GetString() : "",
            poster = x.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String ? "https://image.tmdb.org/t/p/w500" + p.GetString() : null,
            backdrop = x.TryGetProperty("backdrop_path", out var b) && b.ValueKind == JsonValueKind.String ? "https://image.tmdb.org/t/p/original" + b.GetString() : null,
            mediaType,
            rating = x.TryGetProperty("vote_average", out var v) ? v.GetDouble() : 0,
            popularity = x.TryGetProperty("popularity", out var popularity) ? popularity.GetDouble() : 0,
            originalLanguage = x.TryGetProperty("original_language", out var language) ? language.GetString() : null,
            year = ReadMediaYear(x)
        }).ToArray();
        return Results.Json(new { configured = true, results });
    }
    catch (HttpRequestException) { return Results.Problem("Impossible de joindre le catalogue TMDB.", statusCode: 502); }
});

app.MapGet("/api/catalog/{mediaType}/{id:int}", async (string mediaType, int id, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (mediaType is not ("movie" or "tv") || id <= 0) return Results.BadRequest();
    var token = app.Services.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { configured = false }, statusCode: 503);
    try
    {
        using var response = await clients.CreateClient("tmdb").GetAsync($"{mediaType}/{id}?language=fr-FR&append_to_response=videos,credits", ct);
        if (!response.IsSuccessStatusCode) return Results.Problem("TMDB n’a pas répondu correctement.", statusCode: (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var item = json.RootElement;
        string? Text(string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        int? Number(string property) => item.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
        var genres = item.TryGetProperty("genres", out var genreList) && genreList.ValueKind == JsonValueKind.Array
            ? genreList.EnumerateArray().Select(genre => genre.TryGetProperty("name", out var name) ? name.GetString() : null).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray()
            : Array.Empty<string?>();
        var runtime = Number("runtime");
        if (runtime is null && item.TryGetProperty("episode_run_time", out var runtimes) && runtimes.ValueKind == JsonValueKind.Array && runtimes.GetArrayLength() > 0 && runtimes[0].TryGetInt32(out var episodeRuntime))
            runtime = episodeRuntime;
        var title = Text("title") ?? Text("name") ?? "Sans titre";
        var poster = Text("poster_path");
        var backdrop = Text("backdrop_path");
        var credits = item.TryGetProperty("credits", out var creditData) ? creditData : default;
        var directors = credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array
            ? crew.EnumerateArray()
                .Where(person => person.TryGetProperty("job", out var job) && job.GetString() == "Director")
                .Select(person => person.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray()
            : [];
        var creators = item.TryGetProperty("created_by", out var creatorList) && creatorList.ValueKind == JsonValueKind.Array
            ? creatorList.EnumerateArray().Select(person => person.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray()
            : [];
        var cast = credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("cast", out var castList) && castList.ValueKind == JsonValueKind.Array
            ? castList.EnumerateArray().Select(person => person.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray()
            : [];
        var seasons = mediaType == "tv" && item.TryGetProperty("seasons", out var seasonList) && seasonList.ValueKind == JsonValueKind.Array
            ? seasonList.EnumerateArray().Select(season => new
            {
                seasonNumber = season.TryGetProperty("season_number", out var seasonNumber) ? seasonNumber.GetInt32() : 0,
                name = season.TryGetProperty("name", out var seasonName) ? seasonName.GetString() : "Saison",
                episodeCount = season.TryGetProperty("episode_count", out var count) ? count.GetInt32() : 0,
                poster = season.TryGetProperty("poster_path", out var seasonPoster) && seasonPoster.ValueKind == JsonValueKind.String ? "https://image.tmdb.org/t/p/w500" + seasonPoster.GetString() : null
            }).ToArray()
            : [];
        string? trailerUrl = null;
        if (item.TryGetProperty("videos", out var videos) && videos.TryGetProperty("results", out var videoList) && videoList.ValueKind == JsonValueKind.Array)
        {
            var trailer = videoList.EnumerateArray()
                .Where(video => video.TryGetProperty("site", out var site) && site.GetString() == "YouTube"
                    && video.TryGetProperty("type", out var videoType) && videoType.GetString() == "Trailer"
                    && video.TryGetProperty("key", out var key) && !string.IsNullOrWhiteSpace(key.GetString()))
                .OrderByDescending(video => video.TryGetProperty("iso_639_1", out var language) && language.GetString() == "fr")
                .ThenByDescending(video => video.TryGetProperty("official", out var official) && official.ValueKind == JsonValueKind.True)
                .FirstOrDefault();
            if (trailer.ValueKind == JsonValueKind.Object && trailer.TryGetProperty("key", out var trailerKey))
                trailerUrl = $"https://www.youtube-nocookie.com/embed/{Uri.EscapeDataString(trailerKey.GetString()!)}?autoplay=1&rel=0";
        }
        return Results.Json(new
        {
            id,
            title,
            originalTitle = Text(mediaType == "movie" ? "original_title" : "original_name"),
            overview = Text("overview"),
            poster = poster is null ? null : "https://image.tmdb.org/t/p/w500" + poster,
            backdrop = backdrop is null ? null : "https://image.tmdb.org/t/p/original" + backdrop,
            mediaType,
            rating = item.TryGetProperty("vote_average", out var rating) && rating.TryGetDouble(out var score) ? score : 0,
            originalLanguage = Text("original_language"),
            year = ReadMediaYear(item),
            genres,
            runtimeMinutes = runtime,
            seasonCount = Number("number_of_seasons"),
            episodeCount = Number("number_of_episodes"),
            seasons,
            trailerUrl,
            directors,
            creators,
            cast
        });
    }
    catch (HttpRequestException) { return Results.Problem("Impossible de joindre TMDB.", statusCode: 502); }
});

app.MapGet("/api/catalog/tv/{id:int}/season/{seasonNumber:int}", async (int id, int seasonNumber, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (id <= 0 || seasonNumber < 0) return Results.BadRequest();
    var token = app.Services.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { configured = false }, statusCode: 503);
    try
    {
        using var response = await clients.CreateClient("tmdb").GetAsync($"tv/{id}/season/{seasonNumber}?language=fr-FR", ct);
        if (!response.IsSuccessStatusCode) return Results.Problem("TMDB n’a pas répondu correctement.", statusCode: (int)response.StatusCode);
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
                still = episode.TryGetProperty("still_path", out var still) && still.ValueKind == JsonValueKind.String ? "https://image.tmdb.org/t/p/w500" + still.GetString() : null,
                airDate = episode.TryGetProperty("air_date", out var date) ? date.GetString() : null,
                runtimeMinutes = episode.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : (int?)null
            }).ToArray()
            : [];
        return Results.Json(new { seasonNumber, episodes });
    }
    catch (HttpRequestException) { return Results.Problem("Impossible de récupérer les épisodes sur TMDB.", statusCode: 502); }
});

app.MapGet("/api/torrent-catalogs", async (TorrentCatalogStore store, CancellationToken ct) =>
    Results.Json((await store.GetAll(ct)).Select(x => new { x.Id, x.Name, x.Url, x.RightsConfirmed, x.AddedAt })));
app.MapPost("/api/torrent-catalogs", async (TorrentCatalogInput input, TorrentCatalogStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100)
        return Results.BadRequest(new { error = "Donnez un nom de 1 à 100 caractères à la source." });
    if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        return Results.BadRequest(new { error = "L’adresse du catalogue doit être une URL HTTP ou HTTPS valide." });
    if (!input.RightsConfirmed)
        return Results.BadRequest(new { error = "Confirmez que ce catalogue ne référence que des médias dont vous pouvez autoriser le téléchargement." });
    if (string.IsNullOrWhiteSpace(input.ApiKey) || input.ApiKey.Length > 500)
        return Results.BadRequest(new { error = "Une clé API Torznab valide est requise." });
    var saved = await store.Add(new TorrentCatalogSource(Guid.NewGuid(), input.Name.Trim(), uri.ToString(), input.ApiKey, input.RightsConfirmed, DateTimeOffset.UtcNow), ct);
    return Results.Created($"/api/torrent-catalogs/{saved.Id}", new { saved.Id, saved.Name, saved.Url, saved.RightsConfirmed, saved.AddedAt });
});
app.MapDelete("/api/torrent-catalogs/{id:guid}", async (Guid id, TorrentCatalogStore store, CancellationToken ct) =>
    await store.Remove(id, ct) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/torrent-catalogs/search", async (string query, TorrentCatalogSearchService search, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(query) || query.Length > 200) return Results.BadRequest(new { error = "Saisissez un titre (200 caractères maximum)." });
    return Results.Json(await search.SearchAsync(query, ct));
});
app.MapGet("/api/catalog/indexer-popular", async (IndexerPopularityService popular, CancellationToken ct) =>
    Results.Json(await popular.GetSnapshotAsync(ct)));
app.MapGet("/api/catalog/local-status", async (MediaDownloadService downloads, MediaWatchlistStore watchlist, CancellationToken ct) =>
    Results.Json(new { downloads = downloads.GetMediaStatuses(), watchlist = await watchlist.GetAllAsync(ct) }));

app.MapGet("/api/downloads/{tmdbId:int}", async (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, MediaDownloadService downloads, CancellationToken ct) =>
{
    var status = await downloads.GetStatusAsync(tmdbId, mediaType ?? "movie", seasonNumber, episodeNumber, ct);
    return status is null ? Results.NotFound() : Results.Json(status);
});
app.MapGet("/api/downloads/{tmdbId:int}/subtitles/fr", async (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, MediaDownloadService downloads, FrenchSubtitleService subtitles, CancellationToken ct) =>
{
    var type = mediaType ?? "movie";
    var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
    if (status is null || string.Equals(status.OriginalLanguage, "fr", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    var videoPath = downloads.GetCachedVideoPath(tmdbId, type, seasonNumber, episodeNumber) ?? downloads.GetActiveVideoPath(tmdbId, type, seasonNumber, episodeNumber);
    if (videoPath is null) return Results.NotFound();
    var content = await subtitles.GetSubtitleAsync(videoPath, status.Title, ct);
    return content is null ? Results.NotFound() : Results.Bytes(content, "text/vtt; charset=utf-8");
});
app.MapGet("/api/downloads/{tmdbId:int}/stream", async (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, MediaDownloadService downloads, CancellationToken ct) =>
{
    var type = mediaType ?? "movie";
    var cachedPath = downloads.GetCachedVideoPath(tmdbId, type, seasonNumber, episodeNumber);
    if (cachedPath is not null) return Results.File(cachedPath, MediaDownloadService.ContentTypeFor(cachedPath), enableRangeProcessing: true);
    var videoPath = downloads.GetActiveVideoPath(tmdbId, type, seasonNumber, episodeNumber);
    if (videoPath is null) return Results.Conflict(new { error = "La vidéo sera lisible dès que les métadonnées du torrent seront disponibles." });
    try
    {
        var stream = await downloads.CreateVideoStreamAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
        return Results.Stream(stream, MediaDownloadService.ContentTypeFor(videoPath));
    }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/downloads", (MediaDownloadService downloads) => Results.Json(downloads.GetActiveDownloads()));
app.MapGet("/api/watch-progress", async (WatchProgressStore progress, CancellationToken ct) => Results.Json(await progress.GetAllAsync(ct)));
app.MapGet("/api/watchlist", async (MediaWatchlistStore store, CancellationToken ct) => Results.Json(await store.GetAllAsync(ct)));
app.MapPost("/api/watchlist", async (MediaWatchlistRequest input, MediaWatchlistStore store, MediaAvailabilityWorker worker, IHttpClientFactory httpClients, CancellationToken ct) =>
{
    if (input.TmdbId <= 0 || input.MediaType is not ("movie" or "tv") || string.IsNullOrWhiteSpace(input.Title)) return Results.BadRequest();
    if (input.MediaType == "tv" && (input.SeasonNumber is null || input.EpisodeNumber is null))
    {
        try
        {
            var client = httpClients.CreateClient("tmdb");
            if (input.SeasonNumber is null)
            {
                using var seriesResponse = await client.GetAsync($"tv/{input.TmdbId}?language=fr-FR", ct);
                if (!seriesResponse.IsSuccessStatusCode) return Results.StatusCode(502);
                using var seriesJson = JsonDocument.Parse(await seriesResponse.Content.ReadAsStreamAsync(ct));
                input = input with
                {
                    SeasonNumber = seriesJson.RootElement.GetProperty("seasons").EnumerateArray()
                        .Where(season => season.TryGetProperty("episode_count", out var count) && count.TryGetInt32(out var number) && number > 0)
                        .Select(season => season.GetProperty("season_number").GetInt32()).Where(number => number > 0).Order().Select(number => (int?)number).FirstOrDefault()
                };
            }
            if (input.SeasonNumber is null) return Results.BadRequest(new { error = "Aucune saison avec des épisodes n’est disponible sur TMDB." });
            using var seasonResponse = await client.GetAsync($"tv/{input.TmdbId}/season/{input.SeasonNumber}?language=fr-FR", ct);
            if (!seasonResponse.IsSuccessStatusCode) return Results.StatusCode(502);
            using var seasonJson = JsonDocument.Parse(await seasonResponse.Content.ReadAsStreamAsync(ct));
            var firstEpisode = seasonJson.RootElement.GetProperty("episodes").EnumerateArray()
                .Where(episode => episode.TryGetProperty("episode_number", out var number) && number.TryGetInt32(out var episodeNumber) && episodeNumber > 0)
                .OrderBy(episode => episode.GetProperty("episode_number").GetInt32()).FirstOrDefault();
            if (firstEpisode.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "Aucun épisode n’est disponible dans cette saison." });
            input = input with
            {
                EpisodeNumber = firstEpisode.GetProperty("episode_number").GetInt32(),
                EpisodeTitle = firstEpisode.TryGetProperty("name", out var episodeTitle) && episodeTitle.ValueKind == JsonValueKind.String ? episodeTitle.GetString() : input.EpisodeTitle,
                RuntimeMinutes = firstEpisode.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : input.RuntimeMinutes
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            app.Logger.LogWarning(ex, "Could not resolve the first episode for watchlist request {TmdbId}.", input.TmdbId);
            return Results.StatusCode(502);
        }
    }
    var entry = new MediaWatchlistEntry(MediaWatchlistEntry.CreateKey(input.TmdbId, input.MediaType, input.SeasonNumber, input.EpisodeNumber),
        input.TmdbId, input.MediaType, input.Title, input.OriginalLanguage ?? "", input.Year, input.SeasonNumber, input.EpisodeNumber,
        input.EpisodeTitle, input.RuntimeMinutes, DateTimeOffset.UtcNow);
    var saved = await store.AddAsync(entry, ct);
    await worker.CheckNowAsync(saved, ct);
    return Results.Ok(saved);
});
app.MapDelete("/api/watchlist/{key}", async (string key, MediaWatchlistStore store, CancellationToken ct) =>
    await store.RemoveAsync(key, ct) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/watchlist/next-episode", async (NextEpisodeRequest input, MediaWatchlistStore store, WatchProgressStore progress, MediaAvailabilityWorker worker, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (input.TmdbId <= 0 || input.SeasonNumber < 0 || input.EpisodeNumber <= 0) return Results.BadRequest();
    var client = clients.CreateClient("tmdb");
    try
    {
        using var seriesResponse = await client.GetAsync($"tv/{input.TmdbId}?language=fr-FR", ct);
        if (!seriesResponse.IsSuccessStatusCode) return Results.StatusCode(502);
        using var seriesJson = JsonDocument.Parse(await seriesResponse.Content.ReadAsStreamAsync(ct));
        var seasons = seriesJson.RootElement.GetProperty("seasons").EnumerateArray()
            .Where(season => season.GetProperty("episode_count").GetInt32() > 0)
            .Select(season => season.GetProperty("season_number").GetInt32())
            .OrderBy(number => number).ToArray();
        var watched = await progress.GetEpisodesAsync(input.TmdbId, ct);
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
                    input.TmdbId, "tv", input.Title, input.OriginalLanguage ?? "", input.Year, seasonNumber, episodeNumber,
                    title, runtime, DateTimeOffset.UtcNow);
                var saved = await store.AddAsync(entry, ct);
                await worker.CheckNowAsync(saved, ct);
                return Results.Ok(saved);
            }
        }
        return Results.NoContent();
    }
    catch (HttpRequestException) { return Results.StatusCode(502); }
});
app.MapDelete("/api/downloads/{tmdbId:int}", async (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, MediaDownloadService downloads, CancellationToken ct) =>
    await downloads.CancelAsync(tmdbId, mediaType ?? "movie", seasonNumber, episodeNumber, ct) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/downloads", async (MediaDownloadRequest input, TorrentCatalogStore catalogs, SelectedIndexerStore selected, MediaDownloadService downloads, CancellationToken ct) =>
{
    var hasRights = (await catalogs.GetAll(ct)).Any(source => source.RightsConfirmed && string.Equals(source.Name, input.Source, StringComparison.OrdinalIgnoreCase))
        || (await selected.GetAllAsync(ct)).Any(source => source.RightsConfirmed && string.Equals(source.Name, input.Source, StringComparison.OrdinalIgnoreCase));
    if (!hasRights) return Results.BadRequest(new { error = "La source n’est pas configurée avec une attestation de droits." });
    try { return Results.Accepted($"/api/downloads/{input.TmdbId}?mediaType={Uri.EscapeDataString(input.MediaType)}&seasonNumber={input.SeasonNumber}&episodeNumber={input.EpisodeNumber}", await downloads.StartAsync(input with { RightsConfirmed = true }, ct)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// La bibliothèque est un dossier local de médias que l’utilisateur est autorisé à lire.
app.MapGet("/api/library", (IConfiguration config, MediaDownloadService downloads) =>
{
    var root = config["Media:Root"] ?? Environment.GetEnvironmentVariable("MEDIA_ROOT");
    var allowed = new[] { ".mp4", ".mkv", ".webm", ".m4v" };
    var files = new List<LibraryItem>();
    if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        files.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => allowed.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => new LibraryItem(Path.GetFileNameWithoutExtension(f), Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, f))).TrimEnd('=').Replace('+', '-').Replace('/', '_'))));
    files.AddRange(downloads.GetCompletedMediaFiles().Select(file => new LibraryItem(
        string.Equals(file.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(file.FullPath) : file.Name,
        "cache." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(downloads.CacheRoot, file.FullPath))).TrimEnd('=').Replace('+', '-').Replace('/', '_'))));
    return Results.Json(new { items = files });
});
app.MapGet("/api/library/play/{path}", async (string path, IConfiguration config, MediaDownloadService downloads, CancellationToken ct) =>
{
    var fromCache = path.StartsWith("cache.", StringComparison.Ordinal);
    var root = fromCache ? downloads.CacheRoot : config["Media:Root"] ?? Environment.GetEnvironmentVariable("MEDIA_ROOT");
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Results.NotFound();
    try
    {
        var encoded = (fromCache ? path[6..] : path).Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        var relative = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var basePath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
        var contentType = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            _ => "video/mp4"
        };
        if (fromCache) await downloads.MarkViewedAsync(full, ct);
        return Results.File(full, contentType, enableRangeProcessing: true);
    }
    catch (FormatException) { return Results.BadRequest(); }
});
app.MapDelete("/api/library/{path}", async (string path, IConfiguration config, MediaDownloadService downloads, CancellationToken ct) =>
{
    var fromCache = path.StartsWith("cache.", StringComparison.Ordinal);
    var root = fromCache ? downloads.CacheRoot : config["Media:Root"] ?? Environment.GetEnvironmentVariable("MEDIA_ROOT");
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Results.NotFound();
    try
    {
        var encoded = (fromCache ? path[6..] : path).Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        var relative = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
        if (fromCache)
            return await downloads.DeleteCachedMediaFileAsync(full, ct) ? Results.NoContent() : Results.NotFound();
        var allowed = new[] { ".mp4", ".mkv", ".webm", ".m4v", ".avi" };
        if (!allowed.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
        File.Delete(full);
        return Results.NoContent();
    }
    catch (FormatException) { return Results.BadRequest(); }
    catch (IOException) { return Results.Problem("Le fichier n’a pas pu être supprimé.", statusCode: 500); }
});
app.MapGet("/api/library/properties/{path}", async (string path, IConfiguration config, MediaDownloadService downloads, MediaPropertiesService properties, CancellationToken ct) =>
{
    var fromCache = path.StartsWith("cache.", StringComparison.Ordinal);
    var root = fromCache ? downloads.CacheRoot : config["Media:Root"] ?? Environment.GetEnvironmentVariable("MEDIA_ROOT");
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Results.NotFound();
    try
    {
        var encoded = (fromCache ? path[6..] : path).Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        var relative = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var allowed = new[] { ".mp4", ".mkv", ".webm", ".m4v", ".avi" };
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
            || !allowed.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
        return Results.Json(await properties.ReadAsync(full, ct));
    }
    catch (FormatException) { return Results.BadRequest(); }
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

static int? ReadMediaYear(JsonElement media)
{
    var date = media.TryGetProperty("release_date", out var release) ? release.GetString()
        : media.TryGetProperty("first_air_date", out var firstAir) ? firstAir.GetString() : null;
    return date is { Length: >= 4 } && int.TryParse(date[..4], out var year) ? year : null;
}

record TorrentCatalogInput(string Name, string Url, string ApiKey, bool RightsConfirmed);
record TrackerInput(List<string> Urls);
record NextEpisodeRequest(int TmdbId, int SeasonNumber, int EpisodeNumber, string Title, string? OriginalLanguage, int? Year);
record ApiCredentialsInput(string? TmdbReadAccessToken, string? OpenSubtitlesApiKey);
public record TorrentCatalogSource(Guid Id, string Name, string Url, string ApiKey, bool RightsConfirmed, DateTimeOffset AddedAt);
record LibraryItem(string Name, string Path);

public sealed class TorrentCatalogStore(IWebHostEnvironment environment)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string file = Path.Combine(environment.ContentRootPath, "App_Data", "torrent-catalogs.json");

    public async Task<List<TorrentCatalogSource>> GetAll(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(file)) return [];
            await using var input = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<TorrentCatalogSource>>(input, cancellationToken: ct) ?? [];
        }
        catch (JsonException) { return []; }
        finally { gate.Release(); }
    }

    public async Task<TorrentCatalogSource> Add(TorrentCatalogSource source, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnlocked(ct);
            items.RemoveAll(x => string.Equals(x.Url, source.Url, StringComparison.OrdinalIgnoreCase));
            items.Add(source);
            await WriteUnlocked(items, ct);
            return source;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> Remove(Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnlocked(ct);
            var removed = items.RemoveAll(x => x.Id == id) > 0;
            if (removed) await WriteUnlocked(items, ct);
            return removed;
        }
        finally { gate.Release(); }
    }

    private async Task<List<TorrentCatalogSource>> ReadUnlocked(CancellationToken ct)
    {
        if (!File.Exists(file)) return [];
        try
        {
            await using var input = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<TorrentCatalogSource>>(input, cancellationToken: ct) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private async Task WriteUnlocked(List<TorrentCatalogSource> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await using var output = File.Create(file);
        await JsonSerializer.SerializeAsync(output, items, cancellationToken: ct);
    }
}

public partial class Program { }
