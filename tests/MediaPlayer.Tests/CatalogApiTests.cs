using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
        using var client = factory.CreateClient();
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
        using var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<TmdbStatus>("/api/catalog");
        Assert.NotNull(response);
        Assert.False(response.Configured);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task RootRouteRendersBlazorCinemaInterface()
    {
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/");

        Assert.Contains("ÉCRAN", html);
        Assert.Contains("Tendances du moment", html);
        Assert.Contains("_framework/blazor.web.js", html);
    }

    [Fact]
    public async Task ConfigurationRouteRendersTorznabSettingsSeparately()
    {
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/configuration");

        Assert.Contains("Configuration", html);
        Assert.Contains("Sources Torznab", html);
        Assert.Contains("/configuration", html);
    }

    [Fact]
    public async Task LibraryRouteRendersAsItsOwnPage()
    {
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/bibliotheque");

        Assert.Contains("Ma bibliothèque", html);
        Assert.Contains("FICHIERS LOCAUX", html);
        Assert.Contains("/bibliotheque", html);
    }

    [Fact]
    public async Task DefinitionCacheStatusRouteIsAvailable()
    {
        using var client = factory.CreateClient();
        var status = await client.GetFromJsonAsync<DefinitionCacheStatus>("/api/indexer-definitions/status");

        Assert.NotNull(status);
        Assert.False(status.Available);
        Assert.Equal(0, status.DefinitionCount);
    }

    [Fact]
    public async Task DefinitionSourceRequiresRightsAndAnAvailableCachedDefinition()
    {
        using var client = factory.CreateClient();
        var unconfirmed = await client.PostAsJsonAsync("/api/selected-indexers", new { definitionId = "sample", rightsConfirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);

        var missing = await client.PostAsJsonAsync("/api/selected-indexers", new { definitionId = "sample", rightsConfirmed = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    public void Dispose()
    {
        factory.Dispose();
        if (Directory.Exists(contentRoot)) Directory.Delete(contentRoot, recursive: true);
    }

    private sealed class TestApplicationFactory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing").UseContentRoot(root)
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TMDB:ReadAccessToken"] = string.Empty,
                ["IndexerDefinitions:Enabled"] = "false"
            }));
    }

    private sealed record TmdbStatus(bool Configured, object[] Results);
    private sealed record DefinitionCacheStatus(bool Available, int DefinitionCount, DateTimeOffset? FetchedAtUtc, string? ArchiveSha256, string SourceUrl, string? DefinitionVersion);
}
