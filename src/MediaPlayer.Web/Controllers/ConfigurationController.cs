using media_player.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace media_player.Controllers;

public sealed record TrackerInput(List<string> Urls);
public sealed record ApiCredentialsInput(string? TmdbReadAccessToken, string? OpenSubtitlesApiKey);
public sealed record MediaStorageSettingsInput(string CacheRoot, long CacheMaxBytes, double? ShareRatioLimit,
    int? PreferredResolution, bool? EnableUpnp = null, string? SubtitleLanguage = null, bool? AllowAnonymousTorrentActions = null);

[ApiController]
[Authorize(Policy = "AdminWhenMultiUserEnabled")]
public sealed class ConfigurationController(
    ApiCredentialStore credentials,
    MediaStorageSettingsStore settings,
    MediaDownloadService downloads,
    IWebHostEnvironment environment,
    IOutputCacheStore outputCache,
    ServerRestartService restart,
    BittorrentTrackerStore trackers,
    SelectedIndexerStore selectedIndexers,
    IndexerDefinitionCacheService definitions) : ControllerBase
{
    [HttpGet("/api/configuration/credentials")]
    public IActionResult GetCredentials() => Ok(credentials.GetStatus());

    [HttpPut("/api/configuration/credentials")]
    public async Task<IActionResult> SaveCredentials(ApiCredentialsInput input, CancellationToken ct)
    {
        await credentials.SaveLocalAsync(input.TmdbReadAccessToken, input.OpenSubtitlesApiKey, ct);
        await outputCache.EvictByTagAsync("catalog", ct);
        return Ok(credentials.GetStatus());
    }

    [HttpGet("/api/configuration/media")]
    public IActionResult GetMediaSettings() => Ok(MediaResponse(settings.Current));

    [HttpPut("/api/configuration/media")]
    public async Task<IActionResult> SaveMediaSettings(MediaStorageSettingsInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.CacheRoot)) return BadRequest(new { error = "Indiquez le dossier de stockage des médias." });
        if (input.CacheMaxBytes < 100L * 1024 * 1024 || input.CacheMaxBytes > 10L * 1024 * 1024 * 1024 * 1024)
            return BadRequest(new { error = "Le quota doit être compris entre 100 Mo et 10 To." });
        if (input.ShareRatioLimit is double ratio && (!double.IsFinite(ratio) || ratio < 0 || ratio > 1000))
            return BadRequest(new { error = "Le ratio doit être compris entre 0 et 1000, ou choisir le partage sans limite." });
        var resolution = input.PreferredResolution ?? settings.Current.PreferredResolution;
        if (!MediaStorageSettingsStore.IsSupportedResolution(resolution)) return BadRequest(new { error = "La résolution préférée doit être 720p, 1080p ou 2160p." });
        var language = input.SubtitleLanguage?.Trim().ToLowerInvariant() ?? settings.Current.SubtitleLanguage;
        if (!MediaStorageSettingsStore.IsSupportedSubtitleLanguage(language)) return BadRequest(new { error = "La langue des sous-titres doit être auto, en ou fr." });
        string cacheRoot;
        try
        {
            cacheRoot = Path.GetFullPath(Path.IsPathRooted(input.CacheRoot) ? input.CacheRoot.Trim() : Path.Combine(environment.ContentRootPath, input.CacheRoot.Trim()));
            Directory.CreateDirectory(cacheRoot);
            var probe = Path.Combine(cacheRoot, $".mediaplayer-write-test-{Guid.NewGuid():N}");
            await using (System.IO.File.Create(probe)) { }
            System.IO.File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return BadRequest(new { error = "Le dossier choisi ne peut pas être utilisé par le serveur." }); }

        var saved = await settings.SaveAsync(new MediaStorageSettings(cacheRoot, input.CacheMaxBytes, input.ShareRatioLimit, resolution,
            input.EnableUpnp ?? settings.Current.EnableUpnp, language,
            input.AllowAnonymousTorrentActions ?? settings.Current.AllowAnonymousTorrentActions), ct);
        await downloads.ApplySharingPolicyAsync(ct);
        return Ok(MediaResponse(saved));
    }

    [HttpPost("/api/server/restart")]
    public IActionResult RestartServer() { restart.ScheduleRestart(HttpContext); return Accepted(); }

    [HttpGet("/api/bittorrent/trackers")]
    public async Task<IActionResult> GetTrackers(CancellationToken ct) => Ok(new { urls = await trackers.GetAllAsync(ct) });

    [HttpPut("/api/bittorrent/trackers")]
    public async Task<IActionResult> SaveTrackers(TrackerInput input, CancellationToken ct)
    {
        if (input.Urls.Count > 50) return BadRequest(new { error = "Vous pouvez configurer jusqu’à 50 trackers." });
        var normalized = new List<string>();
        foreach (var value in input.Urls.Select(url => url.Trim()).Where(url => url.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("udp" or "http" or "https"))
                return BadRequest(new { error = $"Adresse de tracker invalide : {value}" });
            normalized.Add(uri.ToString());
        }
        await trackers.SaveAsync(normalized, ct);
        return Ok(new { urls = normalized });
    }

    [HttpGet("/api/selected-indexers")]
    public async Task<IActionResult> GetSelectedIndexers(CancellationToken ct) => Ok(await selectedIndexers.GetAllAsync(ct));

    [HttpPost("/api/selected-indexers")]
    public async Task<IActionResult> AddSelectedIndexer(SelectedIndexerInput input, CancellationToken ct)
    {
        if (!input.RightsConfirmed) return BadRequest(new { error = "Confirmez que vous pouvez utiliser le média obtenu via cette source." });
        var definition = (await definitions.GetDefinitionsAsync(cancellationToken: ct)).FirstOrDefault(x => string.Equals(x.Id, input.DefinitionId, StringComparison.OrdinalIgnoreCase));
        definition ??= (await definitions.GetBundledDefinitionsAsync(cancellationToken: ct)).FirstOrDefault(x => string.Equals(x.Id, input.DefinitionId, StringComparison.OrdinalIgnoreCase));
        if (definition is null) return NotFound(new { error = "Cette définition n’est pas disponible localement." });
        var source = await selectedIndexers.AddAsync(new SelectedIndexer(Guid.NewGuid(), definition.Id, definition.Name, true, DateTimeOffset.UtcNow), ct);
        return Created($"/api/selected-indexers/{source.Id}", source);
    }

    [HttpDelete("/api/selected-indexers/{id:guid}")]
    public async Task<IActionResult> RemoveSelectedIndexer(Guid id, CancellationToken ct) =>
        await selectedIndexers.RemoveAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("/api/indexer-definitions/status")]
    public Task<IndexerDefinitionCacheStatus> DefinitionStatus(CancellationToken ct) => definitions.GetStatusAsync(ct);
    [HttpPost("/api/indexer-definitions/refresh")]
    public Task<IndexerDefinitionCacheStatus> RefreshDefinitions(CancellationToken ct) => definitions.RefreshAsync(ct);
    [HttpGet("/api/indexer-definitions")]
    public Task<IReadOnlyList<IndexerDefinitionSummary>> GetDefinitions([FromQuery] string? query, CancellationToken ct) => definitions.GetDefinitionsAsync(query, ct);
    [HttpGet("/api/indexer-definitions/internal")]
    public Task<IReadOnlyList<IndexerDefinitionSummary>> GetBundledDefinitions([FromQuery] string? query, CancellationToken ct) => definitions.GetBundledDefinitionsAsync(query, ct);

    private object MediaResponse(MediaStorageSettings value) => new
    {
        cacheRoot = value.CacheRoot, activeCacheRoot = downloads.CacheRoot, cacheMaxBytes = value.CacheMaxBytes,
        usedBytes = downloads.GetStorageUsedBytes(), shareRatioLimit = value.ShareRatioLimit,
        preferredResolution = value.PreferredResolution, subtitleLanguage = value.SubtitleLanguage,
        allowAnonymousTorrentActions = value.AllowAnonymousTorrentActions,
        resolvedSubtitleLanguage = settings.ResolvedSubtitleLanguage, enableUpnp = value.EnableUpnp,
        activeEnableUpnp = downloads.EnableUpnp, portForwarding = downloads.GetPortForwardingDiagnostic(),
        requiresRestart = !string.Equals(Path.GetFullPath(value.CacheRoot), Path.GetFullPath(downloads.CacheRoot), StringComparison.OrdinalIgnoreCase)
            || value.EnableUpnp != downloads.EnableUpnp
    };
}
