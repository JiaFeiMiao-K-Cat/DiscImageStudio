using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DiscImageStudio.Core;

/// <summary>Title and performer of one audio track, as written to CD-TEXT.</summary>
public sealed record CdTextTrack(string? Title = null, string? Performer = null);

/// <summary>
/// The CD-TEXT of one audio session: the album (disc) title and performer, and one entry per
/// track in disc order. Only pack types 0x80 (title) and 0x81 (performer) are written.
/// </summary>
public sealed record CdTextAlbum(string? Title, string? Performer, IReadOnlyList<CdTextTrack> Tracks)
{
    /// <summary>True when no field holds visible text; such an album is not written at all.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Performer)
        && Tracks.All(track => string.IsNullOrWhiteSpace(track.Title) && string.IsNullOrWhiteSpace(track.Performer));

    /// <summary>Same texts, trimmed; empty strings become null.</summary>
    public CdTextAlbum Normalized() => new(Clean(Title), Clean(Performer),
        Tracks.Select(track => new CdTextTrack(Clean(track.Title), Clean(track.Performer))).ToArray());

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Character code of a CD-TEXT block (byte 0 of the size information).</summary>
public enum CdTextCharacterCode
{
    Iso8859_1 = 0x00,
    Ascii = 0x01,
    MsJis = 0x80,
}

/// <summary>Packs ready for the lead-in, and what had to be changed to produce them.</summary>
/// <param name="Packs">Complete 18-byte packs (header, 12 payload bytes, CRC) of block 0.</param>
/// <param name="Unrepresentable">Characters that the block's character code cannot hold;
/// each was written as a question mark.</param>
public sealed record CdTextEncoding(
    byte[] Packs,
    CdTextCharacterCode CharacterCode,
    int LanguageCode,
    IReadOnlyList<string> Unrepresentable)
{
    public int PackCount => Packs.Length / CdTextPacks.PackBytes;
}

/// <summary>A CD-TEXT block read back from a disc.</summary>
public sealed record CdTextReadResult(
    CdTextAlbum Album,
    CdTextCharacterCode CharacterCode,
    int FirstTrack,
    int LastTrack,
    int PackCount,
    int BadCrcPacks);

/// <summary>
/// CD-TEXT pack encoding and decoding (MMC-3 Annex J, Sony CD-TEXT specification, as
/// documented by libburn's doc/cdtext.txt). Each pack has a 4-byte header (type, track,
/// sequence, block/character position), 12 payload bytes and a CRC-16 (polynomial 0x11021,
/// inverted, big endian). Texts of one pack type are concatenated with NUL terminators and
/// cut into 12-byte payloads; a track text identical to the previous track's is written as
/// a TAB. Three 0x8F packs close the block with the pack counts and the language.
/// </summary>
public static class CdTextPacks
{
    public const int PackBytes = 18;
    public const int PayloadBytes = 12;
    /// <summary>Sequence numbers are one byte per block, three of them are size packs.</summary>
    public const int MaximumTextPacks = 253;
    public const int LanguageEnglish = 0x09;
    public const int LanguageJapanese = 0x69;
    private const byte Title = 0x80;
    private const byte Performer = 0x81;
    private const byte SizeInformation = 0x8f;

    static CdTextPacks() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding ShiftJis => Encoding.GetEncoding(932,
        EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);

    /// <summary>Encodes the album for block 0. Text that fits ISO-8859-1 is written as single
    /// bytes (English language code); anything else is written as MS-JIS double-byte text
    /// (Japanese language code), where ASCII becomes its full-width form so every character
    /// takes two bytes. Characters outside both sets are reported and replaced by '?'.</summary>
    public static CdTextEncoding Encode(CdTextAlbum album, int firstTrack = 1)
    {
        ArgumentNullException.ThrowIfNull(album);
        album = album.Normalized();
        if (album.Tracks.Count is 0 or > 99) throw new ArgumentOutOfRangeException(nameof(album), "CD-TEXT needs 1..99 tracks.");
        if (firstTrack < 1 || firstTrack + album.Tracks.Count - 1 > 99) throw new ArgumentOutOfRangeException(nameof(firstTrack));
        IEnumerable<string> all = new[] { album.Title, album.Performer }
            .Concat(album.Tracks.SelectMany(track => new[] { track.Title, track.Performer }))
            .Where(text => text is not null)!;
        bool latin = all.All(text => text.All(IsLatin1Printable));
        List<string> unrepresentable = [];
        Func<string?, byte[]> encode = latin
            ? text => text is null ? [] : text.Select(c => (byte)c).ToArray()
            : text => text is null ? [] : EncodeMsJis(text, unrepresentable);
        var types = new List<(byte Type, IReadOnlyList<byte[]> Texts)>();
        void Add(byte type, string? disc, Func<CdTextTrack, string?> select)
        {
            if (disc is null && album.Tracks.All(track => select(track) is null)) return;
            types.Add((type, new[] { encode(disc) }.Concat(album.Tracks.Select(track => encode(select(track)))).ToArray()));
        }
        Add(Title, album.Title, track => track.Title);
        Add(Performer, album.Performer, track => track.Performer);
        if (types.Count == 0) throw new ArgumentException("CD-TEXT has no text to write.", nameof(album));
        CdTextCharacterCode code = latin ? CdTextCharacterCode.Iso8859_1 : CdTextCharacterCode.MsJis;
        int language = latin ? LanguageEnglish : LanguageJapanese;
        byte[] packs = Build(types, code, firstTrack, firstTrack + album.Tracks.Count - 1, language);
        return new CdTextEncoding(packs, code, language, unrepresentable.Distinct().ToArray());
    }

