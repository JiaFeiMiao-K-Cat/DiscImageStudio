using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DiscImageStudio.Core;

/// <summary>The tag fields CD-TEXT can use. Any field may be missing.</summary>
public sealed record AudioTags(string? Title, string? Artist, string? Album, string? AlbumArtist)
{
    public static AudioTags None { get; } = new(null, null, null, null);

    public bool IsEmpty => Title is null && Artist is null && Album is null && AlbumArtist is null;

    /// <summary>Fields of this set win; missing ones are taken from <paramref name="other"/>.</summary>
    public AudioTags Or(AudioTags other) => new(Title ?? other.Title, Artist ?? other.Artist,
        Album ?? other.Album, AlbumArtist ?? other.AlbumArtist);
}

/// <summary>
/// Reads title, artist, album and album artist from audio files without decoding audio:
/// ID3v2.2–2.4 (at the start of a file or in a WAV "id3 " chunk), RIFF LIST/INFO (INAM, IART,
/// IPRD), FLAC Vorbis comments and MP4/M4A "ilst" atoms. ID3v2 wins over RIFF INFO, whose
/// text has no declared encoding: it is read as UTF-8 when valid, else in the system ANSI
/// code page (the encoding Windows tools write it in). Unreadable or missing tags yield
/// <see cref="AudioTags.None"/>; this never throws for a damaged tag.
/// </summary>
public static class AudioTagReader
{
    private const int MaximumTagBytes = 16 * 1024 * 1024;

