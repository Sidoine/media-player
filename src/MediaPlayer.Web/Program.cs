using System.Net.Http.Headers;
using System.Text.Json;
using media_player.Components;
using media_player.Services;
using MediaPlayer.Localization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

if (args.Length == 2 && string.Equals(args[0], "--signal-dotnet-watch", StringComparison.Ordinal))
{
    await Task.Delay(TimeSpan.FromSeconds(2));
    var marker = $"internal static class ServerRestartMarker_{Guid.NewGuid():N} {{ }}{Environment.NewLine}";
    await File.WriteAllTextAsync(args[1], marker);
    return;
}

var builder = WebApplication.CreateBuilder(args);
// Keep Razor CSS isolation assets available when the app is launched from source in Production mode.
builder.WebHost.UseStaticWebAssets();
var playwrightMode = builder.Environment.IsEnvironment("Playwright") || builder.Configuration.GetValue("Playwright:Enabled", false);
builder.Services.AddSingleton<TorrentCatalogStore>();
builder.Services.AddSingleton<SelectedIndexerStore>();
builder.Services.AddSingleton<BittorrentTrackerStore>();
builder.Services.AddSingleton<MediaStorageSettingsStore>();
builder.Services.AddSingleton<MediaDownloadService>();
builder.Services.AddSingleton<CachedMediaAssociationStore>();
builder.Services.AddSingleton<WatchProgressStore>();
builder.Services.AddSingleton<TorrentCatalogSearchService>();
builder.Services.AddSingleton<MediaWatchlistStore>();
builder.Services.AddSingleton<MediaAvailabilityWorker>();
builder.Services.AddSingleton<IndexerPopularityService>();
builder.Services.AddSingleton<MediaPropertiesService>();
builder.Services.AddSingleton<ApiCredentialStore>();
builder.Services.AddSingleton<ExternalStreamingSettingsStore>();
builder.Services.AddSingleton<UserAccountStore>();
builder.Services.AddSingleton<CircuitIdentityBridge>();
var usersDatabase = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "users.db");
Directory.CreateDirectory(Path.GetDirectoryName(usersDatabase)!);
builder.Services.AddDbContextFactory<UserDbContext>(options => options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
{
    DataSource = usersDatabase,
    Pooling = false
}.ToString()));
builder.Services.AddSingleton<IAuthorizationHandler, AdminWhenMultiUserEnabledHandler>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "MediaPlayer.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(options => options.AddPolicy("AdminWhenMultiUserEnabled", policy => policy.AddRequirements(new AdminWhenMultiUserEnabledRequirement())));
builder.Services.AddControllers();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<TmdbImageCacheService>();
builder.Services.AddHttpClient("tmdb-images", client => client.BaseAddress = new Uri("https://image.tmdb.org/t/p/"));
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("Catalog", policy => policy.Expire(TimeSpan.FromHours(4))
        .VaryByValue(context => new KeyValuePair<string, string>("culture",
            context.Request.Cookies["MediaPlayerCulture"] ?? context.Request.Headers.AcceptLanguage.ToString()))
        .Tag("catalog"));
    options.AddPolicy("CatalogImages", policy => policy.Expire(TimeSpan.FromDays(1)));
});
builder.Services.AddSingleton<ServerRestartService>();
if (!playwrightMode && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaAvailabilityWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerPopularityService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaDownloadService>());
}
builder.Services.AddHttpClient("opensubtitles", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<SubtitleService>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddLocalization();
builder.Services.AddTransient<CircuitIdentityForwardingHandler>();
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<CircuitIdentityForwardingHandler>();
    handler.InnerHandler = new HttpClientHandler();
    var client = new HttpClient(handler) { BaseAddress = new Uri(sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().BaseUri) };
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(CultureInfo.CurrentUICulture.Name);
    return client;
});
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
await app.Services.GetRequiredService<UserAccountStore>().InitializeAsync();
await app.Services.GetRequiredService<WatchProgressStore>().InitializeAsync();
static string TmdbLanguage() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr-FR" : "en-US";
var supportedCultures = new[] { new CultureInfo("en"), new CultureInfo("fr") };
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture("en")
    .AddSupportedCultures(supportedCultures.Select(culture => culture.Name).ToArray())
    .AddSupportedUICultures(supportedCultures.Select(culture => culture.Name).ToArray());
