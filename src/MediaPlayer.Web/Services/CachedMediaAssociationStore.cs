using System.Text.Json;
using System.Text.RegularExpressions;

namespace media_player.Services;

public sealed class CachedMediaAssociationStore(IWebHostEnvironment environment, ILogger<CachedMediaAssociationStore> logger)
{
    private readonly string filePath = Path.Combine(environment.ContentRootPath, "App_Data", "cached-media-associations.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, CachedMediaAssociation> associations = new(StringComparer.OrdinalIgnoreCase);
    private bool loaded;

    public async Task<IReadOnlyList<CachedMediaAssociation>> GetLibraryAssociationsAsync(
        IReadOnlyList<CompletedMediaFile> cachedFiles, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            var changed = false;
            var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var validFiles = cachedFiles.Where(IsTmdbMedia).ToArray();

            foreach (var group in validFiles.GroupBy(file => $"{Path.GetFullPath(file.Directory)}|{file.TmdbId}|{file.MediaType}", StringComparer.OrdinalIgnoreCase))
            {
                var files = group.ToArray();
                var existingGroup = associations.Values.Where(item =>
                    string.Equals(Path.GetDirectoryName(item.Path), Path.GetFullPath(files[0].Directory), StringComparison.OrdinalIgnoreCase)
                    && item.TmdbId == files[0].TmdbId
                    && string.Equals(item.MediaType, files[0].MediaType, StringComparison.OrdinalIgnoreCase)).ToArray();
                var unchanged = existingGroup.Length == files.Length && files.All(cachedFile =>
                {
                    var fullPath = Path.GetFullPath(cachedFile.FullPath);
                    var existing = existingGroup.FirstOrDefault(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase));
                    return existing is not null && existing.FileLength == new FileInfo(fullPath).Length
                        && existing.LastWriteUtc == File.GetLastWriteTimeUtc(fullPath);
                });
                if (unchanged)
                {
                    foreach (var cachedFile in files) currentPaths.Add(Path.GetFullPath(cachedFile.FullPath));
                    continue;
                }

                var primaryMoviePath = string.Equals(files[0].MediaType, "movie", StringComparison.OrdinalIgnoreCase)
                    ? files.Where(file => !IsSupplementaryVideo(file.FullPath))
                        .OrderByDescending(file => new FileInfo(file.FullPath).Length)
                        .Select(file => Path.GetFullPath(file.FullPath)).FirstOrDefault()
                    : null;

                foreach (var cachedFile in files)
                {
                    var fullPath = Path.GetFullPath(cachedFile.FullPath);
                    currentPaths.Add(fullPath);
                    var episode = ReadEpisodeNumbers(fullPath);
                    var season = cachedFile.SeasonNumber ?? episode.SeasonNumber;
                    var episodeNumber = cachedFile.EpisodeNumber ?? episode.EpisodeNumber;
                    var isSeries = string.Equals(cachedFile.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
                    var isSupplementary = IsSupplementaryVideo(fullPath);
                    var isLibraryItem = !isSupplementary && (isSeries
                        ? season is not null && episodeNumber is not null
                        : string.Equals(primaryMoviePath, fullPath, StringComparison.OrdinalIgnoreCase));
                    var length = new FileInfo(fullPath).Length;
                    var lastWrite = File.GetLastWriteTimeUtc(fullPath);
                    var displayName = isSeries ? Path.GetFileNameWithoutExtension(fullPath) : cachedFile.Name;

                    if (!associations.TryGetValue(fullPath, out var existing)
                        || existing.TmdbId != cachedFile.TmdbId
                        || !string.Equals(existing.MediaType, cachedFile.MediaType, StringComparison.OrdinalIgnoreCase)
                        || existing.SeasonNumber != season || existing.EpisodeNumber != episodeNumber
                        || existing.FileLength != length || existing.LastWriteUtc != lastWrite
                        || existing.IsLibraryItem != isLibraryItem || !string.Equals(existing.Name, displayName, StringComparison.Ordinal))
                    {
                        associations[fullPath] = new CachedMediaAssociation(fullPath, cachedFile.TmdbId, cachedFile.MediaType,
                            season, episodeNumber, displayName, isLibraryItem, length, lastWrite);
                        changed = true;
                    }
                }
            }

            foreach (var stalePath in associations.Keys.Where(path => !currentPaths.Contains(path)).ToArray())
            {
                associations.Remove(stalePath);
                changed = true;
            }

            if (changed) await SaveAsync(ct);
            return associations.Values.Where(item => item.IsLibraryItem)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (loaded) return;
        loaded = true;
        if (!File.Exists(filePath)) return;
        try
        {
            await using var input = File.OpenRead(filePath);
            var saved = await JsonSerializer.DeserializeAsync<List<CachedMediaAssociation>>(input, cancellationToken: ct) ?? [];
            associations = saved.Where(item => item.TmdbId > 0 && !string.IsNullOrWhiteSpace(item.Path))
                .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex) { logger.LogWarning(ex, "The cached media association file is invalid; rebuilding it from download metadata."); }
        catch (IOException ex) { logger.LogWarning(ex, "Could not read cached media associations; rebuilding them from download metadata."); }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryPath = filePath + ".tmp";
        await using (var output = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(output, associations.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken: ct);
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    private static bool IsTmdbMedia(CompletedMediaFile file) => file.TmdbId > 0 && (file.MediaType is "movie" or "tv");

    private static (int? SeasonNumber, int? EpisodeNumber) ReadEpisodeNumbers(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"S(?<season>\d{1,2})E(?<episode>\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["season"].Value, out var season) && int.TryParse(match.Groups["episode"].Value, out var episode)
            ? (season, episode)
            : (null, null);
    }

    private static bool IsSupplementaryVideo(string path) => Regex.IsMatch(Path.GetFileNameWithoutExtension(path),
        @"(^|[ ._\-\[\(])(bonus|extras?|featurettes?|behind[ ._-]*the[ ._-]*scenes|making[ ._-]*of|deleted[ ._-]*scenes?|interviews?|trailers?|samples?|bande[ ._-]*annonce|coulisses|sc[eè]nes[ ._-]*coup[eé]es)([ ._\-\]\)]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

public sealed record CachedMediaAssociation(string Path, int TmdbId, string MediaType, int? SeasonNumber, int? EpisodeNumber,
    string Name, bool IsLibraryItem, long FileLength, DateTime LastWriteUtc);