    /// <summary>Builds block 0 from already encoded texts: for every pack type, element 0 is the
    /// disc text and element i the text of track firstTrack + i - 1 (without terminator).</summary>
    public static byte[] Build(IReadOnlyList<(byte Type, IReadOnlyList<byte[]> Texts)> types,
        CdTextCharacterCode characterCode, int firstTrack, int lastTrack, int languageCode,
        bool copyright = false, bool repeatWithTab = true)
    {
        ArgumentNullException.ThrowIfNull(types);
        bool doubleByte = characterCode == CdTextCharacterCode.MsJis;
        int terminator = doubleByte ? 2 : 1;
        List<byte[]> packs = [];
        int[] counts = new int[16];
        foreach ((byte type, IReadOnlyList<byte[]> texts) in types.OrderBy(entry => entry.Type))
        {
            if (type is < 0x80 or >= SizeInformation) throw new ArgumentOutOfRangeException(nameof(types));
            if (texts.Count != lastTrack - firstTrack + 2) throw new ArgumentException("One disc text and one text per track are required.", nameof(types));
            // Serialise "disc\0track1\0track2\0…", remembering which text owns every byte and
            // the character index of that byte inside its text.
            List<byte> stream = [];
            List<(int Track, int Character)> owner = [];
            for (int index = 0; index < texts.Count; index++)
            {
                byte[] text = texts[index];
                bool repeat = repeatWithTab && index >= 2 && texts[index].Length > 0
                    && texts[index].AsSpan().SequenceEqual(texts[index - 1]);
                byte[] body = repeat ? (doubleByte ? [9, 9] : [9]) : text;
                int track = index == 0 ? 0 : firstTrack + index - 1;
                for (int i = 0; i < body.Length + terminator; i++)
                {
                    stream.Add(i < body.Length ? body[i] : (byte)0);
                    owner.Add((track, i / (doubleByte ? 2 : 1)));
                }
            }

            for (int offset = 0; offset < stream.Count; offset += PayloadBytes)
            {
                byte[] pack = new byte[PackBytes];
                pack[0] = type;
                pack[1] = (byte)owner[offset].Track;
                pack[3] = (byte)((doubleByte ? 0x80 : 0) | Math.Min(15, owner[offset].Character));
                int length = Math.Min(PayloadBytes, stream.Count - offset);
                for (int i = 0; i < length; i++) pack[4 + i] = stream[offset + i];
                packs.Add(pack);
                counts[type - 0x80]++;
            }
        }

        if (packs.Count > MaximumTextPacks)
            throw new CdTextTooLongException(packs.Count);
        counts[0x0f] = 3;
        int lastSequence = packs.Count + 2;
        byte[] size = new byte[36];
        size[0] = (byte)characterCode;
        size[1] = (byte)firstTrack;
        size[2] = (byte)lastTrack;
        size[3] = copyright ? (byte)3 : (byte)0;
        for (int i = 0; i < 16; i++) size[4 + i] = (byte)counts[i];
        size[20] = (byte)lastSequence;
        size[28] = (byte)languageCode;
        for (int part = 0; part < 3; part++)
        {
            byte[] pack = new byte[PackBytes];
            pack[0] = SizeInformation;
            pack[1] = (byte)part;
            size.AsSpan(part * PayloadBytes, PayloadBytes).CopyTo(pack.AsSpan(4));
            packs.Add(pack);
        }

        byte[] result = new byte[packs.Count * PackBytes];
        for (int i = 0; i < packs.Count; i++)
        {
            packs[i][2] = (byte)i;
            WriteCrc(packs[i]);
            packs[i].CopyTo(result, i * PackBytes);
        }
        return result;
    }

    /// <summary>CRC-16/CCITT (x^16+x^12+x^5+1, initial 0) of bytes 0..15, inverted.</summary>
    public static ushort Crc(ReadOnlySpan<byte> header16)
    {
        ushort crc = 0;
        foreach (byte value in header16[..16])
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return (ushort)~crc;
    }