localizationOptions.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider { CookieName = "MediaPlayerCulture" });
app.UseRequestLocalization(localizationOptions);
app.UseStaticFiles();
app.UseOutputCache();
app.UseAntiforgery();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        var bridge = context.RequestServices.GetRequiredService<CircuitIdentityBridge>();
        var principal = bridge.Validate(context.Request.Headers["X-MediaPlayer-Circuit"].FirstOrDefault());
        if (principal is not null) context.User = principal;
    }
    await next();
});
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isTorrentInitiation = HttpMethods.IsPost(context.Request.Method)
        && (path.Equals("/api/watchlist", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/watchlist/next-episode", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/downloads", StringComparison.OrdinalIgnoreCase));
    if (isTorrentInitiation && !context.RequestServices.GetRequiredService<MediaStorageSettingsStore>().Current.AllowAnonymousTorrentActions
        && (await context.RequestServices.GetRequiredService<UserAccountStore>().GetAllAsync()).Count > 0
        && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    var settingsApi = path.StartsWithSegments("/api/configuration") || path.StartsWithSegments("/api/bittorrent")
        || path.StartsWithSegments("/api/selected-indexers")
        || path.StartsWithSegments("/api/indexer-definitions") || path.StartsWithSegments("/api/server/restart")
        || path.Equals("/api/catalog/indexer-popular", StringComparison.OrdinalIgnoreCase)
        || (path.Equals("/api/torrent-catalogs/search", StringComparison.OrdinalIgnoreCase)
            && context.Request.Query.ContainsKey("sourceType"));
    if (settingsApi)
    {
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(context.User, "AdminWhenMultiUserEnabled");
        if (!result.Succeeded) { context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true ? 403 : 401; return; }
    }
    await next();
});
if (playwrightMode) app.UseMiddleware<PlaywrightApiMockMiddleware>();

app.MapControllers();

app.MapGet("/api/catalog", async (IHttpClientFactory clients, string? query, CancellationToken ct) =>
{
    var token = app.Services.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { configured = false, results = Array.Empty<object>() });
    var language = TmdbLanguage();
    var route = string.IsNullOrWhiteSpace(query) ? $"trending/all/week?language={language}" : $"search/multi?query={Uri.EscapeDataString(query)}&include_adult=false&language={language}";
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
                poster = x.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(p.GetString(), "w500") : null,
                backdrop = x.TryGetProperty("backdrop_path", out var b) && b.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(b.GetString(), "original") : null,
                mediaType = x.TryGetProperty("media_type", out var m) ? m.GetString() : "movie",
                rating = x.TryGetProperty("vote_average", out var v) ? v.GetDouble() : 0,
                originalLanguage = x.TryGetProperty("original_language", out var language) ? language.GetString() : null,
                year = ReadMediaYear(x)
            }).ToArray();
        return Results.Json(new { configured = true, results });
    }
    catch (HttpRequestException) { return Results.Problem("Impossible de joindre TMDB.", statusCode: 502); }
}).CacheOutput("Catalog");
app.MapGet("/api/catalog/genre/{mediaType}/{genreId:int}", async (string mediaType, int genreId, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (mediaType is not ("movie" or "tv") || genreId <= 0) return Results.BadRequest();
    var token = app.Services.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { configured = false, results = Array.Empty<object>() });
    var route = $"discover/{mediaType}?with_genres={genreId}&sort_by=popularity.desc&include_adult=false&language={TmdbLanguage()}&page=1";
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
            poster = x.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(p.GetString(), "w500") : null,
            backdrop = x.TryGetProperty("backdrop_path", out var b) && b.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(b.GetString(), "original") : null,
            mediaType,
            rating = x.TryGetProperty("vote_average", out var v) ? v.GetDouble() : 0,
            popularity = x.TryGetProperty("popularity", out var popularity) ? popularity.GetDouble() : 0,
            originalLanguage = x.TryGetProperty("original_language", out var language) ? language.GetString() : null,
            year = ReadMediaYear(x)
        }).ToArray();
        return Results.Json(new { configured = true, results });
    }
    catch (HttpRequestException) { return Results.Problem("Impossible de joindre le catalogue TMDB.", statusCode: 502); }
}).CacheOutput("Catalog");

app.MapGet("/api/catalog/{mediaType}/{id:int}", async (string mediaType, int id, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (mediaType is not ("movie" or "tv") || id <= 0) return Results.BadRequest();
    var token = app.Services.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { configured = false }, statusCode: 503);
    try
    {
        using var response = await clients.CreateClient("tmdb").GetAsync($"{mediaType}/{id}?language={TmdbLanguage()}&append_to_response=videos,credits", ct);
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
                poster = season.TryGetProperty("poster_path", out var seasonPoster) && seasonPoster.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(seasonPoster.GetString(), "w500") : null
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
            poster = TmdbImageCacheService.ToLocalUrl(poster, "w500"),
            backdrop = TmdbImageCacheService.ToLocalUrl(backdrop, "original"),
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
}).CacheOutput("Catalog");

