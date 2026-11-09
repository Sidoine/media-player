using System.Xml.Linq;

namespace media_player.Services;

public sealed class TorrentCatalogSearchService(
    TorrentCatalogStore catalogs,
    SelectedIndexerStore selectedIndexers,
    CardigannSearchService cardigann,
    IHttpClientFactory clients)
{
    public async Task<TorrentSearchResponse> SearchAsync(string query, CancellationToken ct)
    {
        var results = new List<TorrentSearchResult>();
        var errors = new List<string>();
        foreach (var source in await catalogs.GetAll(ct))
        {
            try
            {
                var endpoint = new UriBuilder(source.Url);
                var queryParts = System.Web.HttpUtility.ParseQueryString(endpoint.Query);
                queryParts["t"] = "search";
                queryParts["q"] = query;
                queryParts["apikey"] = source.ApiKey;
                endpoint.Query = queryParts.ToString();
                using var response = await clients.CreateClient().GetAsync(endpoint.Uri, ct);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var xml = XDocument.Load(stream);
                XNamespace torznab = "http://torznab.com/schemas/2015/feed";
                foreach (var item in xml.Descendants("item"))
                {
                    string? Attr(string name) => item.Elements(torznab + "attr").FirstOrDefault(a => (string?)a.Attribute("name") == name)?.Attribute("value")?.Value;
                    var title = (string?)item.Element("title") ?? "Sans titre";
                    var link = (string?)item.Element("link") ?? (string?)item.Element("enclosure")?.Attribute("url") ?? (string?)item.Element("guid");
                    if (!Uri.TryCreate(link, UriKind.Absolute, out var itemUri) || itemUri.Scheme is not ("magnet" or "http" or "https")) continue;
                    results.Add(new TorrentSearchResult(source.Name, title, link!, Attr("size"), Attr("seeders"), Attr("leechers"), Attr("category"), Attr("grabs"), source.RightsConfirmed));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or UriFormatException or TaskCanceledException)
            {
                errors.Add($"{source.Name}: réponse Torznab indisponible ou invalide.");
            }
        }

        var cardigannResponse = await cardigann.SearchAsync(await selectedIndexers.GetAllAsync(ct), query, ct);
        results.AddRange(cardigannResponse.Results.Select(result => new TorrentSearchResult(result.Source, result.Title, result.Link,
            result.Size, result.Seeders, result.Leechers, result.Category, result.Grabs, result.RightsConfirmed)));
        errors.AddRange(cardigannResponse.Errors);
        return new TorrentSearchResponse(results, errors);
    }
}

public sealed record TorrentSearchResult(string Source, string Title, string Link, string? Size, string? Seeders,
    string? Leechers, string? Category, string? Grabs, bool RightsConfirmed);
public sealed record TorrentSearchResponse(List<TorrentSearchResult> Results, List<string> Errors);
