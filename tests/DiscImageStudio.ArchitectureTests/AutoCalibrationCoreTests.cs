using System.Buffers.Binary;
using System.IO;
using DiscImageStudio.AutoCalibration;

internal static class AutoCalibrationCoreTests
{
    public static void Run()
    {
        MediaProfilesAndMmcCommands();
        PeriodEstimationAndCircularStatistics();
        CalibrationImageGeneration();
        CalibrationDiscRecognition();
        SimulatedLadderMeasurement();
        SimulatedCdLadderMeasurement();
        Console.WriteLine("auto-calibration core: media profiles, MMC commands, spindle analysis, and marker ISO passed");
    }

    private static void MediaProfilesAndMmcCommands()
    {
        if (CalibrationMediaProfile.Parse("CD-ROM") != CalibrationMedia.Cd
            || CalibrationMediaProfile.Parse("cd") != CalibrationMedia.Cd
            || CalibrationMediaProfile.Parse("dvd") != CalibrationMedia.Dvd
            || CalibrationMediaProfile.Parse("DVD+R") != CalibrationMedia.Dvd
            || CalibrationMediaProfile.IsSupported("DVD+R DL"))
        {
            throw new InvalidOperationException("Calibration media classification changed unexpectedly.");
        }

        if (CalibrationMediaProfile.CompletionBit(CalibrationMedia.Dvd, 17) != 32 * 38_688.0
            || CalibrationMediaProfile.CompletionBit(CalibrationMedia.Cd, 0) != 206 * 588.0)
        {
            throw new InvalidOperationException("The read completion points do not match Discratcher.");
        }

        byte[] read = MmcCommandSet.Read12(0x12345678, 0x01020304, fua: true);
        if (read[0] != 0xA8 || read[1] != 0x08
            || BinaryPrimitives.ReadUInt32BigEndian(read.AsSpan(2, 4)) != 0x12345678
            || BinaryPrimitives.ReadUInt32BigEndian(read.AsSpan(6, 4)) != 0x01020304)
        {
            throw new InvalidOperationException("READ(12) CDB layout is invalid.");
        }
    }

    private static void PeriodEstimationAndCircularStatistics()
    {
        const double period = 25.474e6;
        var timestamps = new List<double>(60);
        double time = 0;
        var random = new Random(9);
        for (int i = 0; i < 60; i++)
        {
            time += period * (2 + random.Next(4));
            timestamps.Add(time + (random.NextDouble() - 0.5) * 80_000);
        }

        SpindlePeriodFit fit = SpindlePhaseMath.EstimatePeriod(timestamps, 1_200, 12_000);
        if (Math.Abs(fit.PeriodNanoseconds / period - 1) >= 1e-4 || fit.Coherence < 0.98)
        {
            throw new InvalidOperationException($"Unexpected spindle period fit: {fit}.");
        }

        CircularPhaseStatistics stats = SpindlePhaseMath.CircularStatistics(
            [359.2, 359.8, 0.1, 359.7, 170, 45]);
        if (Math.Abs(SpindlePhaseMath.WrapDegrees(stats.MeanDegrees - 359.7)) > 0.5
            || stats.UsedSamples < 3)
        {
            throw new InvalidOperationException($"Circular statistics mishandled wraparound/outliers: {stats}.");
        }
    }