app.MapGet("/api/catalog/tv/{id:int}/season/{seasonNumber:int}", async (int id, int seasonNumber, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (id <= 0 || seasonNumber < 0) return Results.BadRequest();
    var token = app.Services.GetRequiredService<ApiCredentialStore>().TmdbToken;
    if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { configured = false }, statusCode: 503);
    try
    {
        using var response = await clients.CreateClient("tmdb").GetAsync($"tv/{id}/season/{seasonNumber}?language={TmdbLanguage()}", ct);
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
                still = episode.TryGetProperty("still_path", out var still) && still.ValueKind == JsonValueKind.String ? TmdbImageCacheService.ToLocalUrl(still.GetString(), "w500") : null,
                airDate = episode.TryGetProperty("air_date", out var date) ? date.GetString() : null,
                runtimeMinutes = episode.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : (int?)null
            }).ToArray()
            : [];
        return Results.Json(new { seasonNumber, episodes });
    }
    catch (HttpRequestException) { return Results.Problem("Impossible de récupérer les épisodes sur TMDB.", statusCode: 502); }
}).CacheOutput("Catalog");

app.MapGet("/api/catalog/image/{size}/{fileName}", async (string size, string fileName, TmdbImageCacheService cache, CancellationToken ct) =>
{
    var image = await cache.GetCachedImageAsync(size, fileName, ct);
    return image is null ? Results.NotFound() : Results.File(image.Value.Path, image.Value.ContentType);
}).CacheOutput("CatalogImages");

app.MapGet("/api/torrent-catalogs/search", async (string query, string? sourceType, Guid? sourceId, TorrentCatalogSearchService search, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(query) || query.Length > 200) return Results.BadRequest(new { error = "Saisissez un titre (200 caractères maximum)." });
    if (sourceType is not null && (sourceType is not ("torznab" or "cardigann") || sourceId is null))
        return Results.BadRequest(new { error = "L’indexeur à tester est invalide." });
    return Results.Json(await search.SearchAsync(query, ct, sourceType, sourceId));
});
app.MapGet("/api/catalog/indexer-popular", async (IndexerPopularityService popular, CancellationToken ct) =>
{
    var snapshot = await popular.GetSnapshotAsync(ct);
    return Results.Json(snapshot with
    {
        Results = snapshot.Results.Select(item => item with
        {
            Poster = TmdbImageCacheService.ToLocalUrlFromRemote(item.Poster),
            Backdrop = TmdbImageCacheService.ToLocalUrlFromRemote(item.Backdrop)
        }).ToList()
    });
});
app.MapGet("/api/catalog/local-status", async (MediaDownloadService downloads, MediaWatchlistStore watchlist, CancellationToken ct) =>
    Results.Json(new { downloads = downloads.GetMediaStatuses(), watchlist = await watchlist.GetAllAsync(ct) }));

