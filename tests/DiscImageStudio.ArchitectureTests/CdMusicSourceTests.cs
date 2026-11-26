using System.IO;
using DiscImageStudio.Cd;
using DiscImageStudio.Services;

/// <summary>
/// Design-page music in formats other than CD WAV: a CD WAV is used in place, any other file
/// is converted in the background into the cache and the plan reads the converted copy while
/// the list keeps the original path; a changed source converts again, a decoder failure is
/// reported on the row, the layout waits while converting, and the cache trims old and
/// excess copies. The last step runs the CD-Extra window's real converter on a 48 kHz WAV
/// (Media Foundation resampler). MP3, FLAC, M4A, AAC and WMA decoding needs Windows codecs
/// and is listed in docs/CD-TEXT-hardware-test.md.
/// </summary>
internal static class CdMusicSourceTests
{
    internal static void Run()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "dis-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string originalCache = CdMusicSources.CacheDirectory;
        var originalConverter = CdMusicSources.Converter;
        CdMusicSources.CacheDirectory = Path.Combine(scratch, "cache");
        try
        {
            TestCdWaveInPlace(scratch);
            TestConversion(scratch);
            TestFailure(scratch);
            TestTrim(scratch);
            CdMusicSources.Converter = originalConverter;
            TestRealResampler(scratch);
        }
        finally
        {
            CdMusicSources.Converter = originalConverter;
            CdMusicSources.CacheDirectory = originalCache;
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine("music-sources: CD WAV in place, background conversion, source change, decode failure, plan wait, cache trim passed");
    }

    private static void TestCdWaveInPlace(string scratch)
    {
        string wav = Path.Combine(scratch, "cd.wav");
        CdExtraLayoutTests.WriteWave(wav, frames: 588 * 400, seed: 1);
        int calls = 0;
        CdMusicSources.Converter = (_, _, _, _) => { calls++; return 0; };
        Require(CdMusicSources.Resolve(wav) == Path.GetFullPath(wav), "a CD WAV is read in place");
        CdExtraTrackProbe probe = CdExtraPlanner.Probe([wav])[0];
        Require(probe.Info?.Path == Path.GetFullPath(wav), "a CD WAV's samples come from the file itself");
        Require(calls == 0 && !Directory.Exists(CdMusicSources.CacheDirectory), "a CD WAV is never converted or cached");
    }

    private static void TestConversion(string scratch)
    {
        string mp3 = Path.Combine(scratch, "song.mp3");
        File.WriteAllBytes(mp3, [1, 2, 3, 4]);
        string wav48 = Path.Combine(scratch, "hi-res.wav");
        CdExtraLayoutTests.WriteWave(wav48, frames: 480 * 400, seed: 2, sampleRate: 48_000);
        List<string> converted = [];
        CdMusicSources.Converter = (input, output, _, _) =>
        {
            lock (converted) converted.Add(input);
            CdExtraLayoutTests.WriteWave(output, frames: 588 * 450, seed: 5);
            return 450;
        };
        int changed = 0;
        Action onChanged = () => Interlocked.Increment(ref changed);
        CdMusicSources.Changed += onChanged;
        try
        {
            IReadOnlyList<CdExtraTrackProbe> pending = CdExtraPlanner.Probe([mp3, wav48]);
            Require(pending.All(probe => probe.Info is null && CdMusicSources.IsConverting(probe.Failure)),
                "non-CD files report that they are converting");
            CdExtraPlan waiting = CdExtraPlanner.Plan(Geometry(), pending, null, "CD_EXTRA", false);
            Require(waiting.Problem == CdExtraPlanProblem.UnreadableMusic
                && CdExtraPlanner.DescribeProblem(waiting).Contains("转换"), "the layout waits for the conversion");
            CdMusicSources.WhenIdleAsync().GetAwaiter().GetResult();
            Require(changed == 2, "each finished conversion is announced");
            Require(converted.Count == 2, "an MP3 and a 48 kHz WAV are both converted");

            IReadOnlyList<CdExtraTrackProbe> ready = CdExtraPlanner.Probe([mp3, wav48]);
            Require(ready.All(probe => probe.Info is not null), "converted tracks are planned");
            Require(ready[0].Path == mp3 && ready[0].Info!.Path.StartsWith(CdMusicSources.CacheDirectory, StringComparison.Ordinal),
                "the list keeps the source while the samples come from the cache");
            Require(ready[0].Info!.RecordedSectors == 450, "the plan uses the converted length");
            CdExtraPlan plan = CdExtraPlanner.Plan(Geometry(), ready, null, "CD_EXTRA", false);
            Require(plan.IsValid, "a plan with converted tracks is valid");

            string first = CdMusicSources.Resolve(mp3);
            CdMusicSources.Request([mp3]);
            Require(converted.Count == 2, "a cached conversion is reused");

            File.WriteAllBytes(mp3, [1, 2, 3, 4, 5]);
            File.SetLastWriteTimeUtc(mp3, DateTime.UtcNow.AddMinutes(1));
            Expect<CdMusicConvertingException>(() => CdMusicSources.Resolve(mp3), "a changed source converts again");
            CdMusicSources.WhenIdleAsync().GetAwaiter().GetResult();
            Require(CdMusicSources.Resolve(mp3) != first && converted.Count == 3, "the changed source gets its own copy");
        }
        finally
        {
            CdMusicSources.Changed -= onChanged;
        }
    }

