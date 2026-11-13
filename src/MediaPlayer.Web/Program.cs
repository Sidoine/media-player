using System.Net.Http.Headers;
using System.Text.Json;
using media_player.Components;
using media_player.Services;
using MediaPlayer.Localization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
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
var dataProtectionKeys = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtection-Keys");
Directory.CreateDirectory(dataProtectionKeys);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeys));
var internalApiBaseAddress = builder.Configuration["MediaPlayer:InternalApiBaseAddress"];
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
    var navigation = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    var baseAddress = string.IsNullOrWhiteSpace(internalApiBaseAddress) ? navigation.BaseUri : internalApiBaseAddress;
    var client = new HttpClient(handler) { BaseAddress = new Uri(baseAddress, UriKind.Absolute) };
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
app.MapStaticAssets();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public record TorrentCatalogSource(Guid Id, string Name, string Url, string ApiKey, bool RightsConfirmed, DateTimeOffset AddedAt);

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
