using System.Buffers.Binary;
using System.Numerics;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DvdImageSolver.Encoding;
using DvdImageSolver.Solver;

namespace DvdImageSolver;

internal static class SelfTests
{
    internal static void Run()
    {
        TestTables();
        TestSy6Encoding();
        TestDataFrame();
        TestEdc();
        TestEcc();
        TestPhysicalEncoding();
        TestCodewordStateControl();
        TestDispersionPool();
        TestBinaryDispersion();
        TestDiscreteGrayscaleSampling();
        TestImageMapping();
        TestIsoOutput();
        TestCalibrationAndRange();
        Console.WriteLine("selftest: all checks passed");
    }

    private static void TestTables()
    {
        Equal((ushort)0x2009, EfmPlusTables.LookupMain(0, 1).Word, "main[0,state1]");
        Equal((byte)1, EfmPlusTables.LookupMain(0, 1).NextState, "main next state");
        Equal((ushort)0x4120, EfmPlusTables.LookupMain(0, 2).Word, "main[0,state2]");
        Equal((ushort)0x4212, EfmPlusTables.LookupMain(255, 4).Word, "main[255,state4]");
        Equal((ushort)0x0480, EfmPlusTables.Substitution(0, 1).Word, "substitution[0,state1]");

        // ECMA-267, 3rd edition, Table 4: SY0..SY7, independent of the runtime table.
        (uint Primary12, uint Secondary12, uint Primary34, uint Secondary34)[] syncVectors =
        [
            (0x12440011, 0x12040011, 0x92040011, 0x92440011),
            (0x04040011, 0x04440011, 0x84440011, 0x84040011),
            (0x10040011, 0x10440011, 0x90440011, 0x90040011),
            (0x08040011, 0x08440011, 0x82440011, 0x82040011),
            (0x20040011, 0x20440011, 0x88440011, 0x88040011),
            (0x22440011, 0x22040011, 0x89040011, 0x81040011),
            (0x24840011, 0x20840011, 0x90840011, 0x80440011),
            (0x24440011, 0x24040011, 0x88840011, 0x80840011),
        ];
        for (int category = 0; category < syncVectors.Length; category++)
        {
            var vector = syncVectors[category];
            for (byte state = 1; state <= 4; state++)
            {
                Equal(state <= 2 ? vector.Primary12 : vector.Primary34,
                    EfmPlusTables.Sync(state, category, primary: true),
                    $"ECMA-267 primary SY{category}, state {state}");
                Equal(state <= 2 ? vector.Secondary12 : vector.Secondary34,
                    EfmPlusTables.Sync(state, category, primary: false),
                    $"ECMA-267 secondary SY{category}, state {state}");
            }
        }
    }

    private static void TestSy6Encoding()
    {
        byte[] frame = new byte[EfmPlusEncoder.BytesPerSyncFrame];
        Array.Fill(frame, (byte)0x92);
        sbyte[] desired = new sbyte[frame.Length];
        Array.Fill(desired, (sbyte)-1);
        byte[] physical = new byte[EfmPlusEncoder.BitsPerSyncFrame];

        for (byte state = 3; state <= 4; state++)
        {
            bool sawSecondary = false;
            foreach (bool isLand in new[] { false, true })
            {
                foreach (int dsv in new[] { -64, 0, 64 })
                {
                    ModulationBoundary input = new(state, dsv, isLand);
                    ModulationBoundary output = new EfmPlusEncoder().EncodeSyncFrame(
                        frame, 6, input, physical);

                    uint sync = 0;
                    bool previousLevel = input.IsLand;
                    for (int bit = 0; bit < 32; bit++)
                    {
                        bool currentLevel = physical[bit] != 0;
                        sync = (sync << 1) | (currentLevel != previousLevel ? 1u : 0u);
                        previousLevel = currentLevel;
                    }

                    True(sync is 0x90840011 or 0x80440011,
                        $"encoded ECMA-267 SY6, state {state}, DSV {dsv}, land {isLand}");
                    sawSecondary |= sync == 0x80440011;
                    Equal(output, new EfmPlusWordScorer().ScoreSyncFrame(
                        frame, 6, input, desired).Boundary, "SY6 word scorer boundary");

                    // Check the secondary prefix against the standard bits, not the shared table.
                    EfmStreamPrefix secondary = EfmPlusPrefixState.Initialize(input, 6).Stream2;
                    int expectedDsv = input.Dsv;
                    bool expectedLand = input.IsLand;
                    for (int bit = 31; bit >= 0; bit--)
                    {
                        if (((0x80440011u >> bit) & 1) != 0)
                            expectedLand = !expectedLand;
                        expectedDsv += expectedLand ? 1 : -1;
                    }
                    Equal((byte)1, secondary.State, "secondary SY6 next state");
                    Equal(expectedDsv, secondary.Dsv, "secondary SY6 prefix DSV");
                    Equal(expectedLand, secondary.IsLand, "secondary SY6 prefix level");
                }
            }
            True(sawSecondary, $"encoder selects secondary SY6 in state {state}");
        }
    }

    private static void TestDataFrame()
    {
        byte[] payload = new byte[DvdDataFrameBuilder.PayloadBytes];
        byte[] frame = DvdDataFrameBuilder.BuildDataFrame(payload, 0x030000);
        SequenceEqual(new byte[] { 0x00, 0x03, 0x00, 0x00 }, frame.AsSpan(0, 4), "ID bytes");
        True(frame.AsSpan(6, 6).IndexOfAnyExcept((byte)0) < 0, "DVD-R RSV must be zero");
        True(ReedSolomon.HasZeroRemainder(frame.AsSpan(0, 6), 2), "IED codeword remainder");
        True(DvdDataFrameBuilder.HasValidEdc(frame), "EDC");

        byte[] scrambled = (byte[])frame.Clone();
        DvdDataFrameBuilder.ScrambleMainData(scrambled, 0x030000);
        True(!scrambled.AsSpan(12, 2048).SequenceEqual(frame.AsSpan(12, 2048)), "scrambler must change zero payload");
        DvdDataFrameBuilder.ScrambleMainData(scrambled, 0x030000);
        SequenceEqual(frame, scrambled, "scrambler XOR round trip");
    }

    private static void TestEdc()
    {
        // ECMA-267 16.4: remainder of the input polynomial multiplied by x^32.
        Equal(0x80000011u, DvdDataFrameBuilder.ComputeEdc([0x01]), "EDC x^32 remainder");
        Equal(0x80000F0Fu, DvdDataFrameBuilder.ComputeEdc([0x80]), "EDC MSB-first order");
        Equal(0xDE5E214Eu, DvdDataFrameBuilder.ComputeEdc([0x12, 0x34, 0x56, 0x78]),
            "EDC multi-byte vector");

        uint[] expectedEdcs = [0x02AEBAF1, 0xB219C5AE, 0x086E4847];
        for (int pattern = 0; pattern < expectedEdcs.Length; pattern++)
        {
            byte[] payload = new byte[DvdDataFrameBuilder.PayloadBytes];
            for (int index = 0; index < payload.Length; index++)
                payload[index] = pattern switch { 1 => (byte)index, 2 => (byte)0xFF, _ => (byte)0 };

            byte[] frame = DvdDataFrameBuilder.BuildDataFrame(payload, 0x030000);
            Equal(expectedEdcs[pattern], BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(2060)),
                $"EDC complete frame vector {pattern}");
            Equal(0u, PolynomialRemainder(frame), $"EDC full-frame polynomial remainder {pattern}");
            True(DvdDataFrameBuilder.HasValidEdc(frame), $"EDC valid frame {pattern}");
            frame[12] ^= 1;
            True(!DvdDataFrameBuilder.HasValidEdc(frame), $"EDC rejects changed payload {pattern}");
        }

