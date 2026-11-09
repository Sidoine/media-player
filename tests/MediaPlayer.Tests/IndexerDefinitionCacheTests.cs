using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using media_player.Services;
using Xunit;

public sealed class IndexerDefinitionCacheTests
{
    [Fact]
    public async Task RefreshCachesOnlyTheActiveV11DefinitionFiles()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "media-player-cache-tests", Guid.NewGuid().ToString("N"));
        var archive = CreateArchive();
        using var client = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        var cache = new IndexerDefinitionCacheService(client, cacheRoot, TimeSpan.FromDays(7), NullLogger<IndexerDefinitionCacheService>.Instance);

        try
        {
            var status = await cache.RefreshAsync();

            Assert.True(status.Available);
            Assert.Equal(1, status.DefinitionCount);
            Assert.NotNull(status.ArchiveSha256);
            Assert.True(File.Exists(Path.Combine(cacheRoot, "v11", "sample.yml")));
            Assert.False(File.Exists(Path.Combine(cacheRoot, "v11", "legacy.yml")));
            var definitions = await cache.GetDefinitionsAsync();
            var definition = Assert.Single(definitions);
            Assert.Equal("sample", definition.Id);
            Assert.Equal("Sample Indexer", definition.Name);
        }
        finally
        {
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshFailureKeepsPreviouslyCachedDefinitions()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "media-player-cache-tests", Guid.NewGuid().ToString("N"));
        var archive = CreateArchive();
        var attempt = 0;
        using var client = new HttpClient(new StaticHandler(_ =>
        {
            attempt++;
            if (attempt == 1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
            throw new HttpRequestException("GitHub unavailable");
        }));
        var cache = new IndexerDefinitionCacheService(client, cacheRoot, TimeSpan.FromDays(7), NullLogger<IndexerDefinitionCacheService>.Instance);

        try
        {
            await cache.RefreshAsync();
            var status = await cache.RefreshAsync();

            Assert.True(status.Available);
            Assert.Equal(1, status.DefinitionCount);
            Assert.True(File.Exists(Path.Combine(cacheRoot, "v11", "sample.yml")));
        }
        finally
        {
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static byte[] CreateArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "Indexers-master/definitions/v11/sample.yml", "id: sample\nname: Sample Indexer\n");
            WriteEntry(archive, "Indexers-master/definitions/v10/legacy.yml", "id: legacy\nname: Legacy Indexer\n");
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
