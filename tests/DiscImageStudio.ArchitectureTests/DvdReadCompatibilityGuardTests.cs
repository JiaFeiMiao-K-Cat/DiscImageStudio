using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DvdImageSolver.Solver;

internal static class DvdReadCompatibilityGuardTests
{
    private delegate int ApplySector(Span<byte> payload, ReadOnlySpan<byte> mask, ReadOnlySpan<byte> ownership);
    private static readonly ApplySector Apply = typeof(FastDispersionImageWriter).Assembly
        .GetType("DvdImageSolver.Solver.DvdReadCompatibilityGuard", throwOnError: true)!
        .GetMethod("ApplySector", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<ApplySector>();
    private static readonly ApplySector ApplyLegacy = typeof(FastDispersionImageWriter).Assembly
        .GetType("DvdImageSolver.Solver.DvdReadCompatibilityGuard", throwOnError: true)!
        .GetMethod("ApplyLegacySector", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<ApplySector>();

    internal static void Run()
    {
        CheckBoundaries();
        CheckIndependentOracle();
        CheckLegacyReplay();
        CheckPatternWriters();
        CheckRasterPaths();
        Console.WriteLine("dvd-read-compatibility-guard: passed (independent every-16 and frozen legacy oracles, fixed phase, non-A5 values, ownership, idempotence, default-off bytes, exact-Q rejection and scalar/SIMD/reuse/parallel parity)");
    }

    private static void CheckBoundaries()
    {
        Type guard = typeof(FastDispersionImageWriter).Assembly
            .GetType("DvdImageSolver.Solver.DvdReadCompatibilityGuard", throwOnError: true)!;
        Require((string)guard.GetField("ModeName", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()! == "periodic-9b-16-v1", "mode identifies the periodic rule");

        byte[] allOwned = Enumerable.Repeat((byte)1, 2048).ToArray();
        byte[] q = Enumerable.Repeat((byte)0x92, 2048).ToArray();
        CheckCase(q, allOwned, Enumerable.Range(0, 128).Select(i => i * 16).ToArray(),
            "pure white changes exactly 128 fixed offsets including 0 and 2032");
        Array.Fill(q, (byte)0xA5);
        CheckCase(q, allOwned, [], "A5 remains unchanged at every offset");
        q[16] = q[2032] = q[2047] = 0x92;
        CheckCase(q, allOwned, [16, 2032], "isolated white bytes need no run or window; last off-grid byte stays unchanged");

        Array.Fill(q, (byte)0xA5);
        Array.Fill(q, (byte)0x92, 3, 67);
        CheckCase(q, allOwned, [16, 32, 48, 64], "an unaligned white section keeps the global phase, not its run start");

        foreach (byte preceding in new byte[] { 0x75, 0x9B, 0xA5 })
        {
            q = Enumerable.Repeat((byte)0xA5, 2048).ToArray();
            q[15] = preceding;
            q[16] = 0x92;
            CheckCase(q, allOwned, [16], "previous bridge or A5 does not suppress a periodic edit");
        }

        q = Enumerable.Repeat((byte)0xA5, 2048).ToArray();
        q[0] = q[1] = 0x00;
        q[16] = 0x10;
        q[32] = 0x75;
        q[48] = 0x9B;
        q[80] = 0xFF;
        CheckCase(q, allOwned, [0, 16, 32, 80],
            "every owned non-A5 value qualifies on-grid; existing 9B is not counted or rewritten");

        Array.Fill(q, (byte)0x92);
        byte[] ownership = new byte[2048];
        CheckCase(q, ownership, [], "unowned file bytes remain unchanged even when their Q matches white");
        ownership[1] = 1;
        ownership[16] = 2;
        ownership[32] = 255;
        CheckCase(q, ownership, [16, 32], "any nonzero ownership qualifies only at fixed offsets");

        Throws<ArgumentException>(() => Apply(new byte[2047], new byte[2048], new byte[2048]));
        Throws<ArgumentException>(() => Apply(new byte[2048], new byte[2047], new byte[2048]));
        Throws<ArgumentException>(() => Apply(new byte[2048], new byte[2048], new byte[2047]));
    }

    private static void CheckCase(byte[] q, byte[] ownership, int[] expectedIndices, string name, bool legacy = false)
    {
        ApplySector apply = legacy ? ApplyLegacy : Apply;
        byte[] mask = IndependentMask(0x30070);
        byte[] original = Xor(q, mask);
        byte[] actual = (byte[])original.Clone();
        int changed = apply(actual, mask, ownership);
        int[] observedIndices = Enumerable.Range(0, 2048).Where(i => actual[i] != original[i]).ToArray();
        Require(changed == expectedIndices.Length && observedIndices.SequenceEqual(expectedIndices), name);
        foreach (int index in observedIndices)
            Require(ownership[index] != 0 && (legacy || index % 16 == 0) && (actual[index] ^ mask[index]) == 0x9B,
                name + ": only eligible owned bytes become 9B");
        byte[] once = (byte[])actual.Clone();
        Require(apply(actual, mask, ownership) == 0 && actual.AsSpan().SequenceEqual(once), name + ": idempotent");
    }

    private static void CheckLegacyReplay()
    {
        byte[] owned = Enumerable.Repeat((byte)1, 2048).ToArray();
        byte[] q = Enumerable.Repeat((byte)0x92, 2048).ToArray();
        CheckCase(q, owned, [0], "frozen legacy white sector keeps one run-start bridge", legacy: true);
        Array.Fill(q, (byte)0xA5);
        q[2046] = q[2047] = 0x92;
        CheckCase(q, owned, [2046], "frozen legacy backward window covers the last run", legacy: true);
        q[2046] = 0xA5;
        CheckCase(q, owned, [], "frozen legacy does not change isolated white", legacy: true);
        foreach (int paletteCount in new[] { 57, 58 })
        {
            q = new byte[2048];
            Array.Fill(q, (byte)0xA5, 0, paletteCount);
            q[0] = q[1] = 0x92;
            CheckCase(q, owned, paletteCount == 58 ? [0] : [], "frozen legacy 58-of-64 threshold", legacy: true);
        }
        q = new byte[2048];
        Array.Fill(q, (byte)0xA5, 0, 64);
        Array.Clear(q, 3, 6);
        q[0] = q[1] = q[63] = q[64] = 0x92;
        CheckCase(q, owned, [0, 63], "frozen legacy windows use original Q", legacy: true);

        Random random = new(190);
        int totalChanges = 0;
        for (int trial = 0; trial < 32; trial++)
        {
            for (int index = 0; index < q.Length; index++)
            {
                int value = random.Next(100);
                q[index] = value < 48 ? (byte)0x92 : value < 94 ? (byte)0xA5 : (byte)random.Next(256);
                owned[index] = random.Next(4) == 0 ? (byte)0 : (byte)1;
            }
            byte[] mask = IndependentMask(0x30000 + (uint)trial * 16);
            byte[] actual = Xor(q, mask), expected = (byte[])actual.Clone();
            int expectedChanges = LegacyOracle(expected, mask, owned);
            int changes = ApplyLegacy(actual, mask, owned);
            Require(changes == expectedChanges && actual.AsSpan().SequenceEqual(expected),
                "frozen legacy independently matches the original bridge oracle");
            Require(ApplyLegacy(actual, mask, owned) == 0, "frozen legacy remains idempotent");
            totalChanges += changes;
        }
        Require(totalChanges > 100, "legacy oracle fixtures exercise qualifying runs");
    }

    private static int LegacyOracle(byte[] payload, byte[] mask, byte[] ownership)
    {
        byte[] q = Xor(payload, mask);
        int changes = 0;
        for (int index = 0; index + 1 < q.Length; index++)
        {
            if (q[index] != 0x92 || q[index + 1] != 0x92 || ownership[index] == 0
                || (index > 0 && q[index - 1] is 0x92 or 0x75 or 0x9B)) continue;
            bool qualifies = false;
            foreach (int begin in new[] { index, index - 63 })
            {
                if (begin < 0 || begin + 64 > q.Length) continue;
                int palette = 0;
                for (int offset = 0; offset < 64; offset++)
                    if (q[begin + offset] is 0x92 or 0xA5) palette++;
                if (palette >= 58) qualifies = true;
            }
            if (!qualifies) continue;
            payload[index] = (byte)(mask[index] ^ 0x9B);
            changes++;
        }
        return changes;
    }

    private static void CheckIndependentOracle()
    {
        Random random = new(90);
        long changes = 0;
        for (int trial = 0; trial < 96; trial++)
        {
            byte[] q = new byte[2048], ownership = new byte[2048];
            for (int index = 0; index < q.Length; index++)
            {
                int value = random.Next(100);
                q[index] = value < 30 ? (byte)0x92 : value < 60 ? (byte)0xA5 : (byte)random.Next(256);
                ownership[index] = random.Next(4) == 0 ? (byte)0 : (byte)random.Next(1, 256);
            }
            byte[] mask = IndependentMask(0x30000 + (uint)trial * 16);
            byte[] actual = Xor(q, mask);
            byte[] expected = (byte[])actual.Clone();
            int expectedCount = Oracle(expected, mask, ownership);
            int observed = Apply(actual, mask, ownership);
            Require(expectedCount == observed && actual.AsSpan().SequenceEqual(expected), "independent scalar sector oracle");
            Require(Apply(actual, mask, ownership) == 0, "random sectors are idempotent");
            changes += observed;
        }
        Require(changes > 1000, "oracle fixtures exercise many owned fixed-phase values");
    }

    private static int Oracle(byte[] payload, byte[] mask, byte[] ownership)
    {
        byte[] q = Xor(payload, mask);
        int changes = 0;
        for (int index = 0; index < q.Length; index++)
        {
            if (index % 16 != 0 || ownership[index] == 0 || q[index] is 0xA5 or 0x9B) continue;
            payload[index] = (byte)(mask[index] ^ 0x9B);
            changes++;
        }
        return changes;
    }

    private static void CheckPatternWriters()
    {
        var options = Options();
        using MemoryStream defaultOutput = new(), explicitOff = new(), enabled = new();
        var original = FastDispersionImageWriter.WriteSectorPatternStream(_ => 255, defaultOutput, options);
        FastDispersionImageWriter.WriteSectorPatternStream(_ => 255, explicitOff, options with { ReadCompatibility = false });
        var guarded = FastDispersionImageWriter.WriteSectorPatternStream(_ => 255, enabled, options with { ReadCompatibility = true, FastParallelism = 2 });
        byte[] off = defaultOutput.ToArray(), on = enabled.ToArray();
        Require(!options.ReadCompatibility && original.CompatibilityChanges == 0 && off.AsSpan().SequenceEqual(explicitOff.ToArray()),
            "default and explicit off preserve identical bytes");
        Require(guarded.CompatibilityChanges == options.FillSectors * 128L,
            "128 replacements per all-white sector, including adjacent sectors");
        for (int sector = 0; sector < options.FillSectors; sector++)
        {
            byte[] mask = IndependentMask(options.StartLba + options.PsnOffset + (uint)sector);
            for (int index = 0; index < 2048; index++)
            {
                Require((off[sector * 2048 + index] ^ mask[index]) == 0x92, "off keeps every requested white byte");
                Require((on[sector * 2048 + index] ^ mask[index]) == (index % 16 == 0 ? 0x9B : 0x92),
                    "the same periodic phase covers every sector");
            }
        }

        using MemoryStream analytic = new(), codeword = new();
        var analyticSummary = FastDispersionImageWriter.WritePatternStream((_, _) => 255, analytic, options with { ReadCompatibility = true });
        var codewordSummary = FastDispersionImageWriter.WriteCodewordPatternStream(_ => 255, codeword, options with { ReadCompatibility = true });
        Require(on.AsSpan().SequenceEqual(analytic.ToArray()) && on.AsSpan().SequenceEqual(codeword.ToArray())
            && analyticSummary.CompatibilityChanges == guarded.CompatibilityChanges && codewordSummary.CompatibilityChanges == guarded.CompatibilityChanges,
            "sector, analytic and gray codeword patterns share the same guard");
        using MemoryStream exact = new();
        int calls = 0;
        Throws<ArgumentException>(() => FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
            _ => { calls++; return 0x92; }, exact, options with { ReadCompatibility = true }));
        Require(exact.Length == 0 && calls == 0, "exact-Q rejection precedes sampling and output");
        foreach (SolverAlgorithm algorithm in new[] { SolverAlgorithm.Full, SolverAlgorithm.StateControl })
            Throws<ArgumentException>(() => FastDispersionImageWriter.WritePatternStream((_, _) => 255, exact,
                options with { ReadCompatibility = true, Algorithm = algorithm }));
    }

    private static void CheckRasterPaths()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dvd-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string opaque = WriteImage(directory, "opaque", (_, _) => 255);
            string translucent = WriteImage(directory, "alpha", (x, y) => ((x / 8 + y / 7) % 3) switch { 0 => 0, 1 => 127, _ => 255 });
            string excluded = WriteImage(directory, "excluded", (_, _) => 127);
            foreach (string path in new[] { opaque, translucent, excluded })
            foreach (bool zeroInitial in new[] { false, true })
            {
                var options = Options() with { ZeroInitialPayload = zeroInitial, ReadCompatibility = true };
                using MemoryStream scalar = new(), optimized = new(), off = new();
                var expected = FastDispersionImageWriter.WriteStream(path, scalar, options with { UseSampleReuse = false, UseSimdSampling = false });
                var actual = FastDispersionImageWriter.WriteStream(path, optimized, options with { FastParallelism = 2 });
                var unchanged = FastDispersionImageWriter.WriteStream(path, off, options with { ReadCompatibility = false });
                Require(scalar.ToArray().AsSpan().SequenceEqual(optimized.ToArray())
                    && expected.CompatibilityChanges == actual.CompatibilityChanges && expected.ControlledWords == actual.ControlledWords,
                    "scalar/SIMD/reuse/parallel output and ownership agree across reused batch slots");
                Require(actual.ControlledWords == unchanged.ControlledWords && actual.BytesWritten == unchanged.BytesWritten,
                    "compatibility changes neither ownership nor output size");
                if (path == excluded)
                    Require(actual.ControlledWords == 0 && actual.CompatibilityChanges == 0 && off.ToArray().AsSpan().SequenceEqual(optimized.ToArray()),
                        "alpha below threshold preserves all initial payload bytes");
                else
                    Require(actual.CompatibilityChanges > 0, "opaque and partial-alpha fixtures actually exercise bridges");
            }
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static MultiBlockSolveOptions Options()
        => new(810_000, 80, 0x30030, 1, 4, 842, 64, .25, false,
            new(2_297_888, 24, 58, 133.3, 37, true, 128, 128, PitchLinear: .18, PitchQuadratic: -.12, PitchCubic: .06),
            Algorithm: SolverAlgorithm.Dispersion, FastParallelism: 1);

    private static string WriteImage(string directory, string name, Func<int, int, byte> alpha)
    {
        const int size = 65;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int index = (y * size + x) * 4;
            byte gray = ((x / 11 + y / 9) & 1) == 0 ? (byte)0 : (byte)255;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = gray;
            pixels[index + 3] = alpha(x, y);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4)));
        string path = Path.Combine(directory, name + ".png");
        using var output = File.Create(path);
        encoder.Save(output);
        return path;
    }

    private static byte[] IndependentMask(uint psn)
    {
        ushort[] seeds = [0x0001, 0x5500, 0x0002, 0x2A00, 0x0004, 0x5400, 0x0008, 0x2800,
            0x0010, 0x5000, 0x0020, 0x2001, 0x0040, 0x4002, 0x0080, 0x0005];
        int state = seeds[(psn >> 4) & 15];
        byte[] mask = new byte[2048];
        for (int index = 0; index < mask.Length; index++)
        {
            mask[index] = (byte)state;
            for (int bit = 0; bit < 8; bit++)
                state = ((state << 1) | (((state >> 14) ^ (state >> 10)) & 1)) & 0x7FFF;
        }
        return mask;
    }

    private static byte[] Xor(byte[] values, byte[] mask)
        => values.Select((value, index) => (byte)(value ^ mask[index])).ToArray();

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