        // Literal long division, independent of the production feedback-register algorithm.
        static uint PolynomialRemainder(ReadOnlySpan<byte> frame)
        {
            BigInteger polynomial = new(frame, isUnsigned: true, isBigEndian: true);
            BigInteger generator = new(0x180000011UL);
            while (polynomial.GetBitLength() > 32)
                polynomial ^= generator << checked((int)(polynomial.GetBitLength() - 33));
            return (uint)polynomial;
        }
    }

    private static void TestEcc()
    {
        byte[] payloads = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        byte[] ecc = DvdEccBlockEncoder.BuildEccBytes(payloads, 0x030000);
        for (int row = 0; row < 208; row++)
        {
            True(ReedSolomon.HasZeroRemainder(ecc.AsSpan(row * 182, 182), 10), $"PI row {row}");
        }

        byte[] column = new byte[208];
        for (int columnIndex = 0; columnIndex < 172; columnIndex++)
        {
            for (int row = 0; row < 208; row++)
            {
                column[row] = ecc[row * 182 + columnIndex];
            }

            True(ReedSolomon.HasZeroRemainder(column, 16), $"PO column {columnIndex}");
        }

        byte[] interleaved = DvdEccBlockEncoder.InterleaveRecordingFrames(ecc);
        Equal(ecc.Length, interleaved.Length, "recording frame size");

        byte[] updatedPayloads = (byte[])payloads.Clone();
        updatedPayloads[17] = 0x5A;
        updatedPayloads[(7 * DvdEccBlockEncoder.PayloadBytesPerSector) + 991] = 0xC3;
        updatedPayloads[^1] = 0x7E;
        byte[] incrementallyUpdated = DvdEccBlockEncoder.UpdateRecordingFrames(
            payloads,
            updatedPayloads,
            interleaved,
            0x030000);
        byte[] fullyRebuilt = DvdEccBlockEncoder.BuildRecordingFrames(
            updatedPayloads,
            0x030000);
        SequenceEqual(fullyRebuilt, incrementallyUpdated, "incremental ECC recording frames");

        Random random = new(731);
        byte[] randomPayloads = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        random.NextBytes(randomPayloads);
        byte[] randomFrames = DvdEccBlockEncoder.BuildRecordingFrames(randomPayloads, 0x030000);
        byte[] mutatedPayloads = (byte[])randomPayloads.Clone();
        for (int mutation = 0; mutation < 64; mutation++)
        {
            int index = random.Next(mutatedPayloads.Length);
            mutatedPayloads[index] ^= checked((byte)random.Next(1, 256));
        }

        incrementallyUpdated = DvdEccBlockEncoder.UpdateRecordingFrames(
            randomPayloads,
            mutatedPayloads,
            randomFrames,
            0x030000);
        fullyRebuilt = DvdEccBlockEncoder.BuildRecordingFrames(mutatedPayloads, 0x030000);
        SequenceEqual(fullyRebuilt, incrementallyUpdated, "incremental ECC random mutations");
    }

    private static void TestPhysicalEncoding()
    {
        byte[] payloads = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        DvdEccBlockEncoder encoder = new();
        ModulationBoundary initial = new(1, 0, IsLand: true);
        EncodedEccBlock first = encoder.Encode(payloads, 0, 0x30000, initial);
        EncodedEccBlock second = encoder.Encode(payloads, 0, 0x30000, initial);
        Equal(DvdEccBlockEncoder.ChannelBitsPerBlock, first.ChannelLevels.Length, "channel bit count");
        Equal((uint)0x30000, first.FirstPhysicalSectorNumber, "first PSN");
        True(first.ChannelLevels.AsSpan().IndexOfAnyExcept((byte)0, (byte)1) < 0, "channel levels are binary");
        SequenceEqual(first.ChannelLevels, second.ChannelLevels, "deterministic modulation");
        Equal(first.FinalBoundary, second.FinalBoundary, "deterministic final boundary");
        byte[] recordingFrames = DvdEccBlockEncoder.BuildRecordingFrames(payloads, 0x30000);
        Equal(
            first.FinalBoundary,
            DvdEccBlockEncoder.ReplayRecordingFrameBoundary(recordingFrames, initial),
            "word replay final boundary");
        ValidateDataRll(first.ChannelLevels, initial.IsLand);
    }

    private static void TestDispersionPool()
    {
        Equal(
            (byte)0xA5,
            DispersionPoolSolver.BlackScrambledByte,
            "dispersion physical black byte polarity");
        Equal(
            (byte)0x92,
            DispersionPoolSolver.WhiteScrambledByte,
            "dispersion physical white byte polarity");
        const uint firstPsn = 0x30000;
        RasterImage black = RasterImage.CreateForTest(8, 8, 0, 0, 0, 255);
        ImageConstraint mapped = ImageTargetMapper.Map(
            black,
            0,
            new ImageMappingOptions(
                TotalSectors: 16,
                InnerRadiusMm: 24,
                OuterRadiusMm: 24.1,
                ChannelBitLengthNm: 133.33,
                StartAngleDegrees: 0,
                Clockwise: false,
                LuminanceThreshold: 128,
                AlphaThreshold: 1,
                SampleEveryChannelBits: 256));
        sbyte[] targets = new sbyte[DvdEccBlockEncoder.ChannelBitsPerBlock];
        Array.Fill(targets, ImageConstraint.Unconstrained);
        for (int payloadIndex = 0;
            payloadIndex < DvdEccBlockEncoder.PayloadBytesPerBlock;
            payloadIndex++)
        {
            int channelOffset = DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex);
            targets.AsSpan(channelOffset, 16).Fill((sbyte)(payloadIndex & 1));
        }

        ImageConstraint constraint = ImageConstraint.Create(targets, mapped.ImageMapping!);
        byte[] initialPayload = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        new Random(9025).NextBytes(initialPayload);
        SolverOptions options = new(
            Iterations: 1,
            MutationBytes: 4,
            RandomSeed: 1,
            InitialTemperature: 64,
            FinalTemperature: 0.25,
            Algorithm: SolverAlgorithm.Dispersion);
        ModulationBoundary boundary = new(1, 0, IsLand: true);
        SolverResult result = SolverEngine.Solve(
            constraint,
            initialPayload,
            0,
            firstPsn,
            boundary,
            options);
        byte[] frames = DvdEccBlockEncoder.BuildRecordingFrames(result.Payloads, firstPsn);
        for (int payloadIndex = 0;
            payloadIndex < DvdEccBlockEncoder.PayloadBytesPerBlock;
            payloadIndex++)
        {
            byte expected = (payloadIndex & 1) == 0
                ? DispersionPoolSolver.BlackScrambledByte
                : DispersionPoolSolver.WhiteScrambledByte;
            Equal(
                expected,
                frames[DvdEccBlockEncoder.PayloadRecordingFrameIndex(payloadIndex)],
                $"dispersion scrambled payload {payloadIndex}");
        }

        EncodedEccBlock exact = new DvdEccBlockEncoder().Encode(
            result.Payloads,
            0,
            firstPsn,
            boundary);
        Equal(exact.FinalBoundary, result.Encoded.FinalBoundary, "dispersion exact final boundary");
        Equal(0, result.Encoded.ChannelLevels.Length, "dispersion avoids channel-bit materialization");
        Equal(0, result.Score, "dispersion transition class error");
        Equal(
            DvdEccBlockEncoder.PayloadBytesPerBlock,
            result.ScoreMaximum,
            "dispersion controlled word count");
        Equal(
            "payload-transition-class-error",
            result.ScoreMetric,
            "dispersion score metric");
    }

    private static void TestBinaryDispersion()
    {
        byte[] inputLevels = [0, 127, 128, 255];
        byte[] expectedBytes = [0xA5, 0xA5, 0x92, 0x92];
        byte[] displayLevels = [0, 255];
        int[] transitionCounts = [5, 2];
        for (int index = 0; index < inputLevels.Length; index++)
        {
            Equal(expectedBytes[index], DvdGrayscalePalette.GetScrambledByte(inputLevels[index]),
                $"DVD grayscale threshold {inputLevels[index]}");
        }

        for (int level = 0; level < displayLevels.Length; level++)
        {
            byte value = expectedBytes[level * 2];
            True(DvdGrayscalePalette.TryGetLuminance(value, out byte luminance), "DVD palette readback");
            Equal(displayLevels[level], luminance, "DVD nominal readback level");
            for (byte state = 1; state <= 4; state++)
            {
                Equal(transitionCounts[level], BitOperations.PopCount(EfmPlusTables.LookupMain(value, state).Word),
                    $"DVD tone 0x{value:X2} has stable transitions in state {state}");
            }
        }

        True(!DvdGrayscalePalette.TryGetLuminance(0, out _), "unknown DVD byte is not classified");
        True(DvdGrayscalePalette.TryGetLuminance(0x66, out byte legacyLuminance), "legacy Q66 is recognized");
        Equal((byte)0, legacyLuminance, "legacy Q66 readback matches observed black appearance");
        True(DvdGrayscalePalette.TryGetLuminance(0xB9, out legacyLuminance), "legacy QB9 is recognized");
        Equal((byte)85, legacyLuminance, "legacy QB9 readback remains nominal dark gray");
        SequenceEqual(new byte[] { 0xA5, 0x92 }, DvdGrayscalePalette.ScrambledBytes,
            "DVD generation palette excludes rejected candidates");
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-grayscale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "gray-source.png");
            WriteTestPng(sourcePath, grayscale: true);
            ImageMappingOptions mapping = new(
                TotalSectors: 64, InnerRadiusMm: 24, OuterRadiusMm: 24.1,
                ChannelBitLengthNm: 133.33, StartAngleDegrees: 17, Clockwise: true,
                LuminanceThreshold: 20, AlphaThreshold: 128, SampleEveryChannelBits: 256);
            MultiBlockSolveOptions fastOptions = new(
                StartLba: 16, FillSectors: 16, PsnOffset: 0x30000,
                IterationsPerBlock: 1, MutationBytes: 4, RandomSeed: 804,
                InitialTemperature: 64, FinalTemperature: 0.25, ZeroInitialPayload: false,
                ImageMapping: mapping, Algorithm: SolverAlgorithm.Dispersion, FastParallelism: 2);
            ImageConstraint constraint = ImageTargetMapper.Map(sourcePath, fastOptions.StartLba, mapping);
            byte[] initialPayload = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
            new Random(fastOptions.RandomSeed).NextBytes(initialPayload);
            ModulationBoundary initialBoundary = new(1, 0, IsLand: true);
            SolverResult exact = SolverEngine.Solve(
                constraint, initialPayload, fastOptions.StartLba, fastOptions.PsnOffset, initialBoundary,
                new SolverOptions(1, 4, 804, 64, 0.25, Algorithm: SolverAlgorithm.Dispersion));
            string isoPath = Path.Combine(directory, "gray.iso");
            FastDispersionImageWriter.Write(sourcePath, isoPath, fastOptions, progress: null);
            SequenceEqual(exact.Payloads, File.ReadAllBytes(isoPath).AsSpan(initialPayload.Length),
                "binary fast file matches exact payload at nonzero LBA");
            using MemoryStream imageStream = new();
            FastDispersionImageWriter.WriteStream(sourcePath, imageStream, fastOptions);
            SequenceEqual(exact.Payloads, imageStream.ToArray(), "binary streaming matches exact payload");

            HashSet<byte> observedBytes = [];
            int preserved = 0;
            for (int payloadIndex = 0; payloadIndex < initialPayload.Length; payloadIndex++)
            {
                int wordIndex = DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex) / 16;
                sbyte target = constraint.ChannelWordGrayscaleTargets[wordIndex];
                True(constraint.ChannelWordTargets[wordIndex] is -1 or 0 or 16,
                    "dispersion targets leave full/state-control solver targets unchanged");
                if (target < 0)
                {
                    Equal(initialPayload[payloadIndex], exact.Payloads[payloadIndex],
                        "transparent payload remains unchanged");
                    preserved++;
                    continue;
                }

                int sector = payloadIndex / DvdEccBlockEncoder.PayloadBytesPerSector;
                int byteIndex = payloadIndex % DvdEccBlockEncoder.PayloadBytesPerSector;
                byte q = (byte)(exact.Payloads[payloadIndex] ^ DvdDataFrameBuilder.PayloadScramblerMask(
                    fastOptions.StartLba + fastOptions.PsnOffset + (uint)sector)[byteIndex]);
                Equal(expectedBytes[target * 2], q, "binary payload reverse scrambling");
                observedBytes.Add(q);
            }

            Equal(2, observedBytes.Count, "both DVD tones generated from image");
            True(!observedBytes.Contains(0x66) && !observedBytes.Contains(0xB9),
                "new constrained payload never uses Q66 or QB9");
            True(preserved > 0, "binary image includes unconstrained alpha samples");

            using MemoryStream codewordStream = new();
            FastDispersionImageWriter.WriteCodewordPatternStream(
                sample => inputLevels[(int)(sample.GlobalCodewordIndex % (uint)inputLevels.Length)], codewordStream, fastOptions);
            byte[] codewordPayload = codewordStream.ToArray();
            EncodedEccBlock encoded = new DvdEccBlockEncoder().Encode(
                codewordPayload, fastOptions.StartLba, fastOptions.PsnOffset, initialBoundary);
            ValidateDataRll(encoded.ChannelLevels, initialBoundary.IsLand);
            for (int payloadIndex = 0; payloadIndex < codewordPayload.Length; payloadIndex++)
            {
                int channelOffset = DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex);
                ulong globalWord = (((ulong)fastOptions.StartLba * DvdEccBlockEncoder.ChannelBitsPerSector)
                    + (ulong)channelOffset) / 16;
                int expectedIndex = (int)(globalWord % (uint)inputLevels.Length);
                int sector = payloadIndex / DvdEccBlockEncoder.PayloadBytesPerSector;
                int byteIndex = payloadIndex % DvdEccBlockEncoder.PayloadBytesPerSector;
                byte q = (byte)(codewordPayload[payloadIndex] ^ DvdDataFrameBuilder.PayloadScramblerMask(
                    fastOptions.StartLba + fastOptions.PsnOffset + (uint)sector)[byteIndex]);
                Equal(expectedBytes[expectedIndex], q, "codeword stream quantizes boundary values");
                int transitions = 0;
                for (int bit = channelOffset; bit < channelOffset + 16; bit++)
                {
                    if (encoded.ChannelLevels[bit] != encoded.ChannelLevels[bit - 1])
                    {
                        transitions++;
                    }
                }

                Equal(transitionCounts[expectedIndex / 2], transitions, "binary ECC/SYNC replay transition count");
            }

            MultiBlockSolveOptions analyticOptions = fastOptions with { StartLba = 0 };
            for (int level = 0; level < displayLevels.Length; level++)
            {
                byte input = displayLevels[level];
                using MemoryStream analyticStream = new();
                FastDispersionImageWriter.WritePatternStream((_, _) => input, analyticStream, analyticOptions);
                byte[] payload = analyticStream.ToArray();
                for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
                {
                    Span<byte> scrambled = payload.AsSpan(
                        sector * DvdEccBlockEncoder.PayloadBytesPerSector,
                        DvdEccBlockEncoder.PayloadBytesPerSector);
                    DvdDataFrameBuilder.TransformPayloadScrambling(scrambled, 0x30000 + (uint)sector);
                    True(scrambled.IndexOfAnyExcept(expectedBytes[level * 2]) < 0,
                        "analytic stream generates the selected DVD tone");
                }

                string constantIsoPath = Path.Combine(directory, $"tone-{level}.iso");
                File.WriteAllBytes(constantIsoPath, analyticStream.ToArray());
                string readbackPath = Path.Combine(directory, $"tone-{level}.png");
                IsoReadbackSummary readback = IsoReadbackRenderer.Render(
                    constantIsoPath, readbackPath,
                    new IsoReadbackOptions(64, 0, 16, 0x30000, 24, 24.1, 133.33,
                        Clockwise: true, PreviewSize: 128, SamplesPerSector: 64));
                Equal(readback.TotalSamples, readback.ClassifiedSamples,
                    "ISO readback recognizes every sample of each DVD tone");
                True(ContainsOpaqueGray(readbackPath, displayLevels[level]),
                    "ISO readback renders each nominal DVD tone");
            }

            string ditheredPath = Path.Combine(directory, "dithered-source.png");
            WriteTestPng(ditheredPath, binaryDither: true);
            ImageConstraint ditheredConstraint = ImageTargetMapper.Map(ditheredPath, fastOptions.StartLba, mapping);
            SolverResult ditheredExact = SolverEngine.Solve(
                ditheredConstraint, initialPayload, fastOptions.StartLba, fastOptions.PsnOffset, initialBoundary,
                new SolverOptions(1, 4, 804, 64, 0.25, Algorithm: SolverAlgorithm.Dispersion));
            using MemoryStream ditheredStream = new();
            FastDispersionImageWriter.WriteStream(ditheredPath, ditheredStream, fastOptions);
            SequenceEqual(ditheredExact.Payloads, ditheredStream.ToArray(),
                "discrete dither exact and streaming paths select identical source pixels");
            string ditheredIsoPath = Path.Combine(directory, "dithered.iso");
            FastDispersionImageWriter.Write(ditheredPath, ditheredIsoPath, fastOptions, progress: null);
            SequenceEqual(ditheredExact.Payloads, File.ReadAllBytes(ditheredIsoPath).AsSpan(initialPayload.Length),
                "discrete dither fast file and exact payload agree");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestDiscreteGrayscaleSampling()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-discrete-sampling-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string discretePath = Path.Combine(directory, "discrete.png");
            WritePixelColumns(discretePath, [0, 255, 0]);
            RasterImage discrete = RasterImage.Load(discretePath);
            True(discrete.HasBinaryGrayscalePalette, "loaded opaque black/white PNG is detected");
            Equal(new PixelSample(0, 255), discrete.SampleDisc(-0.6, 0, 1, preserveDiscreteGrayscale: true),
                "dither black pixel is preserved without interpolation");
            Equal(new PixelSample(255, 255), discrete.SampleDisc(-0.4, 0, 1, preserveDiscreteGrayscale: true),
                "dither white pixel is preserved without interpolation");
            Equal(new PixelSample(153, 255), discrete.SampleDisc(-0.4, 0, 1),
                "binary solver retains existing bilinear sampling");
            Equal((byte)255, BinaryGrayscale.Quantize(153),
                "continuous gray is thresholded to one of the two production tones");

            string continuousPath = Path.Combine(directory, "continuous.png");
            WritePixelColumns(continuousPath, [0, 128, 255]);
            RasterImage continuous = RasterImage.Load(continuousPath);
            True(!continuous.HasBinaryGrayscalePalette, "continuous grayscale is not mistaken for dither");
            Equal(new PixelSample(77, 255), continuous.SampleDisc(-0.4, 0, 1, preserveDiscreteGrayscale: true),
                "continuous image retains bilinear sampling");

            string alphaPath = Path.Combine(directory, "alpha.png");
            WritePixelColumns(alphaPath, [0, 255, 0], firstAlpha: 128);
            RasterImage alpha = RasterImage.Load(alphaPath);
            True(!alpha.HasBinaryGrayscalePalette, "partial alpha disables discrete sampling");
            Equal(new PixelSample(145, 179), alpha.SampleDisc(-0.6, 0, 1, preserveDiscreteGrayscale: true),
                "transparent image retains alpha-weighted bilinear sampling");
            RasterImage color = RasterImage.CreateForTest(3, 3, 0, 255, 0, 255);
            True(!color.HasBinaryGrayscalePalette, "color images are not treated as grayscale dither");

            void WritePixelColumns(string path, byte[] values, byte firstAlpha = 255)
            {
                byte[] pixels = new byte[3 * 3 * 4];
                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        int offset = (y * 3 + x) * 4;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = values[x];
                        pixels[offset + 3] = x == 0 ? firstAlpha : (byte)255;
                    }
                }

                RasterCodec.SavePng(path, 3, 3, pixels, grayscale: false);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestCodewordStateControl()
    {
        const uint firstPsn = 0x30000;
        foreach ((byte state, bool initialIsLand, EfmPlusBinaryBytePair pair) in EfmPlusBinaryBytePairs.AtZeroDsv)
        {
            True(pair.BlackByte != pair.WhiteByte, $"binary pair bytes differ for state {state}/{initialIsLand}");
            True(
                pair.BlackWorstLandCount < pair.WhiteWorstLandCount,
                $"binary pair preserves black/white order for state {state}/{initialIsLand}");
        }

        EfmPlusBinaryBytePair negativeDsvPair = EfmPlusBinaryBytePairs.Get(1, initialIsLand: false, -32);
        EfmPlusBinaryBytePair positiveDsvPair = EfmPlusBinaryBytePairs.Get(1, initialIsLand: false, 32);
        True(
            negativeDsvPair.BlackByte != positiveDsvPair.BlackByte,
            "binary pair reacts to DSV direction");
        Equal(
            1000,
            EfmPlusBinaryBytePairs.Get(2, initialIsLand: true, 1000).ReferenceDsv,
            "binary pair uses exact out-of-cache DSV");

        Random random = new(7819);
        for (int test = 0; test < 64; test++)
        {
            byte[] frame = new byte[EfmPlusEncoder.BytesPerSyncFrame];
            random.NextBytes(frame);
            sbyte[] desired = new sbyte[EfmPlusEncoder.BytesPerSyncFrame];
            for (int index = 0; index < desired.Length; index++)
            {
                desired[index] = random.NextDouble() < 0.7
                    ? (sbyte)-1
                    : checked((sbyte)random.Next(17));
            }

            ModulationBoundary input = new(
                checked((byte)random.Next(1, 5)),
                random.Next(-128, 129),
                random.Next(2) != 0);
            int syncCategory = random.Next(8);
            byte[] physical = new byte[EfmPlusEncoder.BitsPerSyncFrame];
            ModulationBoundary exactBoundary = new EfmPlusEncoder().EncodeSyncFrame(
                frame,
                syncCategory,
                input,
                physical);
            int exactBinaryErrors = 0;
            int exactGrayError = 0;
            for (int byteIndex = 0; byteIndex < desired.Length; byteIndex++)
            {
                if (desired[byteIndex] < 0)
                {
                    continue;
                }

                int landCount = 0;
                int channelOffset = 32 + (byteIndex * 16);
                for (int bit = 0; bit < 16; bit++)
                {
                    landCount += physical[channelOffset + bit];
                }

                if ((desired[byteIndex] < 8 && landCount >= 8)
                    || (desired[byteIndex] > 8 && landCount <= 8))
                {
                    exactBinaryErrors++;
                }

                exactGrayError += Math.Abs(landCount - desired[byteIndex]);
            }

            EfmPlusWordScore wordScore = new EfmPlusWordScorer().ScoreSyncFrame(
                frame,
                syncCategory,
                input,
                desired);
            Equal(exactBoundary, wordScore.Boundary, $"word scorer SYNC/DSV boundary {test}");
            Equal(exactBinaryErrors, wordScore.BinaryErrors, $"word scorer majority error {test}");
            Equal(exactGrayError, wordScore.GrayError, $"word scorer gray error {test}");

            sbyte firstSyncTarget = checked((sbyte)(random.Next(2) * 16));
            sbyte secondSyncTarget = checked((sbyte)(random.Next(2) * 16));
            int syncBinaryErrors = 0;
            int syncGrayError = 0;
            for (int syncWord = 0; syncWord < 2; syncWord++)
            {
                sbyte syncTarget = syncWord == 0 ? firstSyncTarget : secondSyncTarget;
                int landCount = CountLand(physical.AsSpan(syncWord * 16, 16));
                if ((syncTarget < 8 && landCount >= 8)
                    || (syncTarget > 8 && landCount <= 8))
                {
                    syncBinaryErrors++;
                }

                syncGrayError += Math.Abs(landCount - syncTarget);
            }

            EfmPlusWordScore syncScore = new EfmPlusWordScorer().ScoreSyncFrame(
                frame,
                syncCategory,
                input,
                desired,
                firstSyncTarget,
                secondSyncTarget);
            Equal(exactBoundary, syncScore.Boundary, $"word scorer observed SYNC boundary {test}");
            Equal(
                exactBinaryErrors + syncBinaryErrors,
                syncScore.BinaryErrors,
                $"word scorer observed SYNC majority {test}");
            Equal(
                exactGrayError + syncGrayError,
                syncScore.GrayError,
                $"word scorer observed SYNC gray {test}");

            EfmPlusPrefixState prefix = EfmPlusPrefixState.Initialize(input, syncCategory);
            for (int index = 0; index < frame.Length; index++)
            {
                prefix = prefix.Advance(frame[index], desired[index]);
            }

            EfmStreamPrefix selected = prefix.Selected;
            if (selected.Dsv is >= -64 and <= 63)
            {
                Equal(
                    new ModulationBoundary(selected.State, selected.Dsv, selected.IsLand),
                    exactBoundary,
                    $"prefix state boundary {test}");
                Equal(selected.GrayError, exactGrayError, $"prefix state gray error {test}");
                Equal(selected.BinaryErrors, exactBinaryErrors, $"prefix state majority error {test}");
            }
        }

        RasterImage black = RasterImage.CreateForTest(8, 8, 0, 0, 0, 255);
        ImageConstraint mappedBlack = ImageTargetMapper.Map(
            black,
            0,
            new ImageMappingOptions(
                TotalSectors: 16,
                InnerRadiusMm: 24,
                OuterRadiusMm: 24.1,
                ChannelBitLengthNm: 133.33,
                StartAngleDegrees: 0,
                Clockwise: false,
                LuminanceThreshold: 128,
                AlphaThreshold: 1,
                SampleEveryChannelBits: 16));
        Equal(
            DvdEccBlockEncoder.ChannelBitsPerBlock / 16,
            mappedBlack.ChannelWordTargets.ToArray().Count(target => target >= 0),
            "opaque image observes every data and SYNC channel word");
        sbyte[] alternatingTargets = new sbyte[DvdEccBlockEncoder.ChannelBitsPerBlock];
        Array.Fill(alternatingTargets, ImageConstraint.Unconstrained);
        for (int payloadIndex = 0;
            payloadIndex < DvdEccBlockEncoder.PayloadBytesPerBlock;
            payloadIndex += 16)
        {
            int channelOffset = DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex);
            alternatingTargets.AsSpan(channelOffset, 16).Fill((sbyte)((payloadIndex / 16) & 1));
        }

        ImageConstraint constraint = ImageConstraint.Create(
            alternatingTargets,
            mappedBlack.ImageMapping!);
        sbyte[] tiedImageTargets = new sbyte[DvdEccBlockEncoder.ChannelBitsPerBlock];
        Array.Fill(tiedImageTargets, ImageConstraint.Unconstrained);
        int tiedChannelOffset = DvdEccBlockEncoder.PayloadDirectChannelOffset(100);
        tiedImageTargets.AsSpan(tiedChannelOffset, 8).Fill(0);
        tiedImageTargets.AsSpan(tiedChannelOffset + 8, 8).Fill(1);
        CodewordGrayTarget tiedImageTarget = CodewordGrayTarget.Create(
            ImageConstraint.Create(tiedImageTargets, mappedBlack.ImageMapping!),
            bridgeBytes: 0);
        Equal(
            (sbyte)16,
            tiedImageTarget.DesiredAtRecordingIndex(
                DvdEccBlockEncoder.PayloadRecordingFrameIndex(100)),
            "binary image tie uses center sample");
        byte[] initialPayload = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        random.NextBytes(initialPayload);
        SolverOptions baseOptions = new(
            Iterations: 3,
            MutationBytes: 4,
            RandomSeed: 31,
            InitialTemperature: 64,
            FinalTemperature: 0.25,
            Algorithm: SolverAlgorithm.StateControl,
            BridgeBytes: 2,
            // Preserve the quality threshold for sparse alternating targets after ECC propagation.
            BeamWidth: 16,
            RetryBeamWidth: 0,
            RetryFrameFraction: 0,
            BatchFrames: 8,
            BatchCandidates: 4,
            SearchLanes: 2);
        SolverOptions options = baseOptions with
        {
            RetryBeamWidth = 24,
            RetryFrameFraction = 0.25,
        };
        ModulationBoundary boundary = new(1, 0, IsLand: true);
        CodewordGrayTarget grayTarget = CodewordGrayTarget.Create(constraint, options.BridgeBytes);
        CodewordBlockEvaluation initialEvaluation = grayTarget.Evaluate(
            DvdEccBlockEncoder.BuildRecordingFrames(initialPayload, firstPsn),
            boundary);
        SolverResult result = SolverEngine.Solve(
            constraint,
            initialPayload,
            0,
            firstPsn,
            boundary,
            options);
        int controlledScore = result.ControlledScore
            ?? throw new InvalidOperationException("Missing controlled majority score.");
        int controlledGrayError = result.ControlledGrayScore
            ?? throw new InvalidOperationException("Missing controlled gray score.");
        int controlledMaximum = result.ControlledScoreMaximum
            ?? throw new InvalidOperationException("Missing controlled majority maximum.");
        True(
            controlledScore < initialEvaluation.BinaryErrors
                || (controlledScore == initialEvaluation.BinaryErrors
                    && controlledGrayError <= initialEvaluation.GrayError),
            "batch search never worsens the exact block score");
        Equal(0, result.Encoded.ChannelLevels.Length, "state-control avoids final bit materialization");
        Equal(
            "all-channel-word-majority-error",
            result.ScoreMetric,
            "state-control score metric");
        Equal(
            "all-channel-word-gray-error",
            result.GrayScoreMetric ?? string.Empty,
            "state-control gray metric");
        int resultGrayError = result.GrayScore
            ?? throw new InvalidOperationException("Missing state-control gray score.");
        True(
            controlledScore < initialEvaluation.BinaryErrors
                || (controlledScore == initialEvaluation.BinaryErrors
                    && controlledGrayError < initialEvaluation.GrayError),
            "state-control improves majority/gray error lexicographically");
        True(
            (long)controlledScore * 2 < controlledMaximum,
            "ECC-closed batch search beats random controlled majority");
        EncodedEccBlock exact = new DvdEccBlockEncoder().Encode(result.Payloads, 0, firstPsn, boundary);
        Equal(exact.FinalBoundary, result.Encoded.FinalBoundary, "state-control exact final boundary");
        byte[] resultFrames = DvdEccBlockEncoder.BuildRecordingFrames(result.Payloads, firstPsn);
        ModulationBoundary[] frameInputs = new ModulationBoundary[DvdEccBlockEncoder.SyncFrameCount];
        CodewordFrameEvaluation[] frameEvaluations =
            new CodewordFrameEvaluation[DvdEccBlockEncoder.SyncFrameCount];
        CodewordBlockEvaluation evaluated = grayTarget.Evaluate(
            resultFrames,
            boundary,
            frameInputs,
            frameEvaluations);
        Equal(evaluated.BinaryErrors, controlledScore, "state-control controlled majority score");
        Equal(evaluated.GrayError, controlledGrayError, "state-control controlled gray score");
        Equal(
            evaluated.BinaryErrors,
            frameEvaluations.Sum(frame => frame.BinaryErrors),
            "adaptive frame majority scores sum to block score");
        Equal(
            evaluated.GrayError,
            frameEvaluations.Sum(frame => frame.GrayError),
            "adaptive frame gray scores sum to block score");
        Equal(evaluated.FinalBoundary, result.Encoded.FinalBoundary, "state-control word boundary");
        Equal(
            grayTarget.ScoredWords,
            result.ControlledScoreMaximum ?? -1,
            "state-control controlled score maximum");
        Equal(
            grayTarget.MaximumGrayError,
            result.ControlledGrayScoreMaximum ?? -1,
            "state-control controlled gray maximum");

        CodewordBlockEvaluation observed = grayTarget.EvaluateObserved(resultFrames, boundary);
        Equal(observed.BinaryErrors, result.Score, "state-control all-word majority score");
        Equal(observed.GrayError, resultGrayError, "state-control all-word gray score");
        Equal(grayTarget.ObservedWords, result.ScoreMaximum, "state-control all-word maximum");
        Equal(
            grayTarget.MaximumObservedGrayError,
            result.GrayScoreMaximum ?? -1,
            "state-control all-word gray maximum");
        int exactObservedBinaryErrors = 0;
        int exactObservedGrayError = 0;
        ReadOnlySpan<sbyte> observationTargets = constraint.ChannelWordTargets;
        for (int wordIndex = 0; wordIndex < observationTargets.Length; wordIndex++)
        {
            sbyte wordTarget = observationTargets[wordIndex];
            if (wordTarget < 0)
            {
                continue;
            }

            int landCount = CountLand(exact.ChannelLevels.AsSpan(wordIndex * 16, 16));
            if ((wordTarget < 8 && landCount >= 8)
                || (wordTarget > 8 && landCount <= 8))
            {
                exactObservedBinaryErrors++;
            }

            exactObservedGrayError += Math.Abs(landCount - wordTarget);
        }

        Equal(exactObservedBinaryErrors, result.Score, "all-word majority matches channel levels");
        Equal(exactObservedGrayError, resultGrayError, "all-word gray matches channel levels");

        RasterImage transparent = RasterImage.CreateForTest(8, 8, 0, 0, 0, 0);
        ImageConstraint emptyConstraint = ImageTargetMapper.Map(
            transparent,
            0,
            new ImageMappingOptions(
                TotalSectors: 16,
                InnerRadiusMm: 24,
                OuterRadiusMm: 24.1,
                ChannelBitLengthNm: 133.33,
                StartAngleDegrees: 0,
                Clockwise: false,
                LuminanceThreshold: 128,
                AlphaThreshold: 1,
                SampleEveryChannelBits: 256));
        SolverResult emptyResult = SolverEngine.Solve(
            emptyConstraint,
            initialPayload,
            0,
            firstPsn,
            boundary,
            options);
        Equal(0, emptyResult.Score, "transparent control block score");
        Equal(0, emptyResult.CompletedIterations, "transparent control block iterations");
        Equal(0, emptyResult.ScoreMaximum, "transparent control block score maximum");
        Equal(0, emptyResult.GrayScoreMaximum ?? -1, "transparent gray score maximum");
    }

    private static void ValidateDataRll(ReadOnlySpan<byte> levels, bool initialLand)
    {
        bool previous = initialLand;
        byte[] nrz = new byte[EfmPlusEncoder.BitsPerSyncFrame];
        for (int frameOffset = 0; frameOffset < levels.Length; frameOffset += EfmPlusEncoder.BitsPerSyncFrame)
        {
            for (int index = 0; index < nrz.Length; index++)
            {
                bool current = levels[frameOffset + index] != 0;
                nrz[index] = current == previous ? (byte)0 : (byte)1;
                previous = current;
            }

            int zeroRun = 0;
            for (int index = 32; index < nrz.Length; index++)
            {
                if (nrz[index] == 0)
                {
                    zeroRun++;
                }
                else
                {
                    True(
                        zeroRun is >= 2 and <= 10,
                        $"RLL zero run {zeroRun} at channel offset {frameOffset + index}");
                    zeroRun = 0;
                }
            }
        }
    }

    private static int CountLand(ReadOnlySpan<byte> levels)
    {
        int count = 0;
        foreach (byte level in levels)
        {
            count += level;
        }

        return count;
    }

    private static void TestImageMapping()
    {
        const double innerRadius = 24.0;
        const double outerRadius = 58.0;
        const double trackLength = 12_000_000.0;
        ArchimedeanSpiral spiral = ArchimedeanSpiral.Create(innerRadius, outerRadius, trackLength);
        Near(innerRadius, spiral.RadiusAtArcLength(0), 1e-9, "spiral start radius");
        Near(outerRadius, spiral.RadiusAtArcLength(trackLength), 1e-9, "spiral end radius");
        True(spiral.TrackPitchMm > 0, "spiral pitch");

        RasterImage black = RasterImage.CreateForTest(16, 16, 0, 0, 0, 255);
        ImageMappingOptions options = new(
            TotalSectors: 16,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 24.1,
            ChannelBitLengthNm: 133.33,
            StartAngleDegrees: 0,
            Clockwise: false,
            LuminanceThreshold: 128,
            AlphaThreshold: 1);
        ImageConstraint constraint = ImageTargetMapper.Map(black, 0, options);
        Equal(DvdEccBlockEncoder.ChannelBitsPerBlock, constraint.ConstrainedBits, "opaque image constraint count");
        Equal((sbyte)0, constraint.TargetAt(0), "black maps to pit");
        Equal((sbyte)0, constraint.TargetAt(DvdEccBlockEncoder.ChannelBitsPerBlock - 1), "black final target");
    }

    private static void TestIsoOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-image-solver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] payload = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
            for (int index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)((index * 37) + 11);
            }

            string minimalPath = Path.Combine(directory, "minimal.iso");
            IsoWriteSummary minimal = IsoImageWriter.CreateMinimal(minimalPath, payload, 0, 64, "TEST_DISC");
            Equal((uint)64, minimal.VolumeSectors, "minimal ISO sectors");
            Equal(64L * IsoImageWriter.LogicalSectorBytes, new FileInfo(minimalPath).Length, "minimal ISO size");
            True(minimal.VisibleFileLba is >= 18, "system-area payload has visible file copy");

            using (FileStream iso = File.OpenRead(minimalPath))
            {
                byte[] pvd = ReadIsoSector(iso, 16);
                Equal((byte)1, pvd[0], "PVD type");
                SequenceEqual("CD001"u8, pvd.AsSpan(1, 5), "PVD identifier");
                Equal((uint)64, BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80, 4)), "PVD LE volume size");
                Equal((uint)64, BinaryPrimitives.ReadUInt32BigEndian(pvd.AsSpan(84, 4)), "PVD BE volume size");

                byte[] terminator = ReadIsoSector(iso, 17);
                Equal((byte)255, terminator[0], "descriptor terminator type");
                SequenceEqual("CD001"u8, terminator.AsSpan(1, 5), "terminator identifier");

                byte[] solvedAtLba = new byte[payload.Length];
                iso.Position = 0;
                iso.ReadExactly(solvedAtLba);
                SequenceEqual(payload, solvedAtLba, "minimal ISO solved sectors");

                iso.Position = checked((long)minimal.VisibleFileLba!.Value * IsoImageWriter.LogicalSectorBytes);
                iso.ReadExactly(solvedAtLba);
                SequenceEqual(payload, solvedAtLba, "visible SOLVED.BIN copy");
            }

            byte[] replacement = payload.Select(value => (byte)(value ^ 0xA5)).ToArray();
            string patchedPath = Path.Combine(directory, "patched.iso");
            IsoWriteSummary patched = IsoImageWriter.PatchTemplate(minimalPath, patchedPath, replacement, 32);
            Equal("template", patched.Mode, "ISO template mode");
            using FileStream patchedIso = File.OpenRead(patchedPath);
            patchedIso.Position = 32L * IsoImageWriter.LogicalSectorBytes;
            byte[] actualReplacement = new byte[replacement.Length];
            patchedIso.ReadExactly(actualReplacement);
            SequenceEqual(replacement, actualReplacement, "template replacement sectors");

            string dataDirectory = Path.Combine(directory, "data-source");
            string nestedDirectory = Path.Combine(dataDirectory, "子目录");
            Directory.CreateDirectory(nestedDirectory);
            byte[] rootFile = "hybrid root file"u8.ToArray();
            byte[] nestedFile = Enumerable.Range(0, 4097).Select(index => (byte)(index * 29)).ToArray();
            File.WriteAllBytes(Path.Combine(dataDirectory, "资料.txt"), rootFile);
            File.WriteAllBytes(Path.Combine(nestedDirectory, "hello.bin"), nestedFile);

            string hybridPath = Path.Combine(directory, "hybrid.iso");
            HybridIsoWriteSummary hybrid = HybridIsoImageWriter.Create(
                dataDirectory,
                hybridPath,
                128,
                "HYBRID_TEST");
            Equal("hybrid-iso9660-joliet", hybrid.Mode, "hybrid ISO mode");
            Equal(2, hybrid.FileCount, "hybrid ISO file count");
            Equal(2, hybrid.DirectoryCount, "hybrid ISO directory count");
            True(hybrid.DataEndLbaExclusive < 128, "hybrid ISO leaves drawing sectors");
            Equal(128L * IsoImageWriter.LogicalSectorBytes, new FileInfo(hybridPath).Length, "hybrid ISO size");

            IsoDirectoryEntry rootEntry;
            IsoDirectoryEntry nestedEntry;
            using (FileStream hybridIso = File.OpenRead(hybridPath))
            {
                byte[] svd = ReadIsoSector(hybridIso, 17);
                Equal((byte)2, svd[0], "Joliet descriptor type");
                SequenceEqual("%/E"u8, svd.AsSpan(88, 3), "Joliet level 3 escape sequence");
                uint jolietRootLba = BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(158, 4));
                uint jolietRootBytes = BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(166, 4));
                byte[] jolietRoot = ReadIsoExtent(hybridIso, jolietRootLba, jolietRootBytes);
                rootEntry = FindIsoDirectoryEntry(jolietRoot, "资料.txt;1", joliet: true);
                nestedEntry = FindIsoDirectoryEntry(jolietRoot, "子目录", joliet: true);
                True(!rootEntry.IsDirectory, "hybrid Joliet file record flag");
                True(nestedEntry.IsDirectory, "hybrid Joliet directory record flag");

                byte[] actualRootFile = ReadIsoExtent(
                    hybridIso,
                    rootEntry.ExtentLba,
                    rootEntry.DataLength);
                SequenceEqual(rootFile, actualRootFile, "hybrid Joliet root file data");

                byte[] nestedRecords = ReadIsoExtent(
                    hybridIso,
                    nestedEntry.ExtentLba,
                    nestedEntry.DataLength);
                IsoDirectoryEntry nestedFileEntry = FindIsoDirectoryEntry(
                    nestedRecords,
                    "hello.bin;1",
                    joliet: true);
                byte[] actualNestedFile = ReadIsoExtent(
                    hybridIso,
                    nestedFileEntry.ExtentLba,
                    nestedFileEntry.DataLength);
                SequenceEqual(nestedFile, actualNestedFile, "hybrid Joliet nested file data");
            }

            byte[] drawingSector = Enumerable.Repeat((byte)0xA7, IsoImageWriter.LogicalSectorBytes).ToArray();
            using (RawIsoImageWriter drawingWriter = new(hybridPath, 128, preserveExisting: true))
            {
                drawingWriter.WriteBlock(hybrid.DataEndLbaExclusive, drawingSector);
            }

            using (FileStream hybridIso = File.OpenRead(hybridPath))
            {
                byte[] actualRootFile = ReadIsoExtent(
                    hybridIso,
                    rootEntry.ExtentLba,
                    rootEntry.DataLength);
                SequenceEqual(rootFile, actualRootFile, "outer write preserves hybrid file data");
                SequenceEqual(
                    drawingSector,
                    ReadIsoSector(hybridIso, hybrid.DataEndLbaExclusive),
                    "hybrid outer drawing sector");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] ReadIsoSector(FileStream stream, uint lba)
    {
        byte[] sector = new byte[IsoImageWriter.LogicalSectorBytes];
        stream.Position = checked((long)lba * IsoImageWriter.LogicalSectorBytes);
        stream.ReadExactly(sector);
        return sector;
    }

    private static byte[] ReadIsoExtent(FileStream stream, uint lba, uint byteCount)
    {
        byte[] extent = new byte[checked((int)byteCount)];
        stream.Position = checked((long)lba * IsoImageWriter.LogicalSectorBytes);
        stream.ReadExactly(extent);
        return extent;
    }

    private static IsoDirectoryEntry FindIsoDirectoryEntry(
        ReadOnlySpan<byte> directory,
        string expectedIdentifier,
        bool joliet)
    {
        int offset = 0;
        while (offset < directory.Length)
        {
            int recordLength = directory[offset];
            if (recordLength == 0)
            {
                offset = checked(
                    ((offset / IsoImageWriter.LogicalSectorBytes) + 1)
                    * IsoImageWriter.LogicalSectorBytes);
                continue;
            }

            ReadOnlySpan<byte> record = directory.Slice(offset, recordLength);
            int identifierLength = record[32];
            ReadOnlySpan<byte> identifierBytes = record.Slice(33, identifierLength);
            string identifier = joliet
                ? System.Text.Encoding.BigEndianUnicode.GetString(identifierBytes)
                : System.Text.Encoding.ASCII.GetString(identifierBytes);
            if (identifier == expectedIdentifier)
            {
                return new IsoDirectoryEntry(
                    BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(2, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(10, 4)),
                    (record[25] & 0x02) != 0);
            }

            offset += recordLength;
        }

        throw new InvalidOperationException(
            $"ISO directory entry '{expectedIdentifier}' was not found.");
    }

    private static void TestCalibrationAndRange()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dvd-range-solver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "source.png");
            WriteTestPng(imagePath);
            string previewPath = Path.Combine(directory, "preview.png");
            CalibrationPreviewOptions previewOptions = new(
                TotalSectors: 2_297_888,
                StartLba: 0,
                FillSectors: 2_297_888,
                GeneratedInnerRadiusMm: 24,
                GeneratedOuterRadiusMm: 58,
                ActualInnerRadiusMm: 24,
                ActualOuterRadiusMm: 58,
                ChannelBitLengthNm: 133.33,
                Clockwise: false,
                LuminanceThreshold: 128,
                AlphaThreshold: 1,
                PreviewSize: 64,
                SamplesPerSector: 16);
            CalibrationPreviewSummary preview = CalibrationPreviewRenderer.Render(
                imagePath,
                previewPath,
                previewOptions);
            True(File.Exists(previewPath), "calibration preview exists");
            Near(
                preview.GeneratedTrackPitchMicrometres,
                preview.ActualTrackPitchMicrometres,
                1e-9,
                "identical-radius calibration pitch");
            Equal(16, preview.SamplesPerSector, "calibration samples per sector");
            Equal(
                "forward-channel-splat-average",
                preview.MappingMode,
                "calibration forward mapping mode");

            string crossImagePath = Path.Combine(directory, "cross.png");
            WriteCrossPng(crossImagePath);
            string radialPreviewPath = Path.Combine(directory, "physical-preview.png");
            CalibrationPreviewRenderer.Render(
                crossImagePath,
                radialPreviewPath,
                previewOptions with
                {
                    ActualOuterRadiusMm = 58.5,
                    PreviewSize = 256,
                });
            int intermediatePixels = CountIntermediateOpaquePixels(radialPreviewPath);
            True(
                intermediatePixels > 100,
                "physical calibration averages sub-pixel tracks instead of binary moire");

            ImageMappingOptions mapping = new(
                TotalSectors: 64,
                InnerRadiusMm: 24,
                OuterRadiusMm: 24.1,
                ChannelBitLengthNm: 133.33,
                StartAngleDegrees: 0,
                Clockwise: false,
                LuminanceThreshold: 128,
                AlphaThreshold: 1,
                SampleEveryChannelBits: 64);
            MultiBlockSolveOptions rangeOptions = new(
                StartLba: 0,
                FillSectors: 32,
                PsnOffset: 0x30000,
                IterationsPerBlock: 1,
                MutationBytes: 4,
                RandomSeed: 9,
                InitialTemperature: 64,
                FinalTemperature: 0.25,
                ZeroInitialPayload: false,
                ImageMapping: mapping,
                Algorithm: SolverAlgorithm.StateControl,
                BridgeBytes: 1,
                BeamWidth: 8,
                RetryBeamWidth: 0,
                RetryFrameFraction: 0.1,
                BatchFrames: 8,
                BatchCandidates: 1);
            string rawIsoPath = Path.Combine(directory, "range.iso");
            MultiBlockSolveResult range = MultiBlockImageSolver.Solve(
                imagePath,
                rawIsoPath,
                new ModulationBoundary(1, 0, IsLand: true),
                rangeOptions,
                progress: null);
            Equal(2, range.CompletedBlocks, "range ECC block count");
            Equal((uint)32, range.IsoSectors, "range ISO sector count");
            Equal(
                "all-channel-word-majority-error",
                range.ScoreMetric,
                "range state-control metric");
            Equal(
                "all-channel-word-gray-error",
                range.GrayScoreMetric ?? string.Empty,
                "range gray metric");
            Equal(32L * IsoImageWriter.LogicalSectorBytes, new FileInfo(rawIsoPath).Length, "range ISO size");
            True(File.ReadAllBytes(rawIsoPath).AsSpan().IndexOfAnyExcept((byte)0) >= 0, "range ISO payload data");

            ImageMappingOptions dispersionMapping = mapping with
            {
                SampleEveryChannelBits = 256,
            };
            byte[] zeroPayload = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
            SolverResult exactDispersion = SolverEngine.Solve(
                ImageTargetMapper.Map(RasterImage.Load(imagePath), 0, dispersionMapping),
                zeroPayload,
                initialLba: 0,
                psnOffset: 0x30000,
                new ModulationBoundary(1, 0, IsLand: true),
                new SolverOptions(
                    Iterations: 1,
                    MutationBytes: 4,
                    RandomSeed: 9,
                    InitialTemperature: 64,
                    FinalTemperature: 0.25,
                    Algorithm: SolverAlgorithm.Dispersion));
            MultiBlockSolveOptions fastOptions = rangeOptions with
            {
                FillSectors = 16,
                ZeroInitialPayload = true,
                ImageMapping = dispersionMapping,
                Algorithm = SolverAlgorithm.Dispersion,
            };
            string fastIsoPath = Path.Combine(directory, "fast-dispersion.iso");
            FastDispersionWriteResult fast = FastDispersionImageWriter.Write(
                imagePath,
                fastIsoPath,
                fastOptions,
                progress: null);
            Equal(1, fast.CompletedBlocks, "fast dispersion block count");
            Equal(
                (long)DvdEccBlockEncoder.PayloadBytesPerBlock,
                fast.ControlledWords,
                "fast dispersion controlled words");
            SequenceEqual(
                exactDispersion.Payloads,
                File.ReadAllBytes(fastIsoPath),
                "fast dispersion matches exact-replay payload bytes");

            // The read-back simulation must classify every dispersion payload byte of the
            // generated ISO and reproduce a non-trivial two-level scan image from it.
            string readbackPath = Path.Combine(directory, "readback.png");
            IsoReadbackSummary readback = IsoReadbackRenderer.Render(
                fastIsoPath,
                readbackPath,
                new IsoReadbackOptions(
                    TotalSectors: 64,
                    StartLba: 0,
                    FillSectors: 16,
                    PsnOffset: 0x30000,
                    InnerRadiusMm: 24,
                    OuterRadiusMm: 24.1,
                    ChannelBitLengthNm: 133.33,
                    Clockwise: false,
                    PreviewSize: 256,
                    SamplesPerSector: 64));
            True(File.Exists(readbackPath), "ISO readback preview exists");
            Equal("payload-scramble-classify-splat", readback.MappingMode, "ISO readback mapping mode");
            Equal(16L * 64, readback.TotalSamples, "ISO readback sample count");
            Equal(readback.TotalSamples, readback.ClassifiedSamples, "ISO readback classification");
            (int darkPixels, int lightPixels) = CountDarkAndLightPixels(readbackPath);
            True(darkPixels > 0, "ISO readback renders dark code words");
            True(lightPixels > 0, "ISO readback renders light code words");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteTestPng(string path, bool grayscale = false, bool binaryDither = false)
    {
        const int size = 64;
        const int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int offset = (y * stride) + (x * 4);
                byte value = binaryDither
                    ? BinaryGrayscale.GetLuminance((x + y) % 2)
                    : grayscale
                    ? (byte)((x / 16) * 85)
                    : ((x / 8) + (y / 8)) % 2 == 0 ? (byte)0 : (byte)255;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = grayscale && x < 8 && y < 32 ? (byte)0 : (byte)255;
            }
        }

        RasterCodec.SavePng(path, size, size, pixels, grayscale: false);
    }

    private static void WriteCrossPng(string path)
    {
        const int size = 128;
        const int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        double centre = (size - 1) / 2.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int offset = (y * stride) + (x * 4);
                byte value = Math.Abs(x - centre) <= 4 || Math.Abs(y - centre) <= 4
                    ? (byte)0
                    : (byte)255;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        RasterCodec.SavePng(path, size, size, pixels, grayscale: false);
    }

    private static (int DarkPixels, int LightPixels) CountDarkAndLightPixels(string path)
    {
        byte[] pixels = RasterCodec.LoadBgra32(path).Pixels;
        int dark = 0;
        int light = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] != 255)
            {
                continue;
            }

            if (pixels[offset] < 128)
            {
                dark++;
            }
            else if (pixels[offset] > 200)
            {
                light++;
            }
        }

        return (dark, light);
    }

    private static bool ContainsOpaqueGray(string path, byte gray)
    {
        byte[] pixels = RasterCodec.LoadBgra32(path).Pixels;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] == gray && pixels[offset + 1] == gray
                && pixels[offset + 2] == gray && pixels[offset + 3] == 255)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountIntermediateOpaquePixels(string path)
    {
        byte[] pixels = RasterCodec.LoadBgra32(path).Pixels;
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte level = pixels[offset];
            if (pixels[offset + 3] == 255 && level is > 0 and < 255)
            {
                count++;
            }
        }

        return count;
    }

    private static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"selftest failed: {name}");
        }
    }

    private static void Equal<T>(T expected, T actual, string name) where T : notnull
        => True(EqualityComparer<T>.Default.Equals(expected, actual), $"{name}: expected {expected}, got {actual}");

    private static void Near(double expected, double actual, double tolerance, string name)
        => True(Math.Abs(expected - actual) <= tolerance, $"{name}: expected {expected}, got {actual}");

    private static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string name)
        => True(expected.SequenceEqual(actual), name);

    private readonly record struct IsoDirectoryEntry(
        uint ExtentLba,
        uint DataLength,
        bool IsDirectory);
}
