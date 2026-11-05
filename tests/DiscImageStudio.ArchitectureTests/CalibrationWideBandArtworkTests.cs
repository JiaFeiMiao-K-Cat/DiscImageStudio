using System.IO;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;

internal static class CalibrationWideBandArtworkTests
{
    internal static void Run()
    {
        CalibrationAngleSweepArtworkTests.Run();
        var preset = new CalibrationParameters(CalibrationDiscKind.Cd, 24.837775, 58.020875, 359848, 1200);
        var displaced = preset with { InnerRadiusMm = 20, OuterRadiusMm = 51 };
        var target = CalibrationTarget.Create(displaced, artwork: CalibrationArtwork.WideAddressBands);
        var actual = CalibrationTarget.Create(preset, artwork: CalibrationArtwork.WideAddressBands);
        Require(target.SchemaVersion == 12 && target.HasWideAddressBands && target.VisibleRings.Count == 3
            && !target.HasAngularReference && !target.HasClockRuler && !target.HasCrossArms
            && target.RadialArcDashes.Count == 0 && target.Groups.Count == 0, "target identity");

        var original = new CalibrationTarget(11, actual.Id, preset, actual.Options, actual.Cells, actual.Artwork);
        CheckIntervals(original, [new(50, 78), new(280, 320), new(860, 920)]);
        CheckIntervals(actual, [new(35, 170), new(305, 510), new(705, 955)]);
        CheckIntervals(target, [new(35, 170), new(305, 510), new(705, 955)]);
        CheckFirstEncodedTransition(original);
        CheckFirstEncodedTransition(target);

        double[] edges = actual.VisibleRings.SelectMany(r => new[] { r.InnerRadiusMm, r.InnerRadiusMm + r.WidthMm }).ToArray();
        double[] expectedSpans = [6.22, 5.22, 6.78, 5.61, 6.39];
        for (int i = 0; i < expectedSpans.Length; i++)
            Require(Math.Abs(edges[i + 1] - edges[i] - expectedSpans[i]) < .01, "alternating reference-disc dark and light widths");

        var dvd = new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 58, 2295104, 133.3);
        Require(CalibrationTarget.Create(dvd, artwork: target.Artwork).SchemaVersion == 12, "DVD uses alternating bands");
        Throws(() => CalibrationTarget.Create(dvd with { Sectors = dvd.Sectors - 1 }, artwork: target.Artwork));
        Throws(() => CalibrationTarget.Create(preset with { OuterRadiusMm = 38 }, artwork: target.Artwork));
        Throws(() => new CalibrationTarget(10, target.Id, preset, target.Options, target.Cells, target.Artwork));
        Throws(() => new CalibrationTarget(11, target.Id, preset, target.Options, target.Cells, CalibrationArtwork.RadialArcDashes));
        Throws(() => new CalibrationTarget(12, target.Id, preset, target.Options, target.Cells, CalibrationArtwork.ClockRuler));
        Console.WriteLine("calibration-wide-address-bands: v11/v12 intervals, audio transitions, widths and compatibility passed");
    }

    private static void CheckIntervals(CalibrationTarget target, CalibrationAddressBand[] expected)
    {
        var restored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(target, []))).Target;
        Require(restored.SchemaVersion == target.SchemaVersion && restored.VisibleRings.SequenceEqual(target.VisibleRings)
            && restored.Id == target.Id && restored.WideAddressBands.SequenceEqual(expected),
            "stored schema retains its geometry through a session round trip");
        var intervals = target.WideAddressBands;
        Require(intervals.SequenceEqual(expected), "dark logical intervals are frozen for this version");
        var p = target.Parameters;
        long bytes = p.Sectors * 2352L;
        for (int i = 0; i < intervals.Count; i++)
        {
            var interval = intervals[i];
            var (start, end) = interval.ByteRange(bytes);
            Require((start - 1) * 1000 < bytes * interval.StartPermille && start * 1000 >= bytes * interval.StartPermille
                && (end - 1) * 1000 < bytes * interval.EndPermille && end * 1000 >= bytes * interval.EndPermille,
                "byte transitions respect the half-open logical interval");
            double RadiusAt(double q) => Math.Sqrt(p.InnerRadiusMm * p.InnerRadiusMm
                + q * (p.OuterRadiusMm * p.OuterRadiusMm - p.InnerRadiusMm * p.InnerRadiusMm));
            var band = target.VisibleRings[i];
            Require(Math.Abs(band.InnerRadiusMm - RadiusAt(interval.StartProgress)) < 1e-10
                && Math.Abs(band.InnerRadiusMm + band.WidthMm - RadiusAt(interval.EndProgress)) < 1e-10,
                "both boundaries correspond to the intended elapsed addresses");
            foreach (var angle in new[] { 0.0, 0.37, 1.62, 2.95, 4.12, 5.84 })
            {
                byte Ink(double r) => target.Sample(r * Math.Cos(angle), r * Math.Sin(angle));
                Require(Ink(band.InnerRadiusMm - .01) == 255 && Ink(band.InnerRadiusMm + .01) == 0
                    && Ink(band.InnerRadiusMm + band.WidthMm - .01) == 0
                    && Ink(band.InnerRadiusMm + band.WidthMm + .01) == 255, "solid ink at every azimuth");
            }
        }
    }

    private static void CheckFirstEncodedTransition(CalibrationTarget target)
    {
        long total = target.Parameters.Sectors * 2352L;
        int firstDark = checked((int)target.WideAddressBands[0].ByteRange(total).Start);
        using var prefix = new MemoryStream(firstDark + (int)(total / 100));
        using var stop = new CancellationTokenSource();
        try
        {
            CdTrackGenerator.GeneratePatternToStream(target, prefix, false, p =>
            {
                if (p.CompletedBytes >= firstDark + 2352) stop.Cancel();
            }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Require(prefix.Length > firstDark && prefix.GetBuffer()[firstDark - 1] == 0xAA
            && prefix.GetBuffer()[firstDark] == 0x0D, "streamed CD uses the recorded version's first dark byte");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected incompatible target rejection.");
    }
}
