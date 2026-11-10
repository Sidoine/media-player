using System.Globalization;
using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using YamlDotNet.RepresentationModel;

namespace media_player.Services;

/// <summary>Executes the JSON and HTML response modes used by cached Cardigann definitions.</summary>
public sealed class CardigannSearchService(
    IHttpClientFactory clients,
    IndexerDefinitionCacheService definitions,
    ILogger<CardigannSearchService> logger)
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> UnavailableHosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan HostFailureCooldown = TimeSpan.FromMinutes(15);

    public async Task<CardigannSearchResponse> SearchAsync(IEnumerable<SelectedIndexer> sources, string query, CancellationToken ct)
    {
        var results = new List<CardigannSearchResult>();
        var errors = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                var yaml = await definitions.GetDefinitionYamlAsync(source.DefinitionId, ct);
                if (yaml is null) { errors.Add($"{source.Name}: définition absente du cache."); continue; }
                var parsed = Parse(yaml);
                foreach (var path in parsed.Paths)
                {
                    var renderedPath = Render(path.Path, query, parsed.Config, parsed.Homepage);
                    var pathSucceeded = false;
                    var httpFailure = false;
                    foreach (var homepage in parsed.Homepages)
                    {
                        if (!TryResolveUri(renderedPath, homepage, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                        if (UnavailableHosts.TryGetValue(uri.Host, out var failedAt))
                        {
                            if (DateTimeOffset.UtcNow - failedAt < HostFailureCooldown) { httpFailure = true; continue; }
                            UnavailableHosts.TryRemove(uri.Host, out _);
                        }
                        try
                        {
                            using var response = await clients.CreateClient("cardigann").GetAsync(uri, ct);
                            if (!response.IsSuccessStatusCode) { httpFailure = true; continue; }
                            var content = await response.Content.ReadAsStringAsync(ct);
                            if (string.Equals(path.ResponseType, "json", StringComparison.OrdinalIgnoreCase))
                                await ParseJsonResultsAsync(content, uri, parsed, source, results, ct);
                            else if (string.Equals(path.ResponseType, "html", StringComparison.OrdinalIgnoreCase))
                                await ParseHtmlResultsAsync(content, uri, query, parsed, source, results, ct);
                            else
                            {
                                errors.Add($"{source.Name}: type de réponse Cardigann non pris en charge (« {path.ResponseType} »).");
                                pathSucceeded = true;
                                break;
                            }
                            pathSucceeded = true;
                            break;
                        }
                        catch (HttpRequestException ex)
                        {
                            httpFailure = true;
                            UnavailableHosts[uri.Host] = DateTimeOffset.UtcNow;
                            logger.LogDebug(ex, "Cardigann host {Host} failed for {IndexerId}; trying the next configured domain.", uri.Host, source.DefinitionId);
                        }
                    }
                    if (!pathSucceeded)
                        errors.Add(httpFailure
                            ? $"{source.Name}: ses domaines sont injoignables depuis cette machine (DNS/TLS)."
                            : $"{source.Name}: la définition ne fournit pas d’URL de recherche valide.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or YamlDotNet.Core.YamlException
                or InvalidOperationException or UriFormatException or TaskCanceledException or InvalidDataException)
            {
                logger.LogWarning(ex, "Cardigann search failed for {IndexerId} ({IndexerName}).", source.DefinitionId, source.Name);
                errors.Add($"{source.Name}: recherche impossible (site indisponible ou format non pris en charge).");
            }
        }
        return new CardigannSearchResponse(results.DistinctBy(result => result.Link, StringComparer.OrdinalIgnoreCase).ToList(), errors);
    }

    private async Task ParseJsonResultsAsync(string content, Uri baseUri, ParsedDefinition parsed, SelectedIndexer source,
        List<CardigannSearchResult> results, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(content);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : document.RootElement.ValueKind == JsonValueKind.Object
                && (document.RootElement.TryGetProperty("results", out var items) || document.RootElement.TryGetProperty("hits", out items))
                && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray()
                : Enumerable.Empty<JsonElement>();
        foreach (var row in rows)
        {
            var fields = parsed.Fields.ToDictionary(field => field.Key,
                field => ReadJsonSelector(row, field.Value.Selector ?? field.Value.Text ?? ""), StringComparer.OrdinalIgnoreCase);
            var title = Value(fields, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var link = Value(fields, "download", "magnet");
            if (string.IsNullOrWhiteSpace(link) && !string.IsNullOrWhiteSpace(Value(fields, "infohash")))
                link = $"magnet:?xt=urn:btih:{Uri.EscapeDataString(Value(fields, "infohash")!)}&dn={Uri.EscapeDataString(title)}";
            if (string.IsNullOrWhiteSpace(link)) link = Value(fields, "details");
            if (string.IsNullOrWhiteSpace(link)) continue;
            if (Uri.TryCreate(link, UriKind.Relative, out var relative) && Uri.TryCreate(baseUri, relative, out var absolute)) link = absolute.ToString();
            link = await ResolveDownloadLinkAsync(link, baseUri, parsed, ct);
            if (string.IsNullOrWhiteSpace(link)) continue;
            results.Add(CreateResult(source, title, link, fields));
        }
    }

    private async Task ParseHtmlResultsAsync(string content, Uri baseUri, string query, ParsedDefinition parsed,
        SelectedIndexer source, List<CardigannSearchResult> results, CancellationToken ct)
    {
        var document = new HtmlParser().ParseDocument(content);
        var rowsSelector = Render(parsed.RowsSelector, query, parsed.Config, parsed.Homepage);
        foreach (var row in QuerySelectorAll(document, rowsSelector))
        {
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, field) in parsed.Fields)
            {
                if (!string.IsNullOrWhiteSpace(field.Selector))
                {
                    var selector = Render(field.Selector, query, parsed.Config, parsed.Homepage);
                    var value = QuerySelectorAll(row, selector).Select(element => ReadElement(element, field.Attribute)).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                    fields[name] = ApplyFilters(value, field.Filters);
                }
            }
            foreach (var (name, field) in parsed.Fields.Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Text)))
                fields[name] = ApplyFilters(RenderResultTemplate(field.Text!, fields), field.Filters);

            var title = Value(fields, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var link = Value(fields, "download", "magnet", "infohash", "details");
            if (string.IsNullOrWhiteSpace(link)) continue;
            if (Value(fields, "infohash") is { } hash && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                link = $"magnet:?xt=urn:btih:{Uri.EscapeDataString(hash)}&dn={Uri.EscapeDataString(title)}";
            if (Uri.TryCreate(link, UriKind.Relative, out var relative) && Uri.TryCreate(baseUri, relative, out var absolute)) link = absolute.ToString();
            link = await ResolveDownloadLinkAsync(link, baseUri, parsed, ct);
            if (string.IsNullOrWhiteSpace(link)) continue;
            results.Add(CreateResult(source, title, link, fields));
        }
    }

    private async Task<string?> ResolveDownloadLinkAsync(string link, Uri baseUri, ParsedDefinition parsed, CancellationToken ct)
    {
        if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            || link.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            || parsed.DownloadSelectors.Count == 0) return link;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var detailsUri) || detailsUri.Scheme is not ("http" or "https")) return link;
        using var response = await clients.CreateClient("cardigann").GetAsync(detailsUri, ct);
        if (!response.IsSuccessStatusCode) return null;
        var document = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(ct));
        foreach (var selector in parsed.DownloadSelectors)
        {
            var rendered = Render(selector.Selector, "", parsed.Config, parsed.Homepage);
            foreach (var element in QuerySelectorAll(document, rendered))
            {
                var value = ApplyFilters(ReadElement(element, selector.Attribute), selector.Filters);
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (Uri.TryCreate(value, UriKind.Relative, out var relative) && Uri.TryCreate(detailsUri, relative, out var absolute)) value = absolute.ToString();
                if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
                    || Uri.TryCreate(value, UriKind.Absolute, out var downloadUri) && downloadUri.Scheme is "http" or "https")
                    return value;
            }
        }
        return null;
    }

    private static ParsedDefinition Parse(string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        var root = stream.Documents[0].RootNode as YamlMappingNode ?? throw new InvalidDataException("Definition is not a mapping.");
        var links = Sequence(root, "links").Select(Scalar).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Get(root, "settings") is YamlSequenceNode settings)
            foreach (var setting in settings.Children.OfType<YamlMappingNode>())
            {
                var name = Scalar(Get(setting, "name"));
                var value = Scalar(Get(setting, "default"));
                if (value is not null && Get(setting, "options") is YamlMappingNode options && Scalar(Get(options, value)) is { } optionValue)
                    value = optionValue;
                if (!string.IsNullOrWhiteSpace(name) && value is not null) config[name] = value;
            }
        if (links.FirstOrDefault() is { } homepage) config["sitelink"] = homepage;
        var search = Get(root, "search") as YamlMappingNode ?? throw new InvalidDataException("Definition has no search block.");
        var paths = Sequence(search, "paths").OfType<YamlMappingNode>().Select(path =>
        {
            var template = Scalar(Get(path, "path")) ?? throw new InvalidDataException("Definition has a search path with no path value.");
            var responseType = Scalar(Get(Get(path, "response") as YamlMappingNode, "type")) ?? "html";
            return new SearchPath(template, responseType);
        }).ToList();
        if (paths.Count == 0) throw new InvalidDataException("Definition has no search paths.");

        var rowsSelector = Scalar(Get(Get(search, "rows") as YamlMappingNode, "selector")) ?? "";
        var fields = new Dictionary<string, ParsedField>(StringComparer.OrdinalIgnoreCase);
        if (Get(search, "fields") is YamlMappingNode fieldMap)
            foreach (var (key, value) in fieldMap.Children)
                if (key is YamlScalarNode fieldName && value is YamlMappingNode field)
                {
                    var selector = Scalar(Get(field, "selector"));
                    var attribute = Scalar(Get(field, "attribute"));
                    var text = Scalar(Get(field, "text"));
                    var filters = ParseFilters(Get(field, "filters"));
                    if (selector is not null || text is not null)
                        fields[fieldName.Value ?? ""] = new ParsedField(selector, attribute, text, filters);
                }
        var downloadSelectors = Sequence(Get(root, "download") as YamlMappingNode, "selectors")
            .OfType<YamlMappingNode>()
            .Select(node => new ParsedSelector(Scalar(Get(node, "selector")) ?? "", Scalar(Get(node, "attribute")), ParseFilters(Get(node, "filters"))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Selector)).ToList();
        return new ParsedDefinition(paths, links.FirstOrDefault() ?? "", links.Length == 0 ? [""] : links.ToList(), config, rowsSelector, fields, downloadSelectors);
    }

    private static List<ParsedFilter> ParseFilters(YamlNode? node) => SequenceNode(node).OfType<YamlMappingNode>().Select(filter =>
    {
        var name = Scalar(Get(filter, "name")) ?? "";
        var argsNode = Get(filter, "args");
        var args = argsNode is YamlSequenceNode sequence ? sequence.Children.Select(Scalar).Where(value => value is not null).Select(value => value!).ToArray()
            : argsNode is YamlScalarNode scalar && scalar.Value is { } value ? [value] : [];
        return new ParsedFilter(name, args);
    }).ToList();

    private static string Render(string template, string keywords, IReadOnlyDictionary<string, string> config, string homepage)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var updated = Regex.Replace(template,
                @"\{\{\s*if\s+(.+?)\s*\}\}(.*?)(?:\{\{\s*else\s*\}\}(.*?))?\{\{\s*end\s*\}\}",
                match => EvaluateCondition(match.Groups[1].Value, keywords, config)
                    ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : "", RegexOptions.Singleline);
            if (updated == template) break;
            template = updated;
        }
        template = Regex.Replace(template, @"\{\{\s*join\s+\.Categories\s+[^}]+\}\}", "200", RegexOptions.IgnoreCase);
        template = Regex.Replace(template, @"\{\{\s*\.Keywords\s*\}\}", Uri.EscapeDataString(keywords), RegexOptions.IgnoreCase);
        template = Regex.Replace(template, @"\{\{\s*\.Today\.Year\s*\}\}", DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture));
        template = Regex.Replace(template, @"\{\{\s*\.Today\.Month\s*\}\}", DateTime.UtcNow.Month.ToString(CultureInfo.InvariantCulture));
        template = Regex.Replace(template, @"\{\{\s*\.Config\.([A-Za-z0-9_]+)\s*\}\}", match => config.TryGetValue(match.Groups[1].Value, out var value) ? value : "");
        return template;
    }

    private static bool EvaluateCondition(string expression, string keywords, IReadOnlyDictionary<string, string> config)
    {
        expression = expression.Trim();
        if (expression.StartsWith("and ", StringComparison.Ordinal))
            return ParenthesizedArguments(expression[4..]).All(argument => EvaluateCondition(argument, keywords, config));
        if (expression.StartsWith("or ", StringComparison.Ordinal))
            return ParenthesizedArguments(expression[3..]).Any(argument => EvaluateCondition(argument, keywords, config));
        if (expression.StartsWith("not ", StringComparison.Ordinal)) return !EvaluateCondition(expression[4..], keywords, config);
        if (expression.StartsWith("eq ", StringComparison.Ordinal))
        {
            var parts = expression[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length >= 2 && string.Equals(ConditionValue(parts[0], keywords, config), ConditionValue(parts[1], keywords, config), StringComparison.OrdinalIgnoreCase);
        }
        return IsTruthy(ConditionValue(expression, keywords, config));
    }

    private static IEnumerable<string> ParenthesizedArguments(string expression) => Regex.Matches(expression, @"\(([^()]*)\)")
        .Select(match => match.Groups[1].Value.Trim());

    private static string ConditionValue(string value, string keywords, IReadOnlyDictionary<string, string> config)
    {
        value = value.Trim().Trim('(', ')');
        if (value.Equals(".Keywords", StringComparison.OrdinalIgnoreCase)) return keywords;
        if (value.Equals(".False", StringComparison.OrdinalIgnoreCase)) return "false";
        if (value.Equals(".True", StringComparison.OrdinalIgnoreCase)) return "true";
        var match = Regex.Match(value, @"^\.Config\.([A-Za-z0-9_]+)$", RegexOptions.IgnoreCase);
        return match.Success && config.TryGetValue(match.Groups[1].Value, out var result) ? result : value;
    }

    private static bool IsTruthy(string value) => !string.IsNullOrWhiteSpace(value)
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";

    private static string RenderResultTemplate(string template, IReadOnlyDictionary<string, string?> fields)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var updated = Regex.Replace(template,
                @"\{\{\s*if\s+\.Result\.([A-Za-z0-9_]+)\s*\}\}(.*?)(?:\{\{\s*else\s*\}\}(.*?))?\{\{\s*end\s*\}\}",
                match => !string.IsNullOrWhiteSpace(fields.GetValueOrDefault(match.Groups[1].Value))
                    ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (updated == template) break;
            template = updated;
        }
        template = Regex.Replace(template, @"\{\{\s*\.Result\.([A-Za-z0-9_]+)\s*\}\}",
            match => fields.GetValueOrDefault(match.Groups[1].Value) ?? "", RegexOptions.IgnoreCase);
        template = Regex.Replace(template, @"\{\{\s*or\s+\.Result\.([A-Za-z0-9_]+)\s+\.Result\.([A-Za-z0-9_]+)\s*\}\}",
            match => fields.GetValueOrDefault(match.Groups[1].Value) ?? fields.GetValueOrDefault(match.Groups[2].Value) ?? "", RegexOptions.IgnoreCase);
        return template.Trim();
    }

    private static IReadOnlyList<IElement> QuerySelectorAll(IParentNode parent, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return [];
        var contains = new List<(string Text, bool Negate)>();
        selector = Regex.Replace(selector, @":not\(\s*:contains\(([""']?)(.*?)\1\)\s*\)", match =>
        {
            contains.Add((match.Groups[2].Value, true));
            return "";
        }, RegexOptions.IgnoreCase);
        selector = Regex.Replace(selector, @":contains\(([""']?)(.*?)\1\)", match =>
        {
            contains.Add((match.Groups[2].Value, false));
            return "";
        }, RegexOptions.IgnoreCase);
        IElement[] selected;
        try { selected = parent.QuerySelectorAll(selector).ToArray(); }
        catch (Exception ex) { throw new InvalidDataException($"Cardigann HTML selector is invalid: {selector}", ex); }
        return selected.Where(element => contains.All(filter =>
        {
            var hasText = element.TextContent.Contains(filter.Text, StringComparison.OrdinalIgnoreCase);
            return filter.Negate ? !hasText : hasText;
        })).ToArray();
    }

    private static string? ReadElement(IElement element, string? attribute) => !string.IsNullOrWhiteSpace(attribute)
        ? element.GetAttribute(attribute)?.Trim()
        : WebUtility.HtmlDecode(element.TextContent).Trim();

    private static string? ApplyFilters(string? value, IEnumerable<ParsedFilter> filters)
    {
        if (value is null) return null;
        foreach (var filter in filters)
        {
            switch (filter.Name.ToLowerInvariant())
            {
                case "urldecode": value = WebUtility.UrlDecode(value); break;
                case "replace" when filter.Args.Length >= 2: value = value.Replace(filter.Args[0], filter.Args[1], StringComparison.Ordinal); break;
                case "re_replace" when filter.Args.Length >= 2:
                    try { value = Regex.Replace(value, filter.Args[0], filter.Args[1]); }
                    catch (ArgumentException) { }
                    break;
                case "split" when filter.Args.Length >= 2 && int.TryParse(filter.Args[1], out var index):
                    var parts = value.Split('/', StringSplitOptions.None);
                    value = index >= 0 && index < parts.Length ? parts[index] : null;
                    break;
                case "trim": value = value.Trim(); break;
            }
            if (value is null) break;
        }
        return value;
    }

    private static bool TryResolveUri(string path, string homepage, out Uri uri)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out uri!)) return true;
        if (Uri.TryCreate(homepage, UriKind.Absolute, out var home) && Uri.TryCreate(home, path, out uri!)) return true;
        uri = null!;
        return false;
    }

    private static CardigannSearchResult CreateResult(SelectedIndexer source, string title, string link, IReadOnlyDictionary<string, string?> fields) =>
        new(source.Name, title, link, Value(fields, "size"), Value(fields, "seeders"), Value(fields, "leechers"),
            Value(fields, "category"), Value(fields, "grabs"), source.RightsConfirmed);

    private static string? ReadJsonSelector(JsonElement row, string selector)
    {
        var path = selector.StartsWith("$", StringComparison.Ordinal) ? selector[1..] : selector;
        path = path.TrimStart('.');
        if (path.StartsWith("[0].", StringComparison.Ordinal)) path = path[4..];
        else if (path.StartsWith("[", StringComparison.Ordinal))
        {
            var end = path.IndexOf(']');
            if (end > 1 && int.TryParse(path[1..end], out var index) && row.ValueKind == JsonValueKind.Array && index < row.GetArrayLength())
                return ReadJsonSelector(row[index], path[(end + 1)..]);
        }
        var current = row;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(), JsonValueKind.Number => current.ToString(),
            JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null
        };
    }

    private static string? Value(IReadOnlyDictionary<string, string?> fields, params string[] names) =>
        names.Select(name => fields.GetValueOrDefault(name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static YamlNode? Get(YamlMappingNode? node, string key) => node?.Children.FirstOrDefault(item => item.Key is YamlScalarNode scalar && scalar.Value == key).Value;
    private static IEnumerable<YamlNode> Sequence(YamlNode? node, string key) => SequenceNode(Get(node as YamlMappingNode, key));
    private static IEnumerable<YamlNode> SequenceNode(YamlNode? node) => node is YamlSequenceNode sequence ? sequence.Children : [];
    private static string? Scalar(YamlNode? node) => node is YamlScalarNode scalar ? scalar.Value : null;

    private sealed record SearchPath(string Path, string ResponseType);
    private sealed record ParsedField(string? Selector, string? Attribute, string? Text, List<ParsedFilter> Filters);
    private sealed record ParsedSelector(string Selector, string? Attribute, List<ParsedFilter> Filters);
    private sealed record ParsedFilter(string Name, string[] Args);
    private sealed record ParsedDefinition(List<SearchPath> Paths, string Homepage, List<string> Homepages, Dictionary<string, string> Config,
        string RowsSelector, Dictionary<string, ParsedField> Fields, List<ParsedSelector> DownloadSelectors);
}

public sealed record CardigannSearchResult(string Source, string Title, string Link, string? Size, string? Seeders,
    string? Leechers, string? Category, string? Grabs, bool RightsConfirmed);
public sealed record CardigannSearchResponse(List<CardigannSearchResult> Results, List<string> Errors);