    static AudioTagReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static AudioTags Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(input);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return AudioTags.None;
        }
    }

    public static AudioTags Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            Span<byte> magic = stackalloc byte[12];
            input.Position = 0;
            if (input.ReadAtLeast(magic, 12, throwOnEndOfStream: false) < 12) return AudioTags.None;
            input.Position = 0;
            if (magic[..4].SequenceEqual("RIFF"u8) && magic[8..12].SequenceEqual("WAVE"u8)) return ReadRiff(input);
            if (magic[..3].SequenceEqual("ID3"u8)) return ReadId3(input);
            if (magic[..4].SequenceEqual("fLaC"u8)) return ReadFlac(input);
            if (magic[4..8].SequenceEqual("ftyp"u8)) return ReadMp4(input);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or OverflowException or DecoderFallbackException)
        {
        }
        return AudioTags.None;
    }

    private static AudioTags ReadRiff(Stream input)
    {
        using BinaryReader reader = new(input, Encoding.ASCII, leaveOpen: true);
        input.Position = 12;
        long end = Math.Min(input.Length, reader.ReadUInt32At(4, input) + 8L);
        input.Position = 12;
        AudioTags id3 = AudioTags.None, info = AudioTags.None;
        while (input.Position + 8 <= end)
        {
            string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            uint length = reader.ReadUInt32();
            long start = input.Position;
            long next = start + length + (length & 1);
            if (next > input.Length + 1) break;
            if (id is "id3 " or "ID3 " && length <= MaximumTagBytes)
            {
                id3 = ReadId3(new MemoryStream(reader.ReadBytes((int)length)));
            }
            else if (id == "LIST" && length >= 4 && length <= MaximumTagBytes)
            {
                byte[] list = reader.ReadBytes((int)length);
                if (list.AsSpan(0, 4).SequenceEqual("INFO"u8)) info = ReadInfo(list.AsSpan(4));
            }
            input.Position = next;
        }
        return id3.Or(info);
    }

    private static uint ReadUInt32At(this BinaryReader reader, long position, Stream input)
    {
        input.Position = position;
        return reader.ReadUInt32();
    }

    private static AudioTags ReadInfo(ReadOnlySpan<byte> list)
    {
        string? title = null, artist = null, album = null;
        while (list.Length >= 8)
        {
            string id = Encoding.ASCII.GetString(list[..4]);
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(list[4..8]), (uint)(list.Length - 8));
            string? text = AnsiOrUtf8(list.Slice(8, length));
            switch (id)
            {
                case "INAM": title ??= text; break;
                case "IART": artist ??= text; break;
                case "IPRD": album ??= text; break;
            }
            int step = 8 + length + (length & 1);
            if (step > list.Length) break;
            list = list[step..];
        }
        return new AudioTags(title, artist, album, null);
    }

    private static AudioTags ReadId3(Stream input)
    {
        Span<byte> header = stackalloc byte[10];
        if (input.ReadAtLeast(header, 10, throwOnEndOfStream: false) < 10 || !header[..3].SequenceEqual("ID3"u8))
            return AudioTags.None;
        int version = header[3];
        if (version is < 2 or > 4) return AudioTags.None;
        int size = SyncSafe(header[6..10]);
        if (size <= 0 || size > MaximumTagBytes) return AudioTags.None;
        byte[] body = new byte[size];
        int read = input.ReadAtLeast(body, size, throwOnEndOfStream: false);
        ReadOnlySpan<byte> frames = body.AsSpan(0, read);
        if ((header[5] & 0x80) != 0 && version < 4) frames = RemoveUnsynchronisation(frames);
        if ((header[5] & 0x40) != 0 && version >= 3 && frames.Length >= 4)
        {
            int extended = version == 4 ? SyncSafe(frames[..4]) : BinaryPrimitives.ReadInt32BigEndian(frames) + 4;
            if (extended < 0 || extended > frames.Length) return AudioTags.None;
            frames = frames[extended..];
        }

        string? title = null, artist = null, album = null, albumArtist = null;
        int idBytes = version == 2 ? 3 : 4, headerBytes = version == 2 ? 6 : 10;
        while (frames.Length >= headerBytes && frames[0] != 0)
        {
            string id = Encoding.ASCII.GetString(frames[..idBytes]);
            int length = version switch
            {
                2 => frames[3] << 16 | frames[4] << 8 | frames[5],
                3 => BinaryPrimitives.ReadInt32BigEndian(frames[4..8]),
                _ => SyncSafe(frames[4..8]),
            };
            if (length < 0 || headerBytes + length > frames.Length) break;
            ReadOnlySpan<byte> content = frames.Slice(headerBytes, length);
            bool compressedOrEncrypted = version >= 3 && (frames[9] & (version == 3 ? 0xc0 : 0x0c)) != 0;
            if (!compressedOrEncrypted && content.Length > 1)
            {
                switch (id)
                {
                    case "TIT2" or "TT2": title ??= Id3Text(content); break;
                    case "TPE1" or "TP1": artist ??= Id3Text(content); break;
                    case "TALB" or "TAL": album ??= Id3Text(content); break;
                    case "TPE2" or "TP2": albumArtist ??= Id3Text(content); break;
                }
            }
            frames = frames[(headerBytes + length)..];
        }
        return new AudioTags(title, artist, album, albumArtist);
    }

    private static string? Id3Text(ReadOnlySpan<byte> content)
    {
        byte encoding = content[0];
        ReadOnlySpan<byte> text = content[1..];
        string value = encoding switch
        {
            0 => Encoding.Latin1.GetString(text),
            1 => text.Length >= 2 && text[0] == 0xfe && text[1] == 0xff
                ? Encoding.BigEndianUnicode.GetString(text[2..]) : Encoding.Unicode.GetString(text.Length >= 2 && text[0] == 0xff && text[1] == 0xfe ? text[2..] : text),
            2 => Encoding.BigEndianUnicode.GetString(text),
            3 => Encoding.UTF8.GetString(text),
            _ => string.Empty,
        };
        // ID3v2.4 separates multiple values with NUL; CD-TEXT takes the first.
        int nul = value.IndexOf('\0');
        return Clean(nul >= 0 ? value[..nul] : value);
    }

    private static AudioTags ReadFlac(Stream input)
    {
        input.Position = 4;
        Span<byte> header = stackalloc byte[4];
        while (input.ReadAtLeast(header, 4, throwOnEndOfStream: false) == 4)
        {
            bool last = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7f;
            int length = header[1] << 16 | header[2] << 8 | header[3];
            if (type == 4 && length <= MaximumTagBytes)
            {
                byte[] block = new byte[length];
                input.ReadExactly(block);
                return ReadVorbisComments(block);
            }
            input.Position += length;
            if (last) break;
        }
        return AudioTags.None;
    }

    private static AudioTags ReadVorbisComments(ReadOnlySpan<byte> block)
    {
        int vendor = BinaryPrimitives.ReadInt32LittleEndian(block);
        block = block[(4 + vendor)..];
        int count = BinaryPrimitives.ReadInt32LittleEndian(block);
        block = block[4..];
        string? title = null, artist = null, album = null, albumArtist = null;
        for (int i = 0; i < count && block.Length >= 4; i++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(block);
            if (length < 0 || length > block.Length - 4) break;
            string comment = Encoding.UTF8.GetString(block.Slice(4, length));
            block = block[(4 + length)..];
            int equals = comment.IndexOf('=');
            if (equals <= 0) continue;
            string value = comment[(equals + 1)..];
            switch (comment[..equals].ToUpperInvariant())
            {
                case "TITLE": title ??= Clean(value); break;
                case "ARTIST": artist ??= Clean(value); break;
                case "ALBUM": album ??= Clean(value); break;
                case "ALBUMARTIST" or "ALBUM ARTIST": albumArtist ??= Clean(value); break;
            }
        }
        return new AudioTags(title, artist, album, albumArtist);
    }

    private static AudioTags ReadMp4(Stream input)
    {
        string? title = null, artist = null, album = null, albumArtist = null;
        void Walk(long start, long end, int depth)
        {
            byte[] header = new byte[8];
            byte[] large = new byte[8];
            long position = start;
            while (position + 8 <= end && depth < 8)
            {
                input.Position = position;
                input.ReadExactly(header);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                string type = Encoding.Latin1.GetString(header[4..8]);
                long body = position + 8;
                if (size == 1)
                {
                    input.ReadExactly(large);
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(large);
                    body += 8;
                }
                else if (size == 0) size = end - position;
                if (size < 8 || position + size > end) return;
                switch (type)
                {
                    case "moov" or "udta" or "ilst": Walk(body, position + size, depth + 1); break;
                    case "meta": Walk(body + 4, position + size, depth + 1); break;
                    case "©nam" or "©ART" or "©alb" or "aART":
                        if (size <= MaximumTagBytes && ReadMp4Data(body, position + size) is { } value)
                        {
                            switch (type)
                            {
                                case "©nam": title ??= value; break;
                                case "©ART": artist ??= value; break;
                                case "©alb": album ??= value; break;
                                default: albumArtist ??= value; break;
                            }
                        }
                        break;
                }
                position += size;
            }
        }

        string? ReadMp4Data(long start, long end)
        {
            byte[] bytes = new byte[end - start];
            input.Position = start;
            input.ReadExactly(bytes);
            // "data" atom: size, "data", type (4), locale (4), UTF-8 text.
            if (bytes.Length < 16 || !bytes.AsSpan(4, 4).SequenceEqual("data"u8)) return null;
            int size = BinaryPrimitives.ReadInt32BigEndian(bytes);
            return Clean(Encoding.UTF8.GetString(bytes, 16, Math.Clamp(size - 16, 0, bytes.Length - 16)));
        }

        Walk(0, input.Length, 0);
        return new AudioTags(title, artist, album, albumArtist);
    }

    private static int SyncSafe(ReadOnlySpan<byte> bytes)
        => (bytes[0] & 0x7f) << 21 | (bytes[1] & 0x7f) << 14 | (bytes[2] & 0x7f) << 7 | bytes[3] & 0x7f;

    private static byte[] RemoveUnsynchronisation(ReadOnlySpan<byte> data)
    {
        List<byte> result = new(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xff && i + 1 < data.Length && data[i + 1] == 0) i++;
        }
        return result.ToArray();
    }

    private static string? AnsiOrUtf8(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        if (nul >= 0) bytes = bytes[..nul];
        if (bytes.IsEmpty) return null;
        try
        {
            return Clean(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            int codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            try { return Clean(Encoding.GetEncoding(codePage is 0 or 65001 ? 1252 : codePage).GetString(bytes)); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return Clean(Encoding.Latin1.GetString(bytes));
            }
        }
    }

    private static string? Clean(string? text)
    {
        if (text is null) return null;
        string trimmed = text.Trim('\0', ' ', '\t', '\r', '\n', '﻿');
        return trimmed.Length == 0 ? null : trimmed;
    }
}