    public static bool HasValidCrc(ReadOnlySpan<byte> pack)
        => pack.Length >= PackBytes && BinaryPrimitives.ReadUInt16BigEndian(pack[16..]) == Crc(pack);

    private static void WriteCrc(byte[] pack) => BinaryPrimitives.WriteUInt16BigEndian(pack.AsSpan(16), Crc(pack));

    /// <summary>Splits each 18-byte pack into 24 six-bit symbols for the R-W subchannel of the
    /// lead-in (four packs per 96-byte block), as SAO recording expects them.</summary>
    public static byte[] ToSubchannel(ReadOnlySpan<byte> packs)
    {
        if (packs.Length % PackBytes != 0) throw new ArgumentException("Packs must be 18 bytes each.", nameof(packs));
        byte[] result = new byte[packs.Length / 3 * 4];
        for (int source = 0, target = 0; source < packs.Length; source += 3, target += 4)
        {
            int value = packs[source] << 16 | packs[source + 1] << 8 | packs[source + 2];
            result[target] = (byte)(value >> 18 & 0x3f);
            result[target + 1] = (byte)(value >> 12 & 0x3f);
            result[target + 2] = (byte)(value >> 6 & 0x3f);
            result[target + 3] = (byte)(value & 0x3f);
        }
        return result;
    }

    /// <summary>Inverse of <see cref="ToSubchannel"/>.</summary>
    public static byte[] FromSubchannel(ReadOnlySpan<byte> symbols)
    {
        if (symbols.Length % 24 != 0) throw new ArgumentException("Subchannel packs are 24 bytes each.", nameof(symbols));
        byte[] result = new byte[symbols.Length / 4 * 3];
        for (int source = 0, target = 0; source < symbols.Length; source += 4, target += 3)
        {
            int value = (symbols[source] & 0x3f) << 18 | (symbols[source + 1] & 0x3f) << 12
                | (symbols[source + 2] & 0x3f) << 6 | symbols[source + 3] & 0x3f;
            result[target] = (byte)(value >> 16);
            result[target + 1] = (byte)(value >> 8);
            result[target + 2] = (byte)value;
        }
        return result;
    }

    /// <summary>Decodes block 0 of packs as returned by READ TOC/PMA/ATIP format 0101b
    /// (without its 4-byte header). Packs with a bad CRC are counted and skipped.</summary>
    public static CdTextReadResult Decode(ReadOnlySpan<byte> packs)
    {
        int bad = 0;
        var byType = new SortedDictionary<int, List<byte[]>>();
        byte[]? size = null;
        int count = 0;
        for (int offset = 0; offset + PackBytes <= packs.Length; offset += PackBytes)
        {
            ReadOnlySpan<byte> pack = packs.Slice(offset, PackBytes);
            if (((pack[3] >> 4) & 7) != 0) continue; // other language blocks
            count++;
            if (!HasValidCrc(pack)) { bad++; continue; }
            if (pack[0] == SizeInformation)
            {
                size ??= new byte[36];
                if (pack[1] < 3) pack.Slice(4, PayloadBytes).CopyTo(size.AsSpan(pack[1] * PayloadBytes));
                continue;
            }
            if (!byType.TryGetValue(pack[0], out List<byte[]>? list)) byType[pack[0]] = list = [];
            list.Add(pack.ToArray());
        }

        if (size is null) throw new InvalidDataException("CD-TEXT has no size information (0x8F) in block 0.");
        var code = (CdTextCharacterCode)size[0];
        int first = size[1], last = size[2];
        if (first < 1 || last < first || last > 99) throw new InvalidDataException($"CD-TEXT reports tracks {first}..{last}.");
        bool doubleByte = code == CdTextCharacterCode.MsJis || byType.Values.SelectMany(p => p).Any(p => (p[3] & 0x80) != 0);
        string?[] Texts(int type)
        {
            string?[] texts = new string?[last - first + 2];
            if (!byType.TryGetValue(type, out List<byte[]>? list)) return texts;
            List<byte> stream = [];
            int startTrack = list.OrderBy(p => p[2]).First()[1];
            foreach (byte[] pack in list.OrderBy(p => p[2])) stream.AddRange(pack.AsSpan(4, PayloadBytes).ToArray());
            int index = startTrack == 0 ? 0 : startTrack - first + 1;
            int step = doubleByte ? 2 : 1;
            List<byte> current = [];
            for (int i = 0; i + step - 1 < stream.Count && index < texts.Length; i += step)
            {
                bool end = doubleByte ? stream[i] == 0 && stream[i + 1] == 0 : stream[i] == 0;
                if (!end)
                {
                    for (int j = 0; j < step; j++) current.Add(stream[i + j]);
                    continue;
                }
                byte[] bytes = current.ToArray();
                bool tab = doubleByte ? bytes is [9, 9] : bytes is [9];
                texts[index] = tab && index >= 2 ? texts[index - 1] : DecodeText(bytes, code);
                index++;
                current.Clear();
            }
            return texts;
        }

        string?[] titles = Texts(Title), performers = Texts(Performer);
        CdTextTrack[] tracks = Enumerable.Range(1, last - first + 1)
            .Select(i => new CdTextTrack(titles[i], performers[i])).ToArray();
        CdTextAlbum album = new CdTextAlbum(titles[0], performers[0], tracks).Normalized();
        return new CdTextReadResult(album, code, first, last, count, bad);
    }

