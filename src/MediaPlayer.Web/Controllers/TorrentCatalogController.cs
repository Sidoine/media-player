using media_player.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace media_player.Controllers;

public sealed record TorrentCatalogInput(string Name, string Url, string ApiKey, bool RightsConfirmed);

[ApiController]
[Route("api/torrent-catalogs")]
[Authorize(Policy = "AdminWhenMultiUserEnabled")]
public sealed class TorrentCatalogController(TorrentCatalogStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var sources = await store.GetAll(ct);
        return Ok(sources.Select(source => new { source.Id, source.Name, source.Url, source.RightsConfirmed, source.AddedAt }));
    }

    [HttpPost]
    public async Task<IActionResult> Add(TorrentCatalogInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Url) || string.IsNullOrWhiteSpace(input.ApiKey))
            return BadRequest(new { error = "Nom, URL et clé API sont obligatoires." });
        if (!input.RightsConfirmed) return BadRequest(new { error = "Confirmez que vous pouvez utiliser le média obtenu via cette source." });
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return BadRequest(new { error = "L’URL de la source doit être une adresse HTTP ou HTTPS valide." });
        var source = new TorrentCatalogSource(Guid.NewGuid(), input.Name.Trim(), uri.ToString(), input.ApiKey.Trim(), true, DateTimeOffset.UtcNow);
        var saved = await store.Add(source, ct);
        return Created($"/api/torrent-catalogs/{saved.Id}", new { saved.Id, saved.Name, saved.Url, saved.RightsConfirmed, saved.AddedAt });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct) => await store.Remove(id, ct) ? NoContent() : NotFound();
}
