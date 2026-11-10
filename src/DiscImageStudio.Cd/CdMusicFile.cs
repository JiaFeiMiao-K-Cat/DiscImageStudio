using System.Globalization;
using System.IO;
using System.Text;

namespace DiscImageStudio.Cd;

/// <summary>
/// One music track for a CD-Extra audio session: the PCM range of a CD-format WAV
/// file plus the file identity that was measured. The audio is written verbatim;
/// only digital silence is appended to complete the last 2352-byte sector and, for
/// very short files, the Red Book minimum track length of four seconds.
/// </summary>
public sealed record CdMusicTrackInfo(
    string Path,
    long PcmOffset,
    long PcmBytes,
    long FileLength,
    DateTime LastWriteTimeUtc)
{
    public const int BytesPerSecond = 176_400;

    /// <summary>Whole sectors holding the PCM, without the four-second minimum.</summary>
    public long PcmSectors => (PcmBytes + CdDiscParameters.BytesPerSector - 1) / CdDiscParameters.BytesPerSector;

    /// <summary>Sectors written for this track, including padding silence.</summary>
    public long RecordedSectors => Math.Max(CdExtraLayout.MinimumTrackSectors, PcmSectors);

    public long RecordedBytes => checked(RecordedSectors * CdDiscParameters.BytesPerSector);

    public long PaddingBytes => RecordedBytes - PcmBytes;

    /// <summary>True when silence is appended to reach the four-second minimum.</summary>
    public bool PaddedToMinimumLength => PcmSectors < CdExtraLayout.MinimumTrackSectors;

    public TimeSpan Duration => TimeSpan.FromTicks(PcmBytes * TimeSpan.TicksPerSecond / BytesPerSecond);
}

/// <summary>Music files held open for a burn (see <see cref="CdMusicFile.PinUnchanged"/>):
/// writers, renames and deletes are refused while it is held, and the samples are read
/// through these verified handles.</summary>
public sealed class CdMusicPin : IDisposable
{
    private readonly Dictionary<CdMusicTrackInfo, FileStream> _streams;
    private bool _disposed;

    internal CdMusicPin(Dictionary<CdMusicTrackInfo, FileStream> streams) => _streams = streams;

    /// <summary>Writes the track's PCM through its pinned handle, followed by digital silence
    /// up to <see cref="CdMusicTrackInfo.RecordedBytes"/>. Safe to call from several
    /// producer threads: every read is positional.</summary>
    public void WritePaddedPcm(CdMusicTrackInfo track, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(output);
        FileStream? input;
        lock (_streams)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_streams.TryGetValue(track, out input))
                throw new ArgumentException($"The music file is not pinned for this burn: {track.Path}", nameof(track));
        }
        CdMusicFile.CopyPaddedPcm(input.SafeFileHandle, track, output, cancellationToken);
    }

    public void Dispose()
    {
        lock (_streams)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (FileStream stream in _streams.Values) stream.Dispose();
            _streams.Clear();
        }
    }
}

