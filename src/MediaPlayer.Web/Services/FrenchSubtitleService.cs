using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace media_player.Services;

public sealed class FrenchSubtitleService(IHttpClientFactory clients, ApiCredentialStore credentials, ILogger<FrenchSubtitleService> logger)
{
    private static readonly Regex FrenchMarker = new(@"(?:^|[._\- ])(?:fr|fra|fre|french|francais|français)(?:$|[._\- ])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string BaseAddress = "https://api.opensubtitles.com/api/v1";

    public async Task<byte[]?> GetSubtitleAsync(string videoPath, string title, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(videoPath)) return null;

        var existing = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => IsSubtitle(path) && FrenchMarker.IsMatch(Path.GetFileNameWithoutExtension(path)))
            .OrderBy(path => Path.GetFileName(path).Contains("forced", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();
        if (existing is not null)
        {
            try { return ToWebVtt(await File.ReadAllBytesAsync(existing, ct)); }
            catch (IOException ex) { logger.LogDebug(ex, "Could not read local French subtitles {SubtitlePath}.", existing); }
        }

        var apiKey = credentials.OpenSubtitlesKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            var client = clients.CreateClient("opensubtitles");
            using var search = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseAddress}/subtitles?languages=fr&query={Uri.EscapeDataString(Path.GetFileNameWithoutExtension(videoPath).Replace('.', ' '))}");
            AddHeaders(search, apiKey);
            using var searchResponse = await client.SendAsync(search, ct);
            if (!searchResponse.IsSuccessStatusCode) return null;
            using var searchJson = JsonDocument.Parse(await searchResponse.Content.ReadAsStreamAsync(ct));
            if (!searchJson.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

            var candidate = data.EnumerateArray()
                .Where(item => item.TryGetProperty("attributes", out var attributes)
                    && attributes.TryGetProperty("language", out var language)
                    && string.Equals(language.GetString(), "fr", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.GetProperty("attributes").TryGetProperty("moviehash_match", out var exact) && exact.ValueKind == JsonValueKind.True)
                .ThenByDescending(item => item.GetProperty("attributes").TryGetProperty("download_count", out var count) && count.TryGetInt32(out var value) ? value : 0)
                .FirstOrDefault();
            if (candidate.ValueKind != JsonValueKind.Object
                || !candidate.GetProperty("attributes").TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0
                || !files[0].TryGetProperty("file_id", out var fileId)) return null;

            using var downloadRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseAddress}/download")
            {
                Content = JsonContent.Create(new { file_id = fileId.GetInt32(), sub_format = "srt" })
            };
            AddHeaders(downloadRequest, apiKey);
            using var downloadResponse = await client.SendAsync(downloadRequest, ct);
            if (!downloadResponse.IsSuccessStatusCode) return null;
            using var downloadJson = JsonDocument.Parse(await downloadResponse.Content.ReadAsStreamAsync(ct));
            if (!downloadJson.RootElement.TryGetProperty("link", out var link) || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var subtitleUri)) return null;

            var bytes = await client.GetByteArrayAsync(subtitleUri, ct);
            if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024) return null;
            var vtt = ToWebVtt(bytes);
            var outputPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(videoPath) + ".fr.vtt");
            await File.WriteAllBytesAsync(outputPath, vtt, ct);
            return vtt;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException or FormatException)
        {
            logger.LogInformation(ex, "French subtitles are not available for {MediaTitle}.", title);
            return null;
        }
    }

    private static void AddHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.UserAgent.ParseAdd("MediaPlayer/1.0 (French subtitle support)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static bool IsSubtitle(string path) => Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt";

    private static byte[] ToWebVtt(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.StartsWith("WEBVTT", StringComparison.Ordinal)) return Encoding.UTF8.GetBytes(text);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(" --> ", StringComparison.Ordinal)) lines[i] = lines[i].Replace(',', '.');
        return Encoding.UTF8.GetBytes("WEBVTT\n\n" + string.Join('\n', lines));
    }
}
