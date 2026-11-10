using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace media_player.Services;

public sealed class IndexerDefinitionCacheService(
    HttpClient http,
    string cacheRoot,
    TimeSpan refreshInterval,
    ILogger<IndexerDefinitionCacheService> logger,
    string? bundledDefinitionsPath = null) : IHostedService
{
    private const string ArchiveUrl = "https://codeload.github.com/Prowlarr/Indexers/zip/refs/heads/master";
    private const string SourceUrl = "https://github.com/Prowlarr/Indexers/tree/master/definitions/v11";
    private readonly string definitionsPath = Path.Combine(cacheRoot, "v11");
    private readonly string bundledPath = bundledDefinitionsPath ?? Path.Combine(AppContext.BaseDirectory, "IndexerDefinitions");
    private readonly string manifestPath = Path.Combine(cacheRoot, "manifest.json");
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CancellationTokenSource? backgroundRefreshCancellation;
    private Task? backgroundRefreshTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.FetchedAtUtc.HasValue || DateTimeOffset.UtcNow - status.FetchedAtUtc.Value >= refreshInterval)
            await RefreshAsync(cancellationToken);

        backgroundRefreshCancellation = new CancellationTokenSource();
        backgroundRefreshTask = RefreshPeriodicallyAsync(backgroundRefreshCancellation.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (backgroundRefreshCancellation is null || backgroundRefreshTask is null) return;
        await backgroundRefreshCancellation.CancelAsync();
        try { await backgroundRefreshTask.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
        backgroundRefreshCancellation.Dispose();
    }

    private async Task RefreshPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(refreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task<IndexerDefinitionCacheStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(manifestPath))
            {
                await using var stream = File.OpenRead(manifestPath);
                var status = await JsonSerializer.DeserializeAsync<IndexerDefinitionCacheStatus>(stream, cancellationToken: cancellationToken);
                if (status is not null) return status with { Available = Directory.Exists(definitionsPath) };
            }
        }
        catch (JsonException) { }

        var count = Directory.Exists(definitionsPath)
            ? Directory.EnumerateFiles(definitionsPath, "*.yml", SearchOption.TopDirectoryOnly).Count()
            : 0;
        return new IndexerDefinitionCacheStatus(Directory.Exists(definitionsPath), count, null, null, SourceUrl, null);
    }

    public async Task<IndexerDefinitionCacheStatus> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(cacheRoot);
            var archive = await http.GetByteArrayAsync(ArchiveUrl, cancellationToken);
            var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var staging = Path.Combine(cacheRoot, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                using var memory = new MemoryStream(archive, writable: false);
                using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
                const string entryPrefix = "definitions/v11/";
                var entries = zip.Entries
                    .Where(entry => entry.FullName.Contains(entryPrefix, StringComparison.Ordinal)
                        && entry.FullName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        && !entry.FullName.EndsWith('/'))
                    .ToArray();
                if (entries.Length == 0) throw new InvalidDataException("The GitHub archive contains no v11 YAML definitions.");

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    await using var source = entry.Open();
                    await using var destination = File.Create(Path.Combine(staging, fileName));
                    await source.CopyToAsync(destination, cancellationToken);
                }

                var stagedCount = Directory.EnumerateFiles(staging, "*.yml").Count();
                if (stagedCount == 0) throw new InvalidDataException("No definition files were extracted.");

                var previous = Path.Combine(cacheRoot, $".previous-{Guid.NewGuid():N}");
                if (Directory.Exists(definitionsPath)) Directory.Move(definitionsPath, previous);
                try
                {
                    Directory.Move(staging, definitionsPath);
                    var status = new IndexerDefinitionCacheStatus(
                        true,
                        stagedCount,
                        DateTimeOffset.UtcNow,
                        digest,
                        SourceUrl,
                        "v11");
                    await using var manifest = File.Create(manifestPath);
                    await JsonSerializer.SerializeAsync(manifest, status, cancellationToken: cancellationToken);
                    if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
                    logger.LogInformation("Updated local indexer definition cache with {DefinitionCount} files from {SourceUrl}.", stagedCount, SourceUrl);
                    return status;
                }
                catch
                {
                    if (!Directory.Exists(definitionsPath) && Directory.Exists(previous)) Directory.Move(previous, definitionsPath);
                    throw;
                }
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException)
        {
            logger.LogWarning(ex, "Could not refresh indexer definitions from GitHub; retaining the existing local cache if present.");
            return await GetStatusAsync(cancellationToken);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task<IReadOnlyList<IndexerDefinitionSummary>> GetDefinitionsAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        var summaries = new List<IndexerDefinitionSummary>();
        foreach (var path in Directory.Exists(definitionsPath)
            ? Directory.EnumerateFiles(definitionsPath, "*.yml", SearchOption.TopDirectoryOnly)
            : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var reader = File.OpenText(path);
                var stream = new YamlStream();
                stream.Load(reader);
                if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root) continue;
                var id = Scalar(root, "id") ?? Path.GetFileNameWithoutExtension(path);
                var name = Scalar(root, "name") ?? id;
                var description = Scalar(root, "description");
                var language = Scalar(root, "language");
                var access = Scalar(root, "type");
                var homepage = FirstSequenceScalar(root, "links");
                summaries.Add(new IndexerDefinitionSummary(id, name, description, language, access, homepage));
            }
            catch (Exception ex) when (ex is IOException or YamlException or InvalidOperationException)
            {
                logger.LogDebug(ex, "Skipping invalid indexer definition {DefinitionFile}.", Path.GetFileName(path));
            }
        }

        IEnumerable<IndexerDefinitionSummary> result = summaries;
        if (!string.IsNullOrWhiteSpace(query))
        {
            result = result.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<IndexerDefinitionSummary>> GetBundledDefinitionsAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(bundledPath)) return [];
        var summaries = new List<IndexerDefinitionSummary>();
        foreach (var path in Directory.EnumerateFiles(bundledPath, "*.yml", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var reader = File.OpenText(path);
                var stream = new YamlStream();
                stream.Load(reader);
                if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root) continue;
                var id = Scalar(root, "id") ?? Path.GetFileNameWithoutExtension(path);
                var name = Scalar(root, "name") ?? id;
                summaries.Add(new IndexerDefinitionSummary(id, name, Scalar(root, "description"), Scalar(root, "language"),
                    Scalar(root, "type"), FirstSequenceScalar(root, "links")));
            }
            catch (Exception ex) when (ex is IOException or YamlException or InvalidOperationException)
            {
                logger.LogDebug(ex, "Skipping invalid bundled indexer definition {DefinitionFile}.", Path.GetFileName(path));
            }
        }

        IEnumerable<IndexerDefinitionSummary> result = summaries;
        if (!string.IsNullOrWhiteSpace(query))
            result = result.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string?> GetDefinitionYamlAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) return null;
        var path = Path.Combine(definitionsPath, $"{id}.yml");
        if (File.Exists(path)) return await File.ReadAllTextAsync(path, cancellationToken);
        path = Path.Combine(bundledPath, $"{id}.yml");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    private static string? Scalar(YamlMappingNode mapping, string key)
    {
        foreach (var item in mapping.Children)
            if (item.Key is YamlScalarNode name && name.Value == key && item.Value is YamlScalarNode value)
                return value.Value;
        return null;
    }

    private static string? FirstSequenceScalar(YamlMappingNode mapping, string key)
    {
        foreach (var item in mapping.Children)
            if (item.Key is YamlScalarNode name && name.Value == key && item.Value is YamlSequenceNode sequence)
                return sequence.Children.OfType<YamlScalarNode>().Select(node => node.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return null;
    }
}

public sealed record IndexerDefinitionCacheStatus(
    bool Available,
    int DefinitionCount,
    DateTimeOffset? FetchedAtUtc,
    string? ArchiveSha256,
    string SourceUrl,
    string? DefinitionVersion);

public sealed record IndexerDefinitionSummary(
    string Id,
    string Name,
    string? Description,
    string? Language,
    string? AccessType,
    string? Homepage);
