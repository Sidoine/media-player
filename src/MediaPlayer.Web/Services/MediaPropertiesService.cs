using MediaInfo.Analysis;

namespace media_player.Services;

public sealed class MediaPropertiesService
{
    private readonly IMediaInfoAnalyzer analyzer = MediaInfoAnalyzer.CreateDefault();
    private readonly SemaphoreSlim analysisGate = new(1, 1);

    public async Task<MediaProperties> ReadAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        var fileName = info.Name;
        var size = info.Exists ? info.Length : 0;

        // Matroska/WebM metadata lives in the EBML header and can be read without
        // loading MediaInfo's native library (which is unavailable in some deployments).
        if (Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase))
        {
            var metadata = MatroskaMetadataReader.Read(path, ct);
            var subtitleTracks = metadata.SubtitleTracks.ToList();
            AddExternalSubtitles(path, subtitleTracks);
            return new MediaProperties(fileName, size, metadata.Duration, metadata.Resolution,
                metadata.AudioTracks, subtitleTracks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        await analysisGate.WaitAsync(ct);
        try
        {
            var result = await analyzer.AnalyzeAsync(path, ct);
            if (!result.Success) return new MediaProperties(fileName, size, null, null, [], []);

            var video = result.BestVideoStream;
            var resolution = video is not null && video.Width > 0 && video.Height > 0
                ? $"{video.Width} × {video.Height} ({ResolutionLabel(video.Height)})"
                : null;
            var duration = result.General.Duration > TimeSpan.Zero ? FormatDuration(result.General.Duration) : null;
            var audioTracks = result.AudioStreams.Select(track => JoinDetails(track.Language, track.CodecName, track.AudioChannelsFriendly)).ToArray();
            var subtitleTracks = result.Subtitles.Select(track => JoinDetails(track.Language, track.Codec.ToString()))
                .Where(track => !string.IsNullOrWhiteSpace(track)).Select(track => track!).ToList();

            AddExternalSubtitles(path, subtitleTracks);

            return new MediaProperties(fileName, size, duration, resolution, audioTracks, subtitleTracks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        finally { analysisGate.Release(); }
    }

    private static void AddExternalSubtitles(string path, ICollection<string> subtitleTracks)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            foreach (var file in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories).Where(IsExternalSubtitle))
                subtitleTracks.Add($"{ExternalSubtitleLanguage(file)} — fichier séparé ({Path.GetExtension(file).TrimStart('.').ToUpperInvariant()})");
        }
    }

    private static string? JoinDetails(params string?[] values)
    {
        var present = values.Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }

    private static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private static string ResolutionLabel(int height) => height >= 2160 ? "4K UHD" : height >= 1440 ? "QHD" : height >= 1080 ? "Full HD" : height >= 720 ? "HD" : "SD";

    private static bool IsExternalSubtitle(string path) => Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa" or ".sub";

    private static string ExternalSubtitleLanguage(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Contains(".fre", StringComparison.OrdinalIgnoreCase) || name.Contains(".fra", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".fr.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fr", StringComparison.OrdinalIgnoreCase)
            || name.Contains("french", StringComparison.OrdinalIgnoreCase)) return "Français";
        if (name.Contains(".eng", StringComparison.OrdinalIgnoreCase) || name.Contains(".en.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".en", StringComparison.OrdinalIgnoreCase) || name.Contains("english", StringComparison.OrdinalIgnoreCase)) return "Anglais";
        return "Langue inconnue";
    }
}

public sealed record MediaProperties(string FileName, long SizeBytes, string? Duration, string? Resolution,
    IReadOnlyList<string?> AudioTracks, IReadOnlyList<string> SubtitleTracks);
