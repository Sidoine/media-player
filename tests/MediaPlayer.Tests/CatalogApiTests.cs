using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class CatalogApiTests : IDisposable
{
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "media-player-tests", Guid.NewGuid().ToString("N"));
    private readonly TestApplicationFactory factory;

    public CatalogApiTests()
    {
        Directory.CreateDirectory(contentRoot);
        factory = new TestApplicationFactory(contentRoot);
    }

    [Fact]
    public async Task RightsAttestationIsRequiredAndApiKeyIsNotReturnedByList()
    {
        using var client = CreateClient();
        var rejected = await client.PostAsJsonAsync("/api/torrent-catalogs", new
        {
            name = "Licensed indexer", url = "https://indexer.example/api", apiKey = "private-test-key", rightsConfirmed = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var added = await client.PostAsJsonAsync("/api/torrent-catalogs", new
        {
            name = "Licensed indexer", url = "https://indexer.example/api", apiKey = "private-test-key", rightsConfirmed = true
        });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.DoesNotContain("private-test-key", await added.Content.ReadAsStringAsync());

        var list = await client.GetStringAsync("/api/torrent-catalogs");
        Assert.Contains("Licensed indexer", list);
        Assert.DoesNotContain("private-test-key", list);
    }

    [Fact]
    public async Task CatalogWithoutTmdbTokenReturnsUnconfiguredStatus()
    {
        using var client = CreateClient();
        var response = await client.GetFromJsonAsync<TmdbStatus>("/api/catalog");
        Assert.NotNull(response);
        Assert.False(response.Configured);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task RootRouteRendersBlazorCinemaInterface()
    {
        using var client = CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("BOBINE", html);
        Assert.Contains("_framework/blazor.web.js", html);
    }

    [Fact]
    public async Task ConfigurationRouteRendersTorznabSettingsSeparately()
    {
        using var client = CreateClient();
        var html = await client.GetStringAsync("/configuration");

        Assert.Contains("Configuration", html);
        Assert.Contains("Vérification des droits d’accès", System.Net.WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task LibraryRouteRendersAsItsOwnPage()
    {
        using var client = CreateClient();
        var html = await client.GetStringAsync("/bibliotheque");
        Assert.Contains("library-page", html);
    }

    [Fact]
    public async Task DefinitionCacheStatusRouteIsAvailable()
    {
        using var client = CreateClient();
        var status = await client.GetFromJsonAsync<DefinitionCacheStatus>("/api/indexer-definitions/status");

        Assert.NotNull(status);
        Assert.False(status.Available);
        Assert.Equal(0, status.DefinitionCount);
    }

    [Fact]
    public async Task DefinitionSourceRequiresRightsAndAnAvailableCachedDefinition()
    {
        using var client = CreateClient();
        var unconfirmed = await client.PostAsJsonAsync("/api/selected-indexers", new { definitionId = "sample", rightsConfirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);

        var missing = await client.PostAsJsonAsync("/api/selected-indexers", new { definitionId = "sample", rightsConfirmed = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task FirstAccountBecomesAdministratorAndProtectsSettingsApi()
    {
        using var client = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/configuration/credentials")).StatusCode);

        var created = await client.PostAsJsonAsync("/api/users", new { userName = "owner", password = "correct-horse-battery" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("Administrator", await created.Content.ReadAsStringAsync());
        var session = await client.GetFromJsonAsync<UserStatusResponse>("/api/users/status");
        Assert.NotNull(session);
        Assert.True(session.Authenticated);
        Assert.True(session.Administrator);
        Assert.Equal("owner", session.UserName);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/configuration/credentials")).StatusCode);
        var userListResponse = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, userListResponse.StatusCode);
        var userList = await userListResponse.Content.ReadAsStringAsync();
        Assert.Contains("owner", userList);
        Assert.DoesNotContain("PasswordHash", userList);

        using var anonymous = CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/configuration/credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/torrent-catalogs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/users", new { userName = "intruder", password = "correct-horse-battery" })).StatusCode);
    }

    [Fact]
    public async Task StreamingSettingsUseCommonCountryAndRequireAdministratorToChange()
    {
        using var client = CreateClient();
        var defaults = await client.GetFromJsonAsync<StreamingSettingsResponse>("/api/streaming/settings");
        Assert.NotNull(defaults);
        Assert.Equal("FR", defaults.CountryCode);
        Assert.True(defaults.NetflixEnabled);
        Assert.True(defaults.PrimeVideoEnabled);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/users", new { userName = "owner", password = "correct-horse-battery" })).StatusCode);
        using var anonymous = CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/streaming/settings", new { countryCode = "BE", netflixEnabled = false, primeVideoEnabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/streaming/settings", new { countryCode = "BE", netflixEnabled = false, primeVideoEnabled = true })).StatusCode);
        var updated = await client.GetFromJsonAsync<StreamingSettingsResponse>("/api/streaming/settings");
        Assert.NotNull(updated);
        Assert.Equal("BE", updated.CountryCode);
        Assert.False(updated.NetflixEnabled);
        Assert.True(updated.PrimeVideoEnabled);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/streaming/settings", new { countryCode = "ZZ", netflixEnabled = true, primeVideoEnabled = true })).StatusCode);
    }

    [Fact]
    public async Task AnonymousTorrentActionsAreEnabledByDefaultAndCanBeDisabledByAdministrator()
    {
        using var anonymousBeforeAccounts = CreateClient();
        using (var response = await anonymousBeforeAccounts.GetAsync("/api/configuration/media"))
        {
            response.EnsureSuccessStatusCode();
            using var settings = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(settings.RootElement.GetProperty("allowAnonymousTorrentActions").GetBoolean());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymousBeforeAccounts.PostAsJsonAsync("/api/watchlist", new { })).StatusCode);

        using var admin = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/users", new { userName = "owner", password = "correct-horse-battery" })).StatusCode);
        var settingsUpdate = await admin.PutAsJsonAsync("/api/configuration/media", new
        {
            cacheRoot = Path.Combine(contentRoot, "media-cache"), cacheMaxBytes = 500L * 1024 * 1024,
            shareRatioLimit = 1.0, preferredResolution = 1080, enableUpnp = false,
            subtitleLanguage = "auto", allowAnonymousTorrentActions = false
        });
        Assert.Equal(HttpStatusCode.OK, settingsUpdate.StatusCode);

        using var anonymous = CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/watchlist", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/watchlist/next-episode", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/downloads", new { })).StatusCode);
    }

    [Fact]
    public async Task NonAdministratorCannotCreateAdditionalAccounts()
    {
        using var owner = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/users", new { userName = "owner", password = "correct-horse-battery" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/users", new { userName = "member", password = "correct-horse-battery" })).StatusCode);
        using var other = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/users/login", new { userName = "member", password = "correct-horse-battery" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/users", new { userName = "intruder", password = "correct-horse-battery" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/users", new { userName = "member", password = "correct-horse-battery" })).StatusCode);
    }

    [Fact]
    public async Task WatchProgressIsStoredSeparatelyForAnonymousAndSignedInUsers()
    {
        using var anonymous = CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/watch-progress", new
        {
            tmdbId = 42, mediaType = "movie", title = "Shared title", currentTimeSeconds = 10, durationSeconds = 100
        })).StatusCode);

        using var owner = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/users", new { userName = "owner", password = "correct-horse-battery" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/users", new { userName = "member", password = "correct-horse-battery" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/api/watch-progress", new
        {
            tmdbId = 42, mediaType = "movie", title = "Personal title", currentTimeSeconds = 20, durationSeconds = 100
        })).StatusCode);
        var ownerProgress = await owner.GetFromJsonAsync<WatchProgressEntry[]>("/api/watch-progress");
        var anonymousProgress = await anonymous.GetFromJsonAsync<WatchProgressEntry[]>("/api/watch-progress");
        Assert.NotNull(ownerProgress);
        Assert.Single(ownerProgress);
        Assert.Equal("Personal title", ownerProgress[0].Title);
        Assert.NotNull(anonymousProgress);
        Assert.Single(anonymousProgress);
        Assert.Equal("Shared title", anonymousProgress[0].Title);

        using var member = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync("/api/users/login", new { userName = "member", password = "correct-horse-battery" })).StatusCode);
        var signedInProgress = await member.GetFromJsonAsync<WatchProgressEntry[]>("/api/watch-progress");
        Assert.NotNull(signedInProgress);
        Assert.Single(signedInProgress);
        Assert.Equal("Shared title", signedInProgress[0].Title);
    }

    [Fact]
    public async Task LegacyWatchProgressJsonIsImportedIntoTheDatabase()
    {
        Directory.CreateDirectory(Path.Combine(contentRoot, "App_Data"));
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "App_Data", "watch-progress.json"), """
            [{"TmdbId":73,"MediaType":"movie","Title":"Legacy title","CurrentTimeSeconds":45,"DurationSeconds":120,"ProgressPercent":37.5,"UpdatedAt":"2026-11-13T12:00:00+00:00","IsCompleted":false}]
            """);
        using var client = CreateClient();
        var entries = await client.GetFromJsonAsync<WatchProgressEntry[]>("/api/watch-progress");
        Assert.NotNull(entries);
        Assert.Single(entries);
        Assert.Equal("Legacy title", entries[0].Title);
        Assert.Equal(45, entries[0].CurrentTimeSeconds);
    }

    public void Dispose()
    {
        factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(contentRoot)) Directory.Delete(contentRoot, recursive: true);
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr");
        return client;
    }

    private sealed class TestApplicationFactory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing").UseContentRoot(root)
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TMDB:ReadAccessToken"] = string.Empty,
                ["IndexerDefinitions:Enabled"] = "false"
            }))
                .ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }
    }

    private sealed record TmdbStatus(bool Configured, object[] Results);
    private sealed record UserStatusResponse(bool Enabled, bool Authenticated, bool Administrator, string? UserName);
    private sealed record StreamingSettingsResponse(string CountryCode, bool NetflixEnabled, bool PrimeVideoEnabled);
    private sealed record WatchProgressEntry(int TmdbId, string MediaType, string Title, double CurrentTimeSeconds,
        double DurationSeconds, double ProgressPercent, DateTimeOffset UpdatedAt, string? PosterUrl = null,
        int? SeasonNumber = null, int? EpisodeNumber = null, bool IsCompleted = false, string? LibraryPath = null);
    private sealed record DefinitionCacheStatus(bool Available, int DefinitionCount, DateTimeOffset? FetchedAtUtc, string? ArchiveSha256, string SourceUrl, string? DefinitionVersion);
}
