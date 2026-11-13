using System.Text;
using media_player.Services;
using Microsoft.AspNetCore.Mvc;

namespace media_player.Controllers;

[ApiController]
[Route("api/library")]
public sealed class LibraryController(MediaDownloadService downloads, CachedMediaAssociationStore associationStore, IConfiguration configuration,
    MediaPropertiesService properties) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var associations = await associationStore.GetLibraryAssociationsAsync(downloads.GetCompletedMediaFiles(), ct);
        var files = associations.Select(item => new LibraryItem(item.Name,
            "cache." + Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Path)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            item.TmdbId, item.MediaType, item.SeasonNumber, item.EpisodeNumber)).ToArray();
        return Ok(new { items = files });
    }

    [HttpGet("play/{path}")]
    public async Task<IActionResult> Play(string path, CancellationToken ct)
    {
        var fromCache = path.StartsWith("cache.", StringComparison.Ordinal);
        var root = fromCache ? downloads.CacheRoot : configuration["Media:Root"];
        try
        {
            if (!LibraryPathResolver.TryResolve(root, path, fromCache, out var full)) return NotFound();
            if (fromCache ? !downloads.IsCompletedMediaFilePath(full) : !LibraryPathResolver.IsPathWithinRoot(full, root!)) return NotFound();
            var contentType = Path.GetExtension(full).ToLowerInvariant() switch { ".webm" => "video/webm", ".mkv" => "video/x-matroska", _ => "video/mp4" };
            if (fromCache) await downloads.MarkViewedAsync(full, ct);
            return PhysicalFile(full, contentType, enableRangeProcessing: true);
        }
        catch (FormatException) { return BadRequest(); }
    }

    [HttpDelete("{path}")]
    public async Task<IActionResult> Delete(string path, CancellationToken ct)
    {
        var fromCache = path.StartsWith("cache.", StringComparison.Ordinal);
        var root = fromCache ? downloads.CacheRoot : configuration["Media:Root"];
        try
        {
            if (!LibraryPathResolver.TryResolve(root, path, fromCache, out var full)) return NotFound();
            if (fromCache ? !downloads.IsCompletedMediaFilePath(full) : !LibraryPathResolver.IsPathWithinRoot(full, root!)) return NotFound();
            if (fromCache) return await downloads.DeleteCachedMediaFileAsync(full, ct) ? NoContent() : NotFound();
            var allowedExtensions = new[] { ".mp4", ".mkv", ".webm", ".m4v", ".avi" };
            if (!allowedExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) || !System.IO.File.Exists(full)) return NotFound();
            System.IO.File.Delete(full);
            return NoContent();
        }
        catch (FormatException) { return BadRequest(); }
        catch (IOException) { return Problem("Le fichier n’a pas pu être supprimé.", statusCode: 500); }
    }

    [HttpGet("properties/{path}")]
    public async Task<IActionResult> GetProperties(string path, CancellationToken ct)
    {
        var fromCache = path.StartsWith("cache.", StringComparison.Ordinal);
        var root = fromCache ? downloads.CacheRoot : configuration["Media:Root"];
        try
        {
            if (!LibraryPathResolver.TryResolve(root, path, fromCache, out var full)) return NotFound();
            var allowedExtensions = new[] { ".mp4", ".mkv", ".webm", ".m4v", ".avi" };
            if ((fromCache ? !downloads.IsCompletedMediaFilePath(full) : !LibraryPathResolver.IsPathWithinRoot(full, root!))
                || !allowedExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) || !System.IO.File.Exists(full)) return NotFound();
            return Ok(await properties.ReadAsync(full, ct));
        }
        catch (FormatException) { return BadRequest(); }
    }
}

internal static class LibraryPathResolver
{
    public static bool TryResolve(string? root, string encodedPath, bool fromCache, out string fullPath)
    {
        fullPath = "";
        if (!fromCache && (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))) return false;
        var token = fromCache ? encodedPath[6..] : encodedPath;
        token = token.Replace('-', '+').Replace('_', '/');
        token += new string('=', (4 - token.Length % 4) % 4);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
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

    public static bool IsPathWithinRoot(string fullPath, string root)
    {
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record LibraryItem(string Name, string Path, int TmdbId, string MediaType, int? SeasonNumber, int? EpisodeNumber, string? PosterUrl = null);