    private static void TestFailure(string scratch)
    {
        string flac = Path.Combine(scratch, "broken.flac");
        File.WriteAllBytes(flac, [9, 9, 9]);
        CdMusicSources.Converter = (_, output, _, _) =>
        {
            File.WriteAllBytes(output, [0]);
            throw new InvalidDataException("no decoder");
        };
        Expect<CdMusicConvertingException>(() => CdMusicSources.Resolve(flac), "a new file starts converting");
        CdMusicSources.WhenIdleAsync().GetAwaiter().GetResult();
        CdExtraTrackProbe probe = CdExtraPlanner.Probe([flac])[0];
        Require(probe.Failure is CdMusicDecodeException && probe.Error!.Contains("no decoder"), "a decode failure is shown on the row");
        Require(!CdMusicSources.IsConverting(probe.Failure), "a failure is an error, not a pending conversion");
        CdExtraPlan plan = CdExtraPlanner.Plan(Geometry(), [probe], null, "CD_EXTRA", false);
        Require(!CdExtraPlanner.DescribeProblem(plan).Contains("转换为"), "a failed track asks to be replaced");
        Require(Directory.GetFiles(CdMusicSources.CacheDirectory, "*.part").Length == 0, "a failed conversion leaves no partial file");
        string notWave = Path.Combine(scratch, "text.wav");
        File.WriteAllText(notWave, "not a wave file");
        Expect<InvalidDataException>(() => CdMusicSources.Resolve(notWave), "a .wav that is not a WAV fails at once, as before");
                Expect<FileNotFoundException>(() => CdMusicSources.Resolve(Path.Combine(scratch, "missing.mp3")), "a missing file is reported as missing");
        CdMusicSources.ResetFailures();
    }

    private static void TestTrim(string scratch)
    {
        string directory = Path.Combine(scratch, "trim");
        Directory.CreateDirectory(directory);
        DateTime now = DateTime.UtcNow;
        string Make(string name, int bytes, TimeSpan age)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, now - age);
            return path;
        }
        string stale = Make("stale.wav", 10, TimeSpan.FromDays(31));
        string part = Make("x.wav.1.part", 10, TimeSpan.FromHours(2));
        string oldest = Make("a.wav", 400, TimeSpan.FromDays(3));
        string middle = Make("b.wav", 400, TimeSpan.FromDays(2));
        string newest = Make("c.wav", 400, TimeSpan.FromDays(1));
        string other = Make("notes.txt", 10, TimeSpan.FromDays(90));
        int removed = CdMusicSources.Trim(directory, now, keep: null, budget: 800);
        Require(!File.Exists(stale) && !File.Exists(part), "copies unused for 30 days and stale partial files are removed");
        Require(!File.Exists(oldest) && File.Exists(middle) && File.Exists(newest), "over budget, the least recently used go first");
        Require(File.Exists(other) && removed == 3, "other files are left alone");
    }

    private static void TestRealResampler(string scratch)
    {
        if (!OperatingSystem.IsWindows() || IsWine())
        {
            // Wine's Media Foundation resampler lacks IWMResamplerProps.SetHalfFilterLength.
            Console.WriteLine("music-sources: real resampler skipped (not real Windows)");
            return;
        }
        string source = Path.Combine(scratch, "real-48k.wav");
        CdExtraLayoutTests.WriteWave(source, frames: 48_000 * 5, seed: 7, sampleRate: 48_000);
        Expect<CdMusicConvertingException>(() => CdMusicSources.Resolve(source), "the 48 kHz WAV is converted");
        CdMusicSources.WhenIdleAsync().GetAwaiter().GetResult();
        CdMusicTrackInfo info = CdMusicFile.Inspect(CdMusicSources.Resolve(source));
        double seconds = info.Duration.TotalSeconds;
        Require(seconds is > 4.9 and < 5.2, $"the resampled track keeps its length ({seconds:F3} s)");
        Console.WriteLine("music-sources: 48 kHz WAV resampled by Media Foundation passed");
        Require(info.RecordedSectors * 2352 == new FileInfo(info.Path).Length - 44, "the converted WAV is sector-aligned CD audio");
    }

    private static bool IsWine()
        => System.Runtime.InteropServices.NativeLibrary.TryLoad("ntdll.dll", out IntPtr ntdll)
            && System.Runtime.InteropServices.NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);

    private static CdDiscParameters Geometry() => new(22.0, 58.0, 330_000);

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("music-sources test failed: " + message);
    }
}
