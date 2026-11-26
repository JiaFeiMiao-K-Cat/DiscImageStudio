using System.IO;
using System.Security.Cryptography;
using System.Text;
using DiscImageStudio.Cd;
using DiscImageStudio.Localization;

namespace DiscImageStudio.Services;

/// <summary>The track is still being converted to CD audio; the plan is redone when it is ready.</summary>
internal sealed class CdMusicConvertingException(string path)
    : IOException(L.T("正在转换为 CD 音频…") + " " + path)
{
    public string SourcePath { get; } = path;
}

/// <summary>Windows could not decode the track (unsupported format or damaged file).</summary>
internal sealed class CdMusicDecodeException(string path, Exception inner)
    : IOException(inner.Message, inner)
{
    public string SourcePath { get; } = path;
}

/// <summary>
/// Lets the design-page music list hold any format the standalone CD-Extra window accepts.
/// A WAV that is already CD audio (44.1 kHz, 16-bit, stereo PCM) is used in place, exactly as
/// before. Every other file is converted in the background with the CD-Extra window's
/// converter into a CD-audio WAV under <c>%LOCALAPPDATA%\DiscImageStudio\MusicCache</c>; the
/// project keeps the original path. A cached copy is named after the source's full path, size
/// and modification time, so editing the source makes a new conversion. Copies unused for 30
/// days are removed, and the oldest go first once the cache passes 2 GB.
/// </summary>
internal static class CdMusicSources
{
    /// <summary>Same list as the CD-Extra window's picker.</summary>
    internal static readonly string[] Extensions = [".wav", ".mp3", ".flac", ".m4a", ".aac", ".wma"];

    internal static readonly TimeSpan UnusedLifetime = TimeSpan.FromDays(30);
    internal const long CacheBudgetBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumTrackBytes = 450_000L * 2352;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Exception> Failures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Touched = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private static bool _trimmed;

    /// <summary>Raised on a worker thread after a conversion finished or failed.</summary>
    internal static event Action? Changed;

    internal static string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscImageStudio", "MusicCache");

    /// <summary>Test seam: the CD-Extra window's converter.</summary>
    internal static Func<string, string, long, CancellationToken, long> Converter { get; set; } = CdExtraPackage.ConvertAudio;

    internal static bool IsMusicPath(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The CD-audio WAV to read for <paramref name="source"/>: the source itself when
    /// it already is one, otherwise its finished conversion. Throws
    /// <see cref="CdMusicConvertingException"/> while the conversion runs (and starts it when
    /// nobody has), <see cref="CdMusicDecodeException"/> when it failed, and the usual file
    /// errors when the source is missing.</summary>
    internal static string Resolve(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        string full = Path.GetFullPath(source);
        if (IsCdWave(full)) return full;
        FileInfo info = new(full);
        if (!info.Exists) throw new FileNotFoundException(L.T("文件不存在"), full);
        string cached = CachePath(info);
        lock (Gate)
        {
            if (File.Exists(cached))
            {
                if (Touched.Add(cached))
                {
                    try { File.SetLastWriteTimeUtc(cached, DateTime.UtcNow); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                return cached;
            }
            if (Failures.TryGetValue(cached, out Exception? failure)) throw new CdMusicDecodeException(full, failure);
            StartLocked(full, cached);
        }
        throw new CdMusicConvertingException(full);
    }

    internal static IReadOnlyList<string> ResolveAll(IEnumerable<string> sources)
        => sources.Select(Resolve).ToArray();

    /// <summary>Starts the conversions a list still needs, without waiting.</summary>
    internal static void Request(IEnumerable<string> sources)
    {
        foreach (string source in sources)
        {
            try { Resolve(source); }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
    }

    /// <summary>Completes when every conversion started so far has finished.</summary>
    internal static Task WhenIdleAsync()
    {
        lock (Gate) return Task.WhenAll(Pending.Values.ToArray());
    }

    internal static bool IsConverting(Exception? exception) => exception is CdMusicConvertingException;

    /// <summary>Forgets failures so that a retry (for example after installing a codec) converts again.</summary>
    internal static void ResetFailures()
    {
        lock (Gate) Failures.Clear();
    }

    private static bool IsCdWave(string path)
    {
        if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            CdMusicFile.Inspect(path);
            return true;
        }
        catch (InvalidDataException) when (IsRiffWave(path))
        {
            // A WAV in another sample format (48 kHz, 24-bit, mono, float…) is converted;
            // a file that is not a WAV at all keeps the immediate error it always had.
            return false;
        }
    }

    private static bool IsRiffWave(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            return file.ReadAtLeast(header, 12, throwOnEndOfStream: false) == 12
                && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CachePath(FileInfo source)
    {
        string key = $"{source.FullName.ToUpperInvariant()}|{source.Length}|{source.LastWriteTimeUtc.Ticks}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(CacheDirectory, hash + ".wav");
    }

    private static void StartLocked(string source, string cached)
    {
        if (Pending.ContainsKey(cached)) return;
        Pending[cached] = Task.Run(async () =>
        {
            await Worker.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                if (!_trimmed)
                {
                    _trimmed = true;
                    Trim(CacheDirectory, DateTime.UtcNow, keep: cached);
                }
                string temporary = cached + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    Converter(source, temporary, MaximumTrackBytes, CancellationToken.None);
                    File.Move(temporary, cached, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception exception)
            {
                lock (Gate) Failures[cached] = exception;
            }
            finally
            {
                lock (Gate) Pending.Remove(cached);
                Worker.Release();
                try { Changed?.Invoke(); }
                catch { }
            }
        });
    }

    /// <summary>Deletes conversions unused for <see cref="UnusedLifetime"/> and leftover
    /// partial files, then the least recently used ones while the cache exceeds the budget.</summary>
    internal static int Trim(string directory, DateTime nowUtc, string? keep = null, long budget = CacheBudgetBytes)
    {
        if (!Directory.Exists(directory)) return 0;
        int removed = 0;
        List<FileInfo> files = [];
        foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles())
        {
            bool part = file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
            bool wave = file.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
            if (!part && !wave) continue;
            if (string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (part && nowUtc - file.LastWriteTimeUtc > TimeSpan.FromHours(1)
                || wave && nowUtc - file.LastWriteTimeUtc > UnusedLifetime)
            {
                if (TryDelete(file)) removed++;
            }
            else if (wave)
            {
                files.Add(file);
            }
        }

        long total = files.Sum(file => file.Length);
        foreach (FileInfo file in files.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (total <= budget) break;
            lock (Gate) { if (Touched.Contains(file.FullName)) continue; }
            long length = file.Length;
            if (TryDelete(file))
            {
                total -= length;
                removed++;
            }
        }
        return removed;
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
