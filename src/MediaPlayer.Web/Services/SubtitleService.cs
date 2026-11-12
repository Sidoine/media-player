using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace media_player.Services;

public sealed class SubtitleService(IHttpClientFactory clients, ApiCredentialStore credentials, MediaStorageSettingsStore settings, ILogger<SubtitleService> logger)
{
    private const string BaseAddress = "https://api.opensubtitles.com/api/v1";
    public string ResolvedLanguage => settings.ResolvedSubtitleLanguage;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(credentials.OpenSubtitlesKey);

    public async Task<byte[]?> GetSubtitleAsync(string videoPath, string title, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(videoPath)) return null;
        var language = settings.ResolvedSubtitleLanguage;
        var languageMarker = SubtitleMarker(language);

        var existing = EnumerateLocalSubtitles(directory)
            .Where(path => IsSubtitle(path) && languageMarker.IsMatch(Path.GetFileNameWithoutExtension(path)))
            .OrderByDescending(path => NameSimilarity(Path.GetFileNameWithoutExtension(videoPath), Path.GetFileNameWithoutExtension(path)))
            .ThenBy(path => Path.GetFileName(path).Contains("forced", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();
        if (existing is not null)
        {
            try { return ToWebVtt(await File.ReadAllBytesAsync(existing, ct)); }
            catch (IOException ex) { logger.LogDebug(ex, "Could not read local {SubtitleLanguage} subtitles {SubtitlePath}.", language, existing); }
        }

        var choices = await SearchRemoteAsync(videoPath, language, ct);
        var candidate = choices.FirstOrDefault();
        return candidate is null || (!candidate.HashMatch && candidate.MatchScore < 20)
            ? null
            : await DownloadRemoteAsync(videoPath, candidate.FileId, language, ct);
    }

    public async Task<IReadOnlyList<SubtitleChoice>> GetChoicesAsync(string videoPath, string language, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(videoPath)) return [];

        var videoName = Path.GetFileNameWithoutExtension(videoPath);
        var local = EnumerateLocalSubtitles(directory)
            .Select(path => new SubtitleChoice(
                LocalKey(Path.GetRelativePath(directory, path)),
                Path.GetRelativePath(directory, path),
                DetectLanguage(Path.GetFileNameWithoutExtension(path)),
                "local",
                true,
                NameSimilarity(videoName, Path.GetFileNameWithoutExtension(path))))
            .OrderByDescending(item => item.MatchScore)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var remote = await SearchRemoteAsync(videoPath, language, ct);
        var localNames = local.Select(item => NormalizeName(Path.GetFileNameWithoutExtension(item.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return local.Concat(remote
                .Where(item => !localNames.Contains(NormalizeName(Path.GetFileNameWithoutExtension(item.Name))))
                .Select(item => new SubtitleChoice(
                    RemoteKey(item.FileId),
                    item.Name,
                    item.Language,
                    "opensubtitles",
                    false,
                    item.MatchScore)))
            .OrderByDescending(item => item.MatchScore)
            .ThenByDescending(item => item.IsDownloaded)
            .Take(20)
            .ToArray();
    }

    public async Task<bool> SelectAsync(string videoPath, string key, string language, CancellationToken ct)
    {
        if (!TryGetVideoDirectory(videoPath, out var directory)) return false;
        if (TryParseLocalKey(key, out var fileName))
        {
            var path = ResolveLocalPath(directory, fileName);
            return File.Exists(path) && IsSubtitle(path);
        }
        if (TryParseRemoteKey(key, out var fileId))
            return await DownloadRemoteAsync(videoPath, fileId, language, ct) is not null;
        return false;
    }

    public async Task<byte[]?> ReadSelectedAsync(string videoPath, string key, string language, CancellationToken ct)
    {
        if (!TryGetVideoDirectory(videoPath, out var directory)) return null;
        string path;
        if (TryParseLocalKey(key, out var fileName))
            path = ResolveLocalPath(directory, fileName);
        else if (TryParseRemoteKey(key, out var fileId))
            path = CachedRemotePath(videoPath, fileId, language);
        else
            return null;

        if (!File.Exists(path) || !IsSubtitle(path)) return null;
        try { return ToWebVtt(await File.ReadAllBytesAsync(path, ct)); }
        catch (IOException ex) { logger.LogDebug(ex, "Could not read selected subtitles {SubtitlePath}.", path); return null; }
    }

    public static string RemoteKey(int fileId) => $"opensub-{fileId}";

    private async Task<List<RemoteSubtitle>> SearchRemoteAsync(string videoPath, string language, CancellationToken ct)
    {
        var apiKey = credentials.OpenSubtitlesKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return [];

        try
        {
            var client = clients.CreateClient("opensubtitles");
            var videoName = Path.GetFileNameWithoutExtension(videoPath);
            using var search = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseAddress}/subtitles?languages={Uri.EscapeDataString(language)}&query={Uri.EscapeDataString(videoName.Replace('.', ' '))}&order_by=download_count&order_direction=desc");
            AddHeaders(search, apiKey);
            using var response = await client.SendAsync(search, ct);
            if (!response.IsSuccessStatusCode) return [];
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];

            return data.EnumerateArray()
                .Select(item => ParseRemote(item, language, videoName))
                .Where(item => item is not null && (item.HashMatch || (item.MatchScore >= 65 && !ContainsBonusWords(item.Name))))
                .Cast<RemoteSubtitle>()
                .OrderByDescending(item => item.HashMatch)
                .ThenByDescending(item => item.MatchScore)
                .ThenByDescending(item => item.DownloadCount)
                .DistinctBy(item => NormalizeName(item.Name), StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException)
        {
            logger.LogInformation(ex, "Could not search OpenSubtitles for {VideoPath}.", videoPath);
            return [];
        }
    }

    private static RemoteSubtitle? ParseRemote(JsonElement item, string language, string videoName)
    {
        if (!item.TryGetProperty("attributes", out var attributes)
            || !attributes.TryGetProperty("language", out var lang)
            || !string.Equals(lang.GetString(), language, StringComparison.OrdinalIgnoreCase)
            || !attributes.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0
            || !files[0].TryGetProperty("file_id", out var fileIdElement)
            || !fileIdElement.TryGetInt32(out var fileId)) return null;

        var name = files[0].TryGetProperty("file_name", out var fileName) ? fileName.GetString() : null;
        name ??= attributes.TryGetProperty("release", out var release) ? release.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) name = $"Sous-titres OpenSubtitles #{fileId}";
        var score = NameSimilarity(videoName, Path.GetFileNameWithoutExtension(name));
        var hashMatch = attributes.TryGetProperty("moviehash_match", out var hash) && hash.ValueKind == JsonValueKind.True;
        var downloads = attributes.TryGetProperty("download_count", out var count) && count.TryGetInt32(out var value) ? value : 0;
        return new RemoteSubtitle(fileId, name, lang.GetString() ?? language, score, hashMatch, downloads);
    }

    private async Task<byte[]?> DownloadRemoteAsync(string videoPath, int fileId, string language, CancellationToken ct)
    {
        var apiKey = credentials.OpenSubtitlesKey;
        if (string.IsNullOrWhiteSpace(apiKey) || !TryGetVideoDirectory(videoPath, out _)) return null;
        var outputPath = CachedRemotePath(videoPath, fileId, language);
        if (File.Exists(outputPath))
        {
            try { return await File.ReadAllBytesAsync(outputPath, ct); }
            catch (IOException) { }
        }

        try
        {
            var client = clients.CreateClient("opensubtitles");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseAddress}/download")
            {
                Content = JsonContent.Create(new { file_id = fileId, sub_format = "srt" })
            };
            AddHeaders(request, apiKey);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!json.RootElement.TryGetProperty("link", out var link) || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri)) return null;
            var bytes = await client.GetByteArrayAsync(uri, ct);
            if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024) return null;

            var vtt = ToWebVtt(bytes);
            await File.WriteAllBytesAsync(outputPath, vtt, ct);
            return vtt;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException or FormatException)
        {
            logger.LogInformation(ex, "Could not download OpenSubtitles file {FileId} for {VideoPath}.", fileId, videoPath);
            return null;
        }
    }

    private static string CachedRemotePath(string videoPath, int fileId, string language) =>
        Path.Combine(Path.GetDirectoryName(videoPath)!, $"{Path.GetFileNameWithoutExtension(videoPath)}.opensub-{fileId}.{language}.vtt");

    private static string LocalKey(string relativePath) => $"local-{Convert.ToBase64String(Encoding.UTF8.GetBytes(relativePath)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static bool TryParseLocalKey(string key, out string fileName)
    {
        fileName = "";
        if (!key.StartsWith("local-", StringComparison.Ordinal) || key.Length <= 6) return false;
        try
        {
            var token = key[6..].Replace('-', '+').Replace('_', '/');
            token = token.PadRight(token.Length + (4 - token.Length % 4) % 4, '=');
            fileName = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            return !Path.IsPathRooted(fileName) && !fileName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..") && IsSubtitle(fileName);
        }
        catch (FormatException) { return false; }
    }

    private static bool TryParseRemoteKey(string key, out int fileId)
    {
        fileId = 0;
        return key.StartsWith("opensub-", StringComparison.Ordinal)
            && int.TryParse(key[8..], NumberStyles.None, CultureInfo.InvariantCulture, out fileId)
            && fileId > 0;
    }

    private static bool TryGetVideoDirectory(string videoPath, out string directory)
    {
        directory = Path.GetDirectoryName(videoPath) ?? "";
        return directory.Length > 0 && File.Exists(videoPath);
    }

    private static string ResolveLocalPath(string directory, string relativePath) => Path.GetFullPath(Path.Combine(directory, relativePath));

    private static IEnumerable<string> EnumerateLocalSubtitles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(IsSubtitle);

    private static int NameSimilarity(string videoName, string subtitleName)
    {
        var video = NameTokens(videoName);
        var subtitle = NameTokens(subtitleName);
        if (video.Count == 0 || subtitle.Count == 0) return 0;
        var overlap = video.Intersect(subtitle, StringComparer.OrdinalIgnoreCase).Count();
        return (int)Math.Round(100d * overlap / Math.Max(video.Count, subtitle.Count));
    }

    private static HashSet<string> NameTokens(string value) => Regex.Split(NormalizeName(value), @"[^\p{L}\p{N}]+")
        .Where(token => token.Length >= 2 && !GenericReleaseTokens.Contains(token) && !ReleaseGroupTokens.Contains(token) && !StopWords.Contains(token))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeName(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormD), @"\p{Mn}", "").ToLowerInvariant();

    private static bool ContainsBonusWords(string value) => Regex.IsMatch(NormalizeName(value), @"(?:^|[^\p{L}\p{N}])(?:making.of|bonus|featurette|behind.the.scenes|extras?|interview)(?:$|[^\p{L}\p{N}])");

    private static readonly HashSet<string> GenericReleaseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "1080p", "2160p", "720p", "480p", "web", "webrip", "webdl", "bluray", "brrip", "hdtv", "x264", "x265", "h264", "h265", "hevc", "av1", "aac", "aac5", "dts", "hdr", "uhd", "proper", "repack", "multi", "vostfr", "sub", "subs", "forced", "extended", "unrated", "limited", "10bit", "8bit", "ac3", "eac3", "ddp", "atmos", "truehd", "remux", "cam", "telesync", "ts", "amzn", "web-dl", "vf", "vff", "vfq", "vo", "fr", "fre", "french", "en", "eng", "english"
    };

    private static readonly HashSet<string> ReleaseGroupTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "yts", "yify", "gg", "bz", "ettv", "eztv", "rarbg", "ethel", "onlyflix", "slh", "kitsune", "psa", "ntb", "flux", "ctrlhd", "framestor", "ion10", "pahe"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "le", "la", "les", "du", "des", "un", "une", "l"
    };

    private static string DetectLanguage(string fileName)
    {
        foreach (var language in new[] { "fr", "en", "de", "es", "it", "pt", "nl", "ja", "ru" })
            if (SubtitleMarker(language).IsMatch(fileName)) return language;
        return "und";
    }

    private static void AddHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.UserAgent.ParseAdd("MediaPlayer/1.0 (subtitle support)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static bool IsSubtitle(string path) => Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt";

    private static Regex SubtitleMarker(string language)
    {
        var aliases = language switch
        {
            "fr" => "fr|fra|fre|french|francais|français",
            "en" => "en|eng|english",
            "de" => "de|deu|ger|german|deutsch",
            "es" => "es|spa|spanish|espanol|español",
            "it" => "it|ita|italian|italiano",
            "pt" => "pt|por|portuguese|portugues|português",
            "nl" => "nl|nld|dut|dutch|nederlands",
            "ja" => "ja|jpn|japanese",
            "ru" => "ru|rus|russian",
            _ => Regex.Escape(language)
        };
        return new Regex($@"(?:^|[._\- ])(?:{aliases})(?:$|[._\- ])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static byte[] ToWebVtt(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.StartsWith("WEBVTT", StringComparison.Ordinal)) return Encoding.UTF8.GetBytes(text);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(" --> ", StringComparison.Ordinal)) lines[i] = lines[i].Replace(',', '.');
        return Encoding.UTF8.GetBytes("WEBVTT\n\n" + string.Join('\n', lines));
    }

    private sealed record RemoteSubtitle(int FileId, string Name, string Language, int MatchScore, bool HashMatch, int DownloadCount);
}

public sealed record SubtitleChoice(string Key, string Name, string Language, string Source, bool IsDownloaded, int MatchScore);
