using System.Collections.Concurrent;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.Client;

namespace media_player.Services;

public sealed class MediaDownloadService(
    IWebHostEnvironment environment,
    IConfiguration configuration,
    BittorrentTrackerStore trackerStore,
    ILogger<MediaDownloadService> logger) : IHostedService, IAsyncDisposable
{
    private readonly string cacheRoot = ResolveCacheRoot(environment, configuration);
    private readonly string manifestPath = Path.Combine(ResolveCacheRoot(environment, configuration), "downloads.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim manifestGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TorrentManager> managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MediaDownloadRecord> records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task>> streamPrebuffers = new(StringComparer.OrdinalIgnoreCase);
    private ClientEngine? engine;
    private CancellationTokenSource? monitorCancellation;
    private Task? monitorTask;
    private int stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheRoot);
        await LoadManifestAsync(cancellationToken);
        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(cacheRoot, ".engine"),
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowPortForwarding = false
        }.ToSettings();
        engine = new ClientEngine(settings);

        foreach (var record in records.Values.Where(item => !item.Completed))
            await ResumeAsync(record, cancellationToken);

        monitorCancellation = new CancellationTokenSource();
        monitorTask = MonitorDownloadsAsync(monitorCancellation.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        if (monitorCancellation is not null)
        {
            await monitorCancellation.CancelAsync();
            if (monitorTask is not null)
            {
                try { await monitorTask.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { }
            }
        }
        if (engine is not null)
        {
            foreach (var manager in managers.Values)
                try { await manager.StopAsync(); }
                catch (Exception ex) { logger.LogDebug(ex, "Could not stop torrent manager cleanly."); }
            engine.Dispose();
            engine = null;
        }
        await SaveManifestAsync(CancellationToken.None);
    }

    public async Task<MediaDownloadStatus?> GetStatusAsync(int tmdbId, string mediaType, int? seasonNumber = null, int? episodeNumber = null, CancellationToken ct = default)
    {
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        if (!records.TryGetValue(key, out var record)) return null;
        if (record.Completed && GetCachedVideoPath(tmdbId, mediaType, seasonNumber, episodeNumber) is not null)
            return ToStatus(record, null, "Disponible");
        if (managers.TryGetValue(key, out var manager))
        {
            EnsurePlaybackPrebufferStarted(key, manager, seasonNumber, episodeNumber);
            var completed = manager.Progress >= 100;
            if (completed)
            {
                record = record with { Completed = true, CompletedAt = DateTimeOffset.UtcNow };
                records[key] = record;
                await SaveManifestAsync(ct);
            }
            return ToStatus(record, manager, completed ? "Terminé" : "Téléchargement en cours");
        }
        return null;
    }

    public async Task<MediaDownloadStatus> StartAsync(MediaDownloadRequest request, CancellationToken ct = default)
    {
        if (!request.RightsConfirmed) throw new InvalidOperationException("La source n’a pas confirmé les droits nécessaires.");
        if (request.TmdbId <= 0 || string.IsNullOrWhiteSpace(request.Title)) throw new InvalidOperationException("Le média TMDB est invalide.");
        if (!Uri.TryCreate(request.Link, UriKind.Absolute, out var uri)
            || (uri.Scheme != "magnet" && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Le lien BitTorrent sélectionné est invalide.");
        if (engine is null) throw new InvalidOperationException("Le moteur BitTorrent n’est pas prêt.");

        var key = Key(request.TmdbId, request.MediaType, request.SeasonNumber, request.EpisodeNumber);
        await gate.WaitAsync(ct);
        try
        {
            if (records.TryGetValue(key, out var existing) && existing.Completed
                && GetCachedVideoPath(request.TmdbId, request.MediaType, request.SeasonNumber, request.EpisodeNumber) is not null)
                return ToStatus(existing, null, "Disponible");
            if (managers.TryGetValue(key, out var active))
                return ToStatus(records[key], active, "Téléchargement en cours");

            await EnsureCapacityAsync(ParseBytes(request.Size), key, ct);
            var directory = Path.Combine(cacheRoot, key);
            Directory.CreateDirectory(directory);
            var (downloadLink, trackerCount) = await AddConfiguredTrackersAsync(request.Link, ct);
            TorrentManager manager;
            if (uri.Scheme == "magnet" && MagnetLink.TryParse(downloadLink, out var magnet))
                manager = await engine.AddStreamingAsync(magnet, directory);
            else
                manager = await engine.AddStreamingAsync(downloadLink, directory);

            var record = new MediaDownloadRecord(key, request.TmdbId, NormalizeMediaType(request.MediaType), request.Title,
                request.OriginalLanguage ?? "", request.Year, request.Source, request.ResultTitle, downloadLink,
                request.Size, directory, false, DateTimeOffset.UtcNow, null, null, trackerCount, request.SeasonNumber, request.EpisodeNumber,
                request.RuntimeMinutes);
            records[key] = record;
            managers[key] = manager;
            await manager.StartAsync();
            await SaveManifestAsync(ct);
            return ToStatus(record, manager, "Téléchargement en cours");
        }
        finally { gate.Release(); }
    }

    public async Task MarkViewedAsync(string absolutePath, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(absolutePath);
        var root = Path.GetFullPath(cacheRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var record in records.Values)
        {
            var directory = Path.GetFullPath(record.Directory) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTimeOffset.UtcNow;
                records[record.Key] = record with { LastViewedAt = now };
                await SaveManifestAsync(ct);
                return;
            }
        }
    }

    public string CacheRoot => cacheRoot;

    public async Task<bool> DeleteCachedMediaFileAsync(string absolutePath, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(absolutePath);
        await gate.WaitAsync(ct);
        try
        {
            var record = records.Values.FirstOrDefault(item => item.Completed
                && fullPath.StartsWith(Path.GetFullPath(item.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (record is null || !IsVideoFile(fullPath) || !File.Exists(fullPath)) return false;
            File.Delete(fullPath);
            if (!ContainsVideo(record.Directory))
            {
                records.TryRemove(record.Key, out _);
                managers.TryRemove(record.Key, out _);
                if (Directory.Exists(record.Directory)) Directory.Delete(record.Directory, recursive: true);
            }
            await SaveManifestAsync(ct);
            return true;
        }
        finally { gate.Release(); }
    }

    public IReadOnlyList<MediaDownloadStatus> GetActiveDownloads() => managers
        .Where(pair => records.TryGetValue(pair.Key, out var record) && !record.Completed && pair.Value.Progress < 100)
        .Select(pair => ToStatus(records[pair.Key], pair.Value, "Téléchargement en cours"))
        .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<bool> CancelAsync(int tmdbId, string mediaType, int? seasonNumber = null, int? episodeNumber = null, CancellationToken ct = default)
    {
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        await gate.WaitAsync(ct);
        try
        {
            if (!managers.TryGetValue(key, out var manager) || !records.TryGetValue(key, out var record)
                || record.Completed || manager.Progress >= 100) return false;
            await manager.StopAsync(TimeSpan.FromSeconds(5));
            if (engine is not null) await engine.RemoveAsync(manager, RemoveMode.CacheDataAndDownloadedData);
            managers.TryRemove(key, out _);
            records.TryRemove(key, out _);
            if (Directory.Exists(record.Directory)) Directory.Delete(record.Directory, recursive: true);
            await SaveManifestAsync(ct);
            return true;
        }
        finally { gate.Release(); }
    }

    public string? GetCachedVideoPath(int tmdbId, string mediaType, int? seasonNumber = null, int? episodeNumber = null)
    {
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        if (!records.TryGetValue(key, out var record) || !record.Completed || !Directory.Exists(record.Directory)) return null;
        return SelectVideoPath(Directory.EnumerateFiles(record.Directory, "*", SearchOption.AllDirectories), seasonNumber, episodeNumber);
    }

    public string? GetActiveVideoPath(int tmdbId, string mediaType, int? seasonNumber = null, int? episodeNumber = null)
    {
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        return managers.TryGetValue(key, out var manager) && manager.Torrent is not null
            ? SelectVideoPath(manager.Files.Select(file => file.FullPath), seasonNumber, episodeNumber)
            : null;
    }

    public async Task<Stream> CreateVideoStreamAsync(int tmdbId, string mediaType, int? seasonNumber, int? episodeNumber, CancellationToken ct)
    {
        var key = Key(tmdbId, mediaType, seasonNumber, episodeNumber);
        if (!managers.TryGetValue(key, out var manager) || manager is null || manager.Torrent is null)
            throw new InvalidOperationException("Les métadonnées du torrent ne sont pas encore disponibles.");
        var videoFile = SelectVideoFile(manager, seasonNumber, episodeNumber);
        if (videoFile is null) throw new InvalidOperationException("Le fichier de cet épisode n’est pas présent dans ce torrent.");
        var streamProvider = manager.StreamProvider ?? throw new InvalidOperationException("Le flux vidéo n’est pas prêt.");
        return await streamProvider.CreateStreamAsync(videoFile, ct);
    }

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        _ => "video/mp4"
    };

    public IReadOnlyList<CompletedMediaFile> GetCompletedMediaFiles() => records.Values
        .Where(item => item.Completed && Directory.Exists(item.Directory))
        .SelectMany(item => Directory.EnumerateFiles(item.Directory, "*", SearchOption.AllDirectories)
            .Where(file => new[] { ".mp4", ".mkv", ".webm", ".m4v", ".avi" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Select(file => new CompletedMediaFile(item.Title, file, item.MediaType)))
        .ToArray();

    private async Task ResumeAsync(MediaDownloadRecord record, CancellationToken ct)
    {
        if (record.Completed && ContainsVideo(record.Directory)) return;
        try
        {
            var (downloadLink, trackerCount) = await AddConfiguredTrackersAsync(record.Link, ct);
            if (!Uri.TryCreate(downloadLink, UriKind.Absolute, out var uri)) return;
            TorrentManager manager;
            if (uri.Scheme == "magnet" && MagnetLink.TryParse(downloadLink, out var magnet))
                manager = await engine!.AddStreamingAsync(magnet, record.Directory);
            else
                manager = await engine!.AddStreamingAsync(downloadLink, record.Directory);
            records[record.Key] = record with { Link = downloadLink, TrackerCount = trackerCount };
            managers[record.Key] = manager;
            await manager.StartAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "Could not resume download for {MediaKey}.", record.Key);
        }
    }

    private async Task<(string Link, int Count)> AddConfiguredTrackersAsync(string link, CancellationToken ct)
    {
        if (!link.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return (link, 0);
        var existing = link.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.StartsWith("tr=", StringComparison.OrdinalIgnoreCase))
            .Select(part => Uri.UnescapeDataString(part[3..]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = (await trackerStore.GetAllAsync(ct)).Where(existing.Add).ToArray();
        var enriched = link + string.Concat(additions.Select(tracker => "&tr=" + Uri.EscapeDataString(tracker)));
        return (enriched, existing.Count);
    }

    private async Task MonitorDownloadsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var changed = false;
                foreach (var (key, manager) in managers)
                {
                    if (!records.TryGetValue(key, out var record)) continue;
                    if (manager.Progress >= 100 && !record.Completed)
                    {
                        records[key] = record with { Completed = true, CompletedAt = DateTimeOffset.UtcNow };
                        changed = true;
                    }
                }
                if (changed) await SaveManifestAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task EnsureCapacityAsync(long incomingBytes, string keepKey, CancellationToken ct)
    {
        var maxBytes = configuration.GetValue<long?>("Media:CacheMaxBytes") ?? 500L * 1024 * 1024 * 1024;
        if (incomingBytes <= 0) return;
        if (incomingBytes > maxBytes) throw new InvalidOperationException("Ce média dépasse la capacité maximale configurée pour le cache.");
        var used = records.Values.Where(item => item.Completed && Directory.Exists(item.Directory)).Sum(item => DirectorySize(item.Directory));
        foreach (var victim in records.Values.Where(item => item.Key != keepKey && item.Completed && Directory.Exists(item.Directory))
                     .OrderBy(item => item.LastViewedAt.HasValue ? 0 : 1)
                     .ThenBy(item => item.LastViewedAt ?? item.AddedAt))
        {
            if (used + incomingBytes <= maxBytes) break;
            try
            {
                var bytes = DirectorySize(victim.Directory);
                Directory.Delete(victim.Directory, recursive: true);
                records.TryRemove(victim.Key, out _);
                managers.TryRemove(victim.Key, out _);
                used -= bytes;
                await SaveManifestAsync(ct);
            }
            catch (IOException ex) { logger.LogWarning(ex, "Unable to evict cached media {MediaKey}.", victim.Key); }
        }
        if (used + incomingBytes > maxBytes)
            throw new InvalidOperationException("Le cache est plein et aucun ancien média ne peut être supprimé pour libérer assez d’espace.");
    }

    private async Task LoadManifestAsync(CancellationToken ct)
    {
        if (!File.Exists(manifestPath)) return;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var items = await JsonSerializer.DeserializeAsync<List<MediaDownloadRecord>>(stream, cancellationToken: ct) ?? [];
            foreach (var item in items)
            {
                records[item.Key] = item;
            }
        }
        catch (JsonException ex) { logger.LogWarning(ex, "The local media cache index is invalid; starting with an empty index."); }
    }

    private async Task SaveManifestAsync(CancellationToken ct)
    {
        await manifestGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var temporary = manifestPath + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, records.Values.OrderBy(item => item.AddedAt).ToArray(), cancellationToken: ct);
            File.Move(temporary, manifestPath, overwrite: true);
        }
        finally { manifestGate.Release(); }
    }

    private MediaDownloadStatus ToStatus(MediaDownloadRecord record, TorrentManager? manager, string state)
    {
        var peers = manager?.Peers.Available ?? 0;
        if (manager is not null && manager.Progress < 100 && peers == 0) state = "En attente de pairs";
        var cachedPath = manager is null ? GetCachedVideoPath(record.TmdbId, record.MediaType, record.SeasonNumber, record.EpisodeNumber) : null;
        var file = manager is null ? null : SelectVideoFile(manager, record.SeasonNumber, record.EpisodeNumber);
        var fileLength = file?.Length ?? (cachedPath is not null ? new FileInfo(cachedPath).Length : 0);
        var bufferedBytes = file is not null && manager?.Torrent is not null
            ? GetContiguousBufferedBytes(file, manager.Torrent.PieceLength)
            : fileLength;
        var downloadRate = manager?.Monitor.DownloadRate ?? 0;
        var requiredBytes = GetRequiredPlaybackBufferBytes(record, fileLength, downloadRate);
        var prebufferReady = manager is null || record.Completed || IsPlaybackPrebufferReady(record.Key);
        var canStream = fileLength > 0 && prebufferReady && (manager is null || manager.Progress >= 100 || bufferedBytes >= requiredBytes);
        return new MediaDownloadStatus(record.TmdbId, record.MediaType, record.Title, record.OriginalLanguage, state, manager?.Progress ?? (record.Completed ? 100 : 0),
            downloadRate, record.Directory, record.Completed, $"{record.Source} · {record.ResultTitle}",
            peers, record.TrackerCount, engine?.Dht.State.ToString() ?? "indisponible",
            record.SeasonNumber, record.EpisodeNumber,
            bufferedBytes, requiredBytes, canStream);
    }

    private long GetRequiredPlaybackBufferBytes(MediaDownloadRecord record, long fileLength, long downloadRate)
    {
        if (fileLength <= 0 || downloadRate <= 0) return fileLength;

        // Estimate the media bitrate from its size and runtime. TV episodes without an
        // episode-specific runtime use a typical 45-minute duration; movies default to 2h.
        var runtimeMinutes = record.RuntimeMinutes.GetValueOrDefault(record.MediaType == "tv" ? 45 : 120);
        if (runtimeMinutes <= 0) runtimeMinutes = record.MediaType == "tv" ? 45 : 120;
        var mediaBytesPerSecond = fileLength / (double)(runtimeMinutes * 60L);
        var throughputRatio = downloadRate / mediaBytesPerSecond;

        // If the download cannot comfortably keep up, wait for completion. Faster links
        // need a shorter reserve; near-real-time links get a longer one.
        if (throughputRatio < 1.2) return fileLength;
        var reserveSeconds = throughputRatio >= 3 ? 10 : throughputRatio >= 2 ? 20 : throughputRatio >= 1.5 ? 45 : 90;
        var minimumBytes = configuration.GetValue<long?>("Media:MinimumPlaybackBufferBytes") ?? 4L * 1024 * 1024;
        return Math.Min(fileLength, Math.Max(minimumBytes, (long)Math.Ceiling(mediaBytesPerSecond * reserveSeconds)));
    }

    private void EnsurePlaybackPrebufferStarted(string key, TorrentManager manager, int? seasonNumber, int? episodeNumber)
    {
        if (manager.Torrent is null || manager.StreamProvider is null) return;
        var file = SelectVideoFile(manager, seasonNumber, episodeNumber);
        if (file is null) return;
        var lazy = streamPrebuffers.GetOrAdd(key, _ => new Lazy<Task>(() => PrebufferStreamAsync(key, manager, file)));
        _ = lazy.Value;
    }

    private async Task PrebufferStreamAsync(string key, TorrentManager manager, ITorrentManagerFile file)
    {
        try
        {
            await using var stream = await manager.StreamProvider!.CreateStreamAsync(file, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            logger.LogDebug(ex, "Initial playback buffering failed for {MediaKey}; it will be retried.", key);
            streamPrebuffers.TryRemove(key, out _);
        }
    }

    private bool IsPlaybackPrebufferReady(string key) => streamPrebuffers.TryGetValue(key, out var lazy)
        && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully;

    private static long GetContiguousBufferedBytes(ITorrentManagerFile file, int pieceLength)
    {
        var contiguousPieces = file.BitField.FirstFalse();
        var offsetInFirstPiece = file.OffsetInTorrent % Math.Max(1, pieceLength);
        return Math.Clamp((long)contiguousPieces * pieceLength - offsetInFirstPiece, 0, file.Length);
    }

    private static ITorrentManagerFile? SelectVideoFile(TorrentManager manager, int? seasonNumber, int? episodeNumber)
    {
        var path = SelectVideoPath(manager.Files.Select(file => file.FullPath), seasonNumber, episodeNumber);
        return path is null ? null : manager.Files.FirstOrDefault(file => string.Equals(file.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static string? SelectVideoPath(IEnumerable<string> paths, int? seasonNumber, int? episodeNumber)
    {
        var videos = paths.Where(IsVideoFile).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (seasonNumber is null || episodeNumber is null) return videos.FirstOrDefault();
        var pattern = $@"(?i)(?:\bS0*{seasonNumber}\s*E0*{episodeNumber}\b|\b{seasonNumber}\s*[xX]\s*0*{episodeNumber}\b)";
        return videos.FirstOrDefault(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), pattern));
    }

    private static bool ContainsVideo(string path) => Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any(IsVideoFile);
    private static bool IsVideoFile(string path) => new[] { ".mp4", ".mkv", ".webm", ".m4v", ".avi" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static long DirectorySize(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => { try { return new FileInfo(file).Length; } catch (IOException) { return 0L; } })
        : 0;
    private static long ParseBytes(string? value) => long.TryParse(value, out var bytes) ? Math.Max(0, bytes) : 0;
    private static string NormalizeMediaType(string? value) => string.Equals(value, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
    private static string Key(int tmdbId, string? mediaType, int? seasonNumber = null, int? episodeNumber = null) =>
        string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) && seasonNumber is >= 0 && episodeNumber is > 0
            ? $"tv-{tmdbId}-s{seasonNumber:00}e{episodeNumber:00}" : $"{NormalizeMediaType(mediaType)}-{tmdbId}";
    private static string ResolveCacheRoot(IWebHostEnvironment env, IConfiguration config)
    {
        var path = config["Media:CacheRoot"];
        return string.IsNullOrWhiteSpace(path) ? Path.Combine(env.ContentRootPath, "App_Data", "media-cache")
            : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        monitorCancellation?.Dispose();
        gate.Dispose();
        manifestGate.Dispose();
    }
}

public sealed record MediaDownloadRequest(int TmdbId, string Title, string MediaType, string? OriginalLanguage, int? Year,
    string Source, string ResultTitle, string Link, string? Size, bool RightsConfirmed, int? SeasonNumber = null, int? EpisodeNumber = null,
    int? RuntimeMinutes = null);
public sealed record MediaDownloadStatus(int TmdbId, string MediaType, string Title, string OriginalLanguage, string State, double Progress,
    long DownloadRate, string Directory, bool Completed, string SourceTitle, int AvailablePeers, int TrackerCount, string DhtState,
    int? SeasonNumber, int? EpisodeNumber, long BufferedBytes, long RequiredBufferBytes, bool CanStream);
public sealed record MediaDownloadRecord(string Key, int TmdbId, string MediaType, string Title, string OriginalLanguage,
    int? Year, string Source, string ResultTitle, string Link, string? Size, string Directory, bool Completed,
    DateTimeOffset AddedAt, DateTimeOffset? CompletedAt, DateTimeOffset? LastViewedAt, int TrackerCount = 0,
    int? SeasonNumber = null, int? EpisodeNumber = null, int? RuntimeMinutes = null);
public sealed record CompletedMediaFile(string Name, string FullPath, string MediaType);