    private static void CalibrationImageGeneration()
    {
        string path = Path.Combine(Path.GetTempPath(), $"disc-calibration-{Guid.NewGuid():N}.iso");
        try
        {
            CalibrationImageGenerator.GenerateAsync(path, 64).GetAwaiter().GetResult();
            byte[] image = File.ReadAllBytes(path);
            if (image.Length != 64 * CalibrationMediaProfile.SectorBytes
                || !image.AsSpan(16 * 2048 + 1, 5).SequenceEqual("CD001"u8)
                || !image.AsSpan(16 * 2048 + 40, CalibrationImageGenerator.VolumeLabel.Length)
                    .SequenceEqual(System.Text.Encoding.ASCII.GetBytes(CalibrationImageGenerator.VolumeLabel))
                || image[17 * 2048] != 255)
            {
                throw new InvalidOperationException("Generated marker ISO has an invalid PVD or size.");
            }

            byte[] regenerated = CalibrationImageGenerator.CreateSector(31, 64);
            if (!regenerated.AsSpan().SequenceEqual(image.AsSpan(31 * 2048, 2048)))
            {
                throw new InvalidOperationException("Random marker sectors are not reproducible.");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CalibrationDiscRecognition()
    {
        CalibrationDiscCheck ownDisc = CalibrationDiscChecker.Check(
            new FakeCalibrationDrive(50_000, foreign: false),
            CalibrationMedia.Cd);
        CalibrationDiscCheck otherBurn = CalibrationDiscChecker.Check(
            new FakeCalibrationDrive(50_000, foreign: true),
            CalibrationMedia.Cd);

        if (ownDisc.Sectors != 50_000 || ownDisc.Foreign || !otherBurn.Foreign)
        {
            throw new InvalidOperationException("Calibration disc verification did not classify sample contents correctly.");
        }
    }

    private static void SimulatedLadderMeasurement()
    {
        using var drive = new SimulatedOpticalDrive(CalibrationReferenceGeometry.Dvd, seed: 7);
        CalibrationMeasurementResult result = CalibrationLadderRunner.Run(
            drive,
            CalibrationMedia.Dvd,
            new CalibrationLadderOptions());
        CalibrationSpiral truth = CalibrationSpiral.Create(CalibrationReferenceGeometry.Dvd);
        CalibrationSpiral fitted = CalibrationSpiral.Create(result.Geometry);
        double referenceBit = CalibrationMediaProfile.CompletionBit(CalibrationMedia.Dvd, 1_124_592);
        double referenceTruth = truth.AngleAtBit(referenceBit);
        double referenceFit = fitted.AngleAtBit(referenceBit);
        double trackBits = truth.TotalBits;
        double maximumAngleError = Enumerable.Range(0, 301)
            .Select(index => trackBits * (0.02 + 0.94 * index / 300.0))
            .Max(bit => Math.Abs((fitted.AngleAtBit(bit) - referenceFit
                - (truth.AngleAtBit(bit) - referenceTruth)) * 180 / Math.PI));
        if (result.Rungs.Count != 56
            || !double.IsFinite(result.Geometry.InnerRadiusMm)
            || !double.IsFinite(result.Geometry.OuterRadiusMm)
            || Math.Abs(result.SpindlePeriodMilliseconds - 25.474) > 0.02
            || result.RmsResidualDegrees >= 0.5
            || maximumAngleError >= 1.0)
        {
            throw new InvalidOperationException(
                $"Simulated DVD calibration differs from its reference: rms={result.RmsResidualDegrees:F3}, maxAngle={maximumAngleError:F3}, period={result.SpindlePeriodMilliseconds:F3} ms.");
        }
    }

    private static void SimulatedCdLadderMeasurement()
    {
        CalibrationSpiralParameters parameters = CalibrationReferenceGeometry.Cd();
        using var drive = new SimulatedOpticalDrive(parameters, seed: 5);
        CalibrationMeasurementResult result = CalibrationLadderRunner.Run(
            drive,
            CalibrationMedia.Cd,
            new CalibrationLadderOptions(MaxTargets: 36, Repetitions: 32),
            imageSectors: parameters.TotalSectors);
        CalibrationSpiral truth = CalibrationSpiral.Create(parameters);
        CalibrationSpiral fitted = CalibrationSpiral.Create(result.Geometry);
        double referenceBit = CalibrationMediaProfile.CompletionBit(CalibrationMedia.Cd, 176_291);
        double referenceTruth = truth.AngleAtBit(referenceBit);
        double referenceFit = fitted.AngleAtBit(referenceBit);
        double trackBits = truth.TotalBits;
        double maximumAngleError = Enumerable.Range(0, 201)
            .Select(index => trackBits * (0.03 + 0.92 * index / 200.0))
            .Max(bit => Math.Abs((fitted.AngleAtBit(bit) - referenceFit
                - (truth.AngleAtBit(bit) - referenceTruth)) * 180 / Math.PI));
        if (result.Rungs.Count != 36
            || Math.Abs(result.SpindlePeriodMilliseconds - 48.34) > 0.02
            || result.SpecProblem is not null
            || maximumAngleError >= 1.5)
        {
            throw new InvalidOperationException(
                $"Simulated CD calibration differs from its reference: rms={result.RmsResidualDegrees:F3}, maxAngle={maximumAngleError:F3}, period={result.SpindlePeriodMilliseconds:F3} ms, spec={result.SpecProblem}.");
        }
    }

    private sealed class FakeCalibrationDrive(uint sectors, bool foreign) : ITimedOpticalDrive
    {
        public bool SupportsForceUnitAccess => true;
        public int ExpectedCdUserDataOffset => 16;

        public TimedSectorRead ReadTimed(uint lba, bool forceUnitAccess, CancellationToken cancellationToken) =>
            new(lba * 1000.0);

        public uint ReadCapacity(CancellationToken cancellationToken) => sectors;

        public void ReadSector(uint lba, Span<byte> destination, CancellationToken cancellationToken)
        {
            byte[] generated = CalibrationImageGenerator.CreateSector(lba, sectors);
            generated.CopyTo(destination);
            if (foreign && lba > CalibrationImageGenerator.FileSystemLastLba)
            {
                destination[0] ^= 0xFF;
            }
        }

        public void SetReadSpeed(ushort kilobytesPerSecond, CancellationToken cancellationToken) { }
        public void Wait(TimeSpan duration, CancellationToken cancellationToken) { }
        public void Dispose() { }
    }
}
