using System.Buffers.Binary;
using System.Text;

namespace media_player.Services;

/// <summary>
/// Reads the small EBML metadata elements needed by the library view. Media clusters
/// are skipped by seeking, so probing a large MKV does not read its audio/video data.
/// </summary>
internal static class MatroskaMetadataReader
{
    private const ulong SegmentId = 0x18538067;
    private const ulong InfoId = 0x1549A966;
    private const ulong TracksId = 0x1654AE6B;
    private const ulong TimestampScaleId = 0x2AD7B1;
    private const ulong DurationId = 0x4489;
    private const ulong TrackEntryId = 0xAE;
    private const ulong TrackTypeId = 0x83;
    private const ulong CodecIdId = 0x86;
    private const ulong LanguageId = 0x22B59C;
    private const ulong LanguageIetfId = 0x22B59D;
    private const ulong VideoId = 0xE0;
    private const ulong PixelWidthId = 0xB0;
    private const ulong PixelHeightId = 0xBA;
    private const ulong AudioId = 0xE1;
    private const ulong ChannelsId = 0x9F;

    public static MatroskaMetadata Read(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, FileOptions.RandomAccess);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        double? durationUnits = null;
        ulong timestampScale = 1_000_000;
        var tracks = new List<TrackMetadata>();
        var foundSegment = false;

        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = ReadHeader(reader, stream.Length);
            if (element.Id == SegmentId)
            {
                foundSegment = true;
                var segmentEnd = element.End ?? stream.Length;
                ReadChildren(reader, segmentEnd, cancellationToken, child =>
                {
                    if (child.Id == InfoId)
                    {
                        ReadChildren(reader, child.End!.Value, cancellationToken, info =>
                        {
                            if (info.Id == TimestampScaleId) timestampScale = ReadUnsigned(reader, info);
                            else if (info.Id == DurationId) durationUnits = ReadFloat(reader, info);
                        });
                    }
                    else if (child.Id == TracksId)
                    {
                        ReadChildren(reader, child.End!.Value, cancellationToken, track =>
                        {
                            if (track.Id == TrackEntryId) tracks.Add(ReadTrack(reader, track, cancellationToken));
                        });
                    }
                }, () => durationUnits.HasValue && tracks.Count > 0);
                break;
            }