app.MapGet("/api/downloads/{tmdbId:int}/dht-diagnostic", (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, MediaDownloadService downloads) =>
{
    var diagnostic = downloads.GetDhtDiagnostic(tmdbId, mediaType ?? "movie", seasonNumber, episodeNumber);
    return diagnostic is null ? Results.NotFound() : Results.Json(diagnostic);
});
app.MapGet("/api/downloads/{tmdbId:int}", async (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, MediaDownloadService downloads, CancellationToken ct) =>
{
    var status = await downloads.GetStatusAsync(tmdbId, mediaType ?? "movie", seasonNumber, episodeNumber, ct);
    return status is null ? Results.NotFound() : Results.Json(status);
});
app.MapGet("/api/downloads/{tmdbId:int}/subtitles", async (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, MediaDownloadService downloads, SubtitleService subtitles, CancellationToken ct) =>
{
    var type = mediaType ?? "movie";
    var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
    if (status is null || string.Equals(status.OriginalLanguage, subtitles.ResolvedLanguage, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    var videoPath = downloads.GetCachedVideoPath(tmdbId, type, seasonNumber, episodeNumber) ?? downloads.GetActiveVideoPath(tmdbId, type, seasonNumber, episodeNumber);
    if (videoPath is null) return Results.NotFound();
    var content = await subtitles.GetSubtitleAsync(videoPath, status.Title, ct);
    return content is null ? Results.NotFound() : Results.Bytes(content, "text/vtt; charset=utf-8");
});
app.MapGet("/api/downloads/{tmdbId:int}/subtitles/options", async (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, string? language, MediaDownloadService downloads, SubtitleService subtitles, CancellationToken ct) =>
{
    var type = mediaType ?? "movie";
    var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
    if (status is null) return Results.NotFound();
    var videoPath = downloads.GetCachedVideoPath(tmdbId, type, seasonNumber, episodeNumber) ?? downloads.GetActiveVideoPath(tmdbId, type, seasonNumber, episodeNumber);
    if (videoPath is null) return Results.NotFound();
    var selectedLanguage = language is "en" or "fr" ? language : subtitles.ResolvedLanguage;
    var choices = await subtitles.GetChoicesAsync(videoPath, selectedLanguage, ct);
    return Results.Json(new { language = selectedLanguage, subtitlesConfigured = subtitles.IsConfigured, videoFileName = Path.GetFileName(videoPath), results = choices });
});
app.MapPost("/api/downloads/{tmdbId:int}/subtitles/select", async (int tmdbId, string? mediaType, int? seasonNumber, int? episodeNumber, SubtitleSelectionInput input, MediaDownloadService downloads, SubtitleService subtitles, CancellationToken ct) =>
{
    var type = mediaType ?? "movie";
    var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
    if (status is null) return Results.NotFound();
    var videoPath = downloads.GetCachedVideoPath(tmdbId, type, seasonNumber, episodeNumber) ?? downloads.GetActiveVideoPath(tmdbId, type, seasonNumber, episodeNumber);
    if (videoPath is null) return Results.NotFound();
    var language = string.IsNullOrWhiteSpace(input.Language) ? subtitles.ResolvedLanguage : input.Language;
    if (!MediaStorageSettingsStore.IsSupportedSubtitleLanguage(language)) return Results.BadRequest();
    var selected = await subtitles.SelectAsync(videoPath, input.Key, language, ct);
    return selected ? Results.Ok(new { key = input.Key }) : Results.NotFound();
});
app.MapGet("/api/downloads/{tmdbId:int}/subtitles/selected/{key}", async (int tmdbId, string key, string? mediaType, int? seasonNumber, int? episodeNumber, string? language, MediaDownloadService downloads, SubtitleService subtitles, CancellationToken ct) =>
{
    var type = mediaType ?? "movie";
    var status = await downloads.GetStatusAsync(tmdbId, type, seasonNumber, episodeNumber, ct);
    if (status is null) return Results.NotFound();
    var videoPath = downloads.GetCachedVideoPath(tmdbId, type, seasonNumber, episodeNumber) ?? downloads.GetActiveVideoPath(tmdbId, type, seasonNumber, episodeNumber);
    if (videoPath is null) return Results.NotFound();
    var selectedLanguage = string.IsNullOrWhiteSpace(language) ? subtitles.ResolvedLanguage : language;
    var content = await subtitles.ReadSelectedAsync(videoPath, key, selectedLanguage, ct);
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
app.MapGet("/api/watch-progress", async (HttpContext context, WatchProgressStore progress, MediaDownloadService downloads,
    CachedMediaAssociationStore associationStore, CancellationToken ct) =>
{
    var associations = await associationStore.GetLibraryAssociationsAsync(downloads.GetCompletedMediaFiles(), ct);
    var userName = context.User.Identity?.Name;
    await progress.ReconcileLibraryProgressAsync(associations, userName, ct);
    return Results.Json(await progress.GetAllAsync(userName, ct));
});
app.MapPost("/api/watch-progress", async (HttpContext context, PlaybackProgressRequest input, WatchProgressStore progress, CancellationToken ct) =>
{
    if (input.TmdbId <= 0 || input.MediaType is not ("movie" or "tv") || string.IsNullOrWhiteSpace(input.Title)) return Results.BadRequest();
    await progress.SaveAsync(input.TmdbId, input.MediaType, input.Title, input.CurrentTimeSeconds,
        input.DurationSeconds, input.PosterUrl, input.SeasonNumber, input.EpisodeNumber, ct, input.LibraryPath, context.User.Identity?.Name);
    return Results.NoContent();
});
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
app.MapPost("/api/watchlist/next-episode", async (HttpContext context, NextEpisodeRequest input, MediaWatchlistStore store, WatchProgressStore progress, MediaAvailabilityWorker worker, IHttpClientFactory clients, CancellationToken ct) =>
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
        var watched = await progress.GetEpisodesAsync(input.TmdbId, context.User.Identity?.Name, ct);
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
app.MapPost("/api/downloads", async (MediaDownloadRequest input, TorrentCatalogStore catalogs, SelectedIndexerStore selected, MediaDownloadService downloads, MediaWatchlistStore watchlist, ILogger<Program> logger, CancellationToken ct) =>
{
    var hasRights = (await catalogs.GetAll(ct)).Any(source => source.RightsConfirmed && string.Equals(source.Name, input.Source, StringComparison.OrdinalIgnoreCase))
        || (await selected.GetAllAsync(ct)).Any(source => source.RightsConfirmed && string.Equals(source.Name, input.Source, StringComparison.OrdinalIgnoreCase));
    if (!hasRights) return Results.BadRequest(new { error = "La source n’est pas configurée avec une attestation de droits." });
    try
    {
        var status = await downloads.StartAsync(input with { RightsConfirmed = true }, ct);
        var watchKey = MediaWatchlistEntry.CreateKey(input.TmdbId, input.MediaType, input.SeasonNumber, input.EpisodeNumber);
        try { await watchlist.RemoveAsync(watchKey, ct); }
        catch (IOException ex) { logger.LogWarning(ex, "Download started for {WatchKey}, but its watchlist entry could not be removed.", watchKey); }
        return Results.Accepted($"/api/downloads/{input.TmdbId}?mediaType={Uri.EscapeDataString(input.MediaType)}&seasonNumber={input.SeasonNumber}&episodeNumber={input.EpisodeNumber}", status);
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// La bibliothèque est un dossier local de médias que l’utilisateur est autorisé à lire.
app.MapGet("/api/library", async (MediaDownloadService downloads, CachedMediaAssociationStore associationStore, CancellationToken ct) =>
{
    var associations = await associationStore.GetLibraryAssociationsAsync(downloads.GetCompletedMediaFiles(), ct);
    var files = associations.Select(item => new LibraryItem(item.Name,
        "cache." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(item.Path)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        item.TmdbId, item.MediaType, item.SeasonNumber, item.EpisodeNumber)).ToArray();
    return Results.Json(new { items = files });
});
app.MapGet("/api/library/play/{path}", async (string path, IConfiguration config, MediaDownloadService downloads, CancellationToken ct) =>
{
    var fromCache = path.StartsWith("cache.", StringComparison.Ordinal);
    var root = fromCache ? downloads.CacheRoot : config["Media:Root"] ?? Environment.GetEnvironmentVariable("MEDIA_ROOT");
    try
    {
        if (!TryResolveLibraryPath(root, path, fromCache, out var full)) return Results.NotFound();
        if (fromCache ? !downloads.IsCompletedMediaFilePath(full) : !IsPathWithinRoot(full, root!)) return Results.NotFound();
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
    try
    {
        if (!TryResolveLibraryPath(root, path, fromCache, out var full)) return Results.NotFound();
        if (fromCache ? !downloads.IsCompletedMediaFilePath(full) : !IsPathWithinRoot(full, root!)) return Results.NotFound();
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
    try
    {
        if (!TryResolveLibraryPath(root, path, fromCache, out var full)) return Results.NotFound();
        var allowed = new[] { ".mp4", ".mkv", ".webm", ".m4v", ".avi" };
        if ((fromCache ? !downloads.IsCompletedMediaFilePath(full) : !IsPathWithinRoot(full, root!))
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

static bool TryResolveLibraryPath(string? root, string encodedPath, bool fromCache, out string fullPath)
{
    fullPath = "";
    if (!fromCache && (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))) return false;
    var token = fromCache ? encodedPath[6..] : encodedPath;
    token = token.Replace('-', '+').Replace('_', '/');
    token += new string('=', (4 - token.Length % 4) % 4);
    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
    if (Path.IsPathRooted(decoded))
    {
        if (!fromCache) return false;
        fullPath = Path.GetFullPath(decoded);
    }
    else
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        fullPath = Path.GetFullPath(Path.Combine(root, decoded));
    }
    return true;
}

static bool IsPathWithinRoot(string fullPath, string root)
{
    var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    return fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
}

record NextEpisodeRequest(int TmdbId, int SeasonNumber, int EpisodeNumber, string Title, string? OriginalLanguage, int? Year);
record SubtitleSelectionInput(string Key, string? Language = null);
public record TorrentCatalogSource(Guid Id, string Name, string Url, string ApiKey, bool RightsConfirmed, DateTimeOffset AddedAt);
record LibraryItem(string Name, string Path, int TmdbId, string MediaType, int? SeasonNumber, int? EpisodeNumber, string? PosterUrl = null);
record PlaybackProgressRequest(int TmdbId, string? MediaType, string Title, double CurrentTimeSeconds, double DurationSeconds,
    string? PosterUrl = null, int? SeasonNumber = null, int? EpisodeNumber = null, string? LibraryPath = null);

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