public static class CdMusicFile
{
    /// <summary>Reads and validates a 44.1 kHz, 16-bit stereo PCM WAV file. Other formats
    /// are rejected rather than converted, so the burned audio is the file's samples.</summary>
    public static CdMusicTrackInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        FileInfo info = new(fullPath);
        if (!info.Exists) throw new FileNotFoundException($"Music file does not exist: {fullPath}", fullPath);
        using FileStream input = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long offset, length;
        try
        {
            (offset, length) = CdWaveFile.ReadAudioRange(input);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{fullPath}: {exception.Message}", exception);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"{fullPath}: the WAV header is truncated.", exception);
        }
        return new CdMusicTrackInfo(fullPath, offset, length, input.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Writes the track's little-endian PCM followed by digital silence up to
    /// <see cref="CdMusicTrackInfo.RecordedBytes"/>. The file must still match the
    /// measured identity; a replaced or edited file is reported instead of burned.</summary>
    public static void WritePaddedPcm(CdMusicTrackInfo track, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("Music output must be writable.", nameof(output));
        if (!File.Exists(track.Path)) throw new IOException($"Music file changed after it was added: {track.Path}");
        using FileStream input = new(track.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        VerifyUnchanged(track, input);
        long offset = track.PcmOffset;
        long length = track.PcmBytes;
        input.Position = offset;
        byte[] buffer = new byte[256 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException($"Music file ended early: {track.Path}");
            output.Write(buffer, 0, read);
            remaining -= read;
        }

        Array.Clear(buffer);
        long padding = track.PaddingBytes;
        while (padding > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, padding);
            output.Write(buffer, 0, count);
            padding -= count;
        }
    }

    private const int MaximumPlaylistBytes = 4 * 1024 * 1024;

    /// <summary>A playlist's lines. Playlists are small text files: UTF-8, or UTF-16/32 marked by
    /// a byte-order mark; a classic .m3u that is not valid UTF-8 is read in the system's ANSI
    /// code page (GBK on Chinese Windows). Large or binary files are refused before parsing.</summary>
    private static string[] ReadPlaylistLines(string fullPath)
    {
        if (new FileInfo(fullPath).Length > MaximumPlaylistBytes) throw NotAPlaylist(fullPath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        string text;
        try
        {
            using StreamReader reader = new(new MemoryStream(bytes),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            if (System.IO.Path.GetExtension(fullPath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{fullPath} is not valid UTF-8.");
            text = AnsiEncoding().GetString(bytes);
        }
        if (text.Contains('\0')) throw NotAPlaylist(fullPath);
        return text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    }

    internal static Encoding AnsiEncoding()
        => CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)
            ?? Encoding.Latin1;

    private static ArgumentException NotAPlaylist(string fullPath)
        => new($"{fullPath} is neither a .wav file nor a text playlist.");

    private static readonly HashSet<string> OtherAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".aif", ".aiff", ".aifc", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma",
        ".ape", ".wv", ".dsf", ".dff",
    };

    public static bool IsWavePath(string path)
        => System.IO.Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);

    /// <summary>Natural, case-insensitive file-name order, as Explorer sorts by name: "2 Intro"
    /// comes before "10 Outro". Folder sources and the app's add/drop batches use it.</summary>
    public static int CompareTrackFileNames(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        string a = System.IO.Path.GetFileName(left);
        string b = System.IO.Path.GetFileName(right);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                ReadOnlySpan<char> x = a.AsSpan(startA, i - startA).TrimStart('0');
                ReadOnlySpan<char> y = b.AsSpan(startB, j - startB).TrimStart('0');
                if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
                int number = x.SequenceCompareTo(y);
                if (number != 0) return number;
                continue;
            }

            int letter = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (letter != 0) return letter;
            i++;
            j++;
        }

        int rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    /// <summary>Opens every track for reading while denying writers, renames and deletes, and
    /// checks that each one still matches its measured identity and PCM range. The burn then
    /// reads the samples through these handles (<see cref="CdMusicPin.WritePaddedPcm"/>), so
    /// the burned samples equal the planned ones and a metadata-only change, which share
    /// modes do not prevent, cannot stop the session half-way. A changed or locked file fails
    /// here, before anything is written.</summary>
    public static CdMusicPin PinUnchanged(IEnumerable<CdMusicTrackInfo> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Dictionary<CdMusicTrackInfo, FileStream> pinned = [];
        try
        {
            foreach (CdMusicTrackInfo track in tracks)
            {
                ArgumentNullException.ThrowIfNull(track);
                if (pinned.ContainsKey(track)) continue;
                FileStream input = new(track.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                pinned.Add(track, input);
                VerifyUnchanged(track, input);
            }
            return new CdMusicPin(pinned);
        }
        catch
        {
            foreach (FileStream stream in pinned.Values) stream.Dispose();
            throw;
        }
    }

    /// <summary>Copies the track's PCM range through an already verified handle, followed by
    /// digital silence up to <see cref="CdMusicTrackInfo.RecordedBytes"/>.</summary>
    internal static void CopyPaddedPcm(Microsoft.Win32.SafeHandles.SafeFileHandle handle, CdMusicTrackInfo track,
        Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("Music output must be writable.", nameof(output));
        byte[] buffer = new byte[256 * 1024];
        long offset = track.PcmOffset;
        long remaining = track.PcmBytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)), offset);
            if (read <= 0) throw new EndOfStreamException($"Music file ended early: {track.Path}");
            output.Write(buffer, 0, read);
            offset += read;
            remaining -= read;
        }

        Array.Clear(buffer);
        long padding = track.PaddingBytes;
        while (padding > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, padding);
            output.Write(buffer, 0, count);
            padding -= count;
        }
    }

    private static void VerifyUnchanged(CdMusicTrackInfo track, FileStream input)
    {
        if (input.Length != track.FileLength || File.GetLastWriteTimeUtc(track.Path) != track.LastWriteTimeUtc)
            throw new IOException($"Music file changed after it was added: {track.Path}");
        input.Position = 0;
        (long offset, long length) = CdWaveFile.ReadAudioRange(input);
        if (offset != track.PcmOffset || length != track.PcmBytes)
            throw new IOException($"Music file changed after it was added: {track.Path}");
    }



    /// <summary>Expands a music source into ordered WAV paths: a folder (its .wav files in
    /// natural file-name order, without hidden files or AppleDouble "._" companions), a
    /// single .wav file, or a playlist text file (one path per line; blank lines and lines
    /// starting with '#' are ignored; relative paths are resolved against the playlist's
    /// folder). Other audio formats are refused with a hint to convert them.</summary>
    public static IReadOnlyList<string> ResolveSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        string fullPath = System.IO.Path.GetFullPath(source.Trim());
        if (Directory.Exists(fullPath))
        {
            // Extension test instead of a "*.wav" pattern: the pattern is case-sensitive on
            // Linux and macOS. Hidden/system files and AppleDouble "._" companions are skipped.
            string[] files = Directory.EnumerateFiles(fullPath, "*", new EnumerationOptions { IgnoreInaccessible = false })
                .Where(file => IsWavePath(file)
                    && !System.IO.Path.GetFileName(file).StartsWith("._", StringComparison.Ordinal))
                .ToArray();
            Array.Sort(files, CompareTrackFileNames);
            if (files.Length == 0) throw new ArgumentException($"Music folder contains no .wav files: {fullPath}");
            return files;
        }

        if (!File.Exists(fullPath)) throw new FileNotFoundException($"Music folder or playlist does not exist: {fullPath}", fullPath);
        if (IsWavePath(fullPath))
            return [fullPath];
        if (OtherAudioExtensions.Contains(System.IO.Path.GetExtension(fullPath)))
            throw new ArgumentException(
                $"Only 44.1 kHz 16-bit stereo PCM .wav files can be burned; convert {fullPath} first "
                + "(for example: ffmpeg -i in.flac -ar 44100 -ac 2 -sample_fmt s16 out.wav).");
        string baseDirectory = System.IO.Path.GetDirectoryName(fullPath)!;
        List<string> tracks = [];
        string[] lines = ReadPlaylistLines(fullPath);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim().Trim('"');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            try
            {
                tracks.Add(System.IO.Path.GetFullPath(line, baseDirectory));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                or PathTooLongException)
            {
                throw new ArgumentException($"{fullPath}, line {index + 1}: {exception.Message}", exception);
            }
        }

        if (tracks.Count == 0) throw new ArgumentException($"Music playlist lists no tracks: {fullPath}");
        return tracks;
    }
}