            stream.Position = element.End ?? stream.Length;
        }

        if (!foundSegment) throw new InvalidDataException("The file does not contain an EBML Segment element.");

        var video = tracks.FirstOrDefault(track => track.Type == 1);
        string? duration = null;
        if (durationUnits is > 0)
        {
            var elapsed = TimeSpan.FromTicks(checked((long)(durationUnits.Value * timestampScale / 100)));
            duration = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
        var resolution = video is { Width: > 0, Height: > 0 }
            ? $"{video.Width} × {video.Height} ({ResolutionLabel(video.Height.Value)})"
            : null;
        var audio = tracks.Where(track => track.Type == 2)
            .Select(track => JoinDetails(LanguageName(track.Language), CodecName(track.CodecId), ChannelLabel(track.Channels)))
            .ToArray();
        var subtitles = tracks.Where(track => track.Type == 17)
            .Select(track => JoinDetails(LanguageName(track.Language), CodecName(track.CodecId)))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();

        return new MatroskaMetadata(duration, resolution, audio, subtitles);
    }

    private static TrackMetadata ReadTrack(BinaryReader reader, EbmlElement element, CancellationToken cancellationToken)
    {
        var track = new TrackMetadata();
        ReadChildren(reader, element.End!.Value, cancellationToken, child =>
        {
            switch (child.Id)
            {
                case TrackTypeId: track.Type = checked((int)ReadUnsigned(reader, child)); break;
                case CodecIdId: track.CodecId = ReadString(reader, child); break;
                case LanguageId:
                case LanguageIetfId: track.Language = ReadString(reader, child); break;
                case VideoId:
                    ReadChildren(reader, child.End!.Value, cancellationToken, video =>
                    {
                        if (video.Id == PixelWidthId) track.Width = checked((int)ReadUnsigned(reader, video));
                        else if (video.Id == PixelHeightId) track.Height = checked((int)ReadUnsigned(reader, video));
                    });
                    break;
                case AudioId:
                    ReadChildren(reader, child.End!.Value, cancellationToken, audio =>
                    {
                        if (audio.Id == ChannelsId) track.Channels = checked((int)ReadUnsigned(reader, audio));
                    });
                    break;
            }
        });
        return track;
    }

    private static void ReadChildren(BinaryReader reader, long parentEnd, CancellationToken cancellationToken,
        Action<EbmlElement> readKnownElement, Func<bool>? stopWhen = null)
    {
        var stream = reader.BaseStream;
        if (parentEnd < stream.Position || parentEnd > stream.Length)
            throw new InvalidDataException("An EBML element extends beyond the file boundary.");

        while (stream.Position < parentEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopWhen?.Invoke() == true) break;
            var element = ReadHeader(reader, parentEnd);
            if (element.End is null) break;
            readKnownElement(element);
            if (stream.Position < element.End.Value) stream.Position = element.End.Value;
            if (stream.Position > element.End.Value)
                throw new InvalidDataException("An EBML child extends beyond its parent element.");
        }
        if (stream.Position < parentEnd) stream.Position = parentEnd;
    }

    private static EbmlElement ReadHeader(BinaryReader reader, long parentEnd)
    {
        var stream = reader.BaseStream;
        var first = ReadRequiredByte(reader, parentEnd);
        var idLength = VintLength(first);
        if (idLength > 4) throw new InvalidDataException("Invalid EBML element ID length.");
        ulong id = first;
        for (var i = 1; i < idLength; i++) id = (id << 8) | ReadRequiredByte(reader, parentEnd);

        var sizeFirst = ReadRequiredByte(reader, parentEnd);
        var sizeLength = VintLength(sizeFirst);
        if (sizeLength > 8) throw new InvalidDataException("Invalid EBML element size length.");
        var marker = (byte)(0x80 >> (sizeLength - 1));
        ulong size = (ulong)(sizeFirst & (marker - 1));
        var unknownSize = size == (ulong)(marker - 1);
        for (var i = 1; i < sizeLength; i++)
        {
            var next = ReadRequiredByte(reader, parentEnd);
            size = (size << 8) | next;
            unknownSize &= next == byte.MaxValue;
        }

        var dataOffset = stream.Position;
        if (unknownSize) return new EbmlElement(id, dataOffset, null);
        if (size > long.MaxValue || dataOffset > parentEnd - (long)size)
            throw new InvalidDataException("An EBML element extends beyond its parent.");
        return new EbmlElement(id, dataOffset, dataOffset + (long)size);
    }

    private static byte ReadRequiredByte(BinaryReader reader, long end)
    {
        if (reader.BaseStream.Position >= end) throw new InvalidDataException("Unexpected end of EBML data.");
        return reader.ReadByte();
    }

    private static int VintLength(byte first)
    {
        if (first == 0) throw new InvalidDataException("Invalid zero EBML variable integer.");
        for (var length = 1; length <= 8; length++)
            if ((first & (0x80 >> (length - 1))) != 0) return length;
        throw new InvalidDataException("Invalid EBML variable integer.");
    }

    private static ulong ReadUnsigned(BinaryReader reader, EbmlElement element)
    {
        var length = ElementLength(element, max: 8);
        ulong result = 0;
        for (var i = 0; i < length; i++) result = (result << 8) | reader.ReadByte();
        return result;
    }

    private static double ReadFloat(BinaryReader reader, EbmlElement element)
    {
        var length = ElementLength(element, max: 8);
        return length switch
        {
            4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4))),
            8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(8))),
            _ => throw new InvalidDataException("Invalid EBML floating-point value length.")
        };
    }

    private static string ReadString(BinaryReader reader, EbmlElement element)
    {
        var length = ElementLength(element, max: 4096);
        return Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
    }

    private static int ElementLength(EbmlElement element, int max)
    {
        if (element.End is null) throw new InvalidDataException("An EBML scalar has unknown size.");
        var length = element.End.Value - element.DataOffset;
        if (length < 0 || length > max) throw new InvalidDataException("An EBML scalar has an invalid size.");
        return (int)length;
    }

    private static string? CodecName(string? codec) => codec switch
    {
        "V_MPEGH/ISO/HEVC" => "HEVC",
        "V_MPEG4/ISO/AVC" => "H.264/AVC",
        "V_AV1" => "AV1",
        "V_VP9" => "VP9",
        "V_VP8" => "VP8",
        "A_EAC3" => "E-AC-3",
        "A_AC3" => "AC-3",
        "A_AAC" or "A_AAC/MPEG2/LC" or "A_AAC/MPEG4/LC" => "AAC",
        "A_OPUS" => "Opus",
        "A_VORBIS" => "Vorbis",
        "A_FLAC" => "FLAC",
        "A_TRUEHD" => "TrueHD",
        "A_DTS" or "A_DTS/EXPRESS" => "DTS",
        "S_TEXT/UTF8" => "SRT",
        "S_TEXT/ASS" => "ASS",
        "S_TEXT/SSA" => "SSA",
        "S_HDMV/PGS" => "PGS",
        null or "" => null,
        _ => codec ?? "Unknown"
    };

    private static string? LanguageName(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        return language.ToLowerInvariant() switch
        {
            "eng" or "en" or "en-us" or "en-gb" => "Anglais",
            "fre" or "fra" or "fr" or "fr-fr" => "Français",
            "spa" or "es" => "Espagnol",
            "ita" or "it" => "Italien",
            "deu" or "ger" or "de" => "Allemand",
            "por" or "pt" => "Portugais",
            "jpn" or "ja" => "Japonais",
            "und" => null,
            _ => language
        };
    }

    private static string? ChannelLabel(int? channels) => channels switch
    {
        1 => "1.0",
        2 => "2.0",
        3 => "2.1",
        4 => "4.0",
        5 => "5.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        > 0 => $"{channels} canaux",
        _ => null
    };

    private static string? JoinDetails(params string?[] values)
    {
        var present = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }

    private static string ResolutionLabel(int height) => height >= 2160 ? "4K UHD" : height >= 1440 ? "QHD" : height >= 1080 ? "Full HD" : height >= 720 ? "HD" : "SD";

    private sealed record EbmlElement(ulong Id, long DataOffset, long? End);

    private sealed class TrackMetadata
    {
        public int Type { get; set; }
        public string? CodecId { get; set; }
        public string? Language { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? Channels { get; set; }
    }
}

internal sealed record MatroskaMetadata(string? Duration, string? Resolution,
    IReadOnlyList<string?> AudioTracks, IReadOnlyList<string> SubtitleTracks);