    /// <summary>Compares what was written with what was read back, track by track. Returns
    /// one line per difference; an empty list means the disc holds exactly the expected text
    /// in the expected track order.</summary>
    public static IReadOnlyList<string> Compare(CdTextAlbum expected, CdTextReadResult actual, int firstTrack = 1)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        CdTextAlbum written = Encode(expected, firstTrack) is var encoded
            ? Decode(encoded.Packs).Album : expected; // what the chosen character code can carry
        List<string> differences = [];
        if (actual.BadCrcPacks > 0) differences.Add($"{actual.BadCrcPacks} pack(s) failed the CRC check");
        if (actual.FirstTrack != firstTrack || actual.LastTrack != firstTrack + written.Tracks.Count - 1)
            differences.Add($"tracks {actual.FirstTrack}..{actual.LastTrack}, expected {firstTrack}..{firstTrack + written.Tracks.Count - 1}");
        void Check(string field, string? want, string? got)
        {
            if (!string.Equals(want ?? string.Empty, got ?? string.Empty, StringComparison.Ordinal))
                differences.Add($"{field}: expected \"{want}\", read \"{got}\"");
        }
        Check("album title", written.Title, actual.Album.Title);
        Check("album performer", written.Performer, actual.Album.Performer);
        for (int i = 0; i < written.Tracks.Count; i++)
        {
            CdTextTrack? got = i < actual.Album.Tracks.Count ? actual.Album.Tracks[i] : null;
            string number = (firstTrack + i).ToString("00", CultureInfo.InvariantCulture);
            Check($"track {number} title", written.Tracks[i].Title, got?.Title);
            Check($"track {number} performer", written.Tracks[i].Performer, got?.Performer);
        }
        return differences;
    }

    private static bool IsLatin1Printable(char c) => c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ';

    private static byte[] EncodeMsJis(string text, List<string> unrepresentable)
    {
        Encoding sjis = ShiftJis;
        List<byte> bytes = [];
        // NFKC first turns half-width katakana into full-width; ASCII then gets its
        // full-width form so the block stays strictly double-byte.
        string normalized = text.Normalize(NormalizationForm.FormKC);
        for (int i = 0; i < normalized.Length; i++)
        {
            string element = char.IsSurrogatePair(normalized, i) ? normalized.Substring(i++, 2) : normalized[i].ToString();
            string[] candidates = element.Length != 1 ? [element]
                : element[0] == '~' ? ["\uff5e", "\u301c"] : [Widen(element[0]).ToString()];
            byte[] encoded = [];
            foreach (string candidate in candidates)
            {
                try { encoded = sjis.GetBytes(candidate); break; }
                catch (EncoderFallbackException) { }
            }
            if (encoded.Length != 2)
            {
                unrepresentable.Add(element);
                encoded = sjis.GetBytes("？");
            }
            bytes.AddRange(encoded);
        }
        return bytes.ToArray();
    }

    private static char Widen(char c) => c switch
    {
        ' ' => '　',
        '~' => '〜', // wave dash: Shift-JIS has no full-width tilde in its base set
        >= '!' and <= '}' => (char)(c - '!' + '！'),
        '–' or '—' => '―', // en/em dash → horizontal bar (0x815C)
        _ => c,
    };

    private static string DecodeText(byte[] bytes, CdTextCharacterCode code)
    {
        if (code == CdTextCharacterCode.MsJis)
        {
            // Full-width ASCII written by Encode comes back as ordinary ASCII.
            string text = ShiftJis.GetString(bytes);
            return new string(text.Select(c => c switch
            {
                '　' => ' ',
                >= '！' and <= '｝' => (char)(c - '！' + '!'),
                _ => c,
            }).ToArray());
        }
        return new string(bytes.Select(b => (char)b).ToArray());
    }
}

/// <summary>The album's texts need more than the 253 packs one CD-TEXT block can hold.</summary>
public sealed class CdTextTooLongException(int packs)
    : ArgumentException($"CD-TEXT needs {packs} packs; one block holds at most {CdTextPacks.MaximumTextPacks}.")
{
    public int Packs { get; } = packs;
}
