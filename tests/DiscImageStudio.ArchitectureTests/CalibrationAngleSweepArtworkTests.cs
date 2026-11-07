using DiscImageStudio.Core.Calibration;

internal static class CalibrationAngleSweepArtworkTests
{
    internal static void Run()
    {
        Check(new(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200));
        Check(new(CalibrationDiscKind.Dvd, 24.1, 58.1, 2297888, 133.3, PitchCubic: .002));
        Check(new(CalibrationDiscKind.BluRay, 24, 58, 12219392, 74.5,
            DrawingStartSector: 32 * 1200, DrawingEndSectorExclusive: 32 * 343001));
        var mini = new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 38, 714544, 133.3);
        Require(!CalibrationWideBandPattern.SupportsAngleSweep(mini) && CalibrationWideBandPattern.Supports(mini),
            "narrow bands keep the measurable six-edge target");
        CheckCdUsableBoundary();
    }

    private static void CheckCdUsableBoundary()
    {
        var p = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359849, 1200);
        // The last fixed CD band ends at 95.5% of the geometry, independent of
        // the output limit. One whole light sector must follow its rounded edge.
        long finalEdgeSector = (p.Sectors * 955 + 999) / 1000;
        foreach (var artwork in new[] { CalibrationArtwork.WideAddressBands, CalibrationArtwork.AngleSweepBands })
        {
            var full = CalibrationTarget.Create(p, artwork: artwork);
            foreach (long usable in new[] { 287872L, finalEdgeSector })
            {
                try
                {
                    CalibrationTarget.Create(p with { UsableSectors = usable }, artwork: artwork);
                    throw new InvalidOperationException("Incomplete fixed CD bands were accepted.");
                }
                catch (ArgumentException) { }
            }

            foreach (long usable in new[] { finalEdgeSector + 1, p.Sectors - 2 })
            {
                var limited = CalibrationTarget.Create(p with { UsableSectors = usable }, artwork: artwork);
                Require(limited.VisibleRings.SequenceEqual(full.VisibleRings)
                    && limited.VisibleRings.All(r => r.RadiusMm + r.WidthMm / 2 < limited.DrawingOuterRadiusMm),
                    "sufficient output preserves every fixed CD edge without rescaling");
            }

            // Deserialization bypasses new-target qualification so saved artwork
            // remains reproducible, including a previously saved truncated output.
            var frozen = new CalibrationTarget(full.SchemaVersion, full.Id,
                p with { UsableSectors = 287872 }, full.Options, full.Cells, artwork);
            var restored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(frozen, []))).Target;
            Require(restored.Parameters.UsableSectors == 287872
                && restored.VisibleRings.SequenceEqual(full.VisibleRings),
                "frozen CD records retain their original limits and full-track addresses");
        }
    }

    private static void Check(CalibrationParameters p)
    {
        var old = CalibrationTarget.Create(p, artwork: CalibrationArtwork.WideAddressBands);
        var target = CalibrationTarget.Create(p, artwork: CalibrationArtwork.AngleSweepBands);
        var reloaded = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(target, []))).Target;
        var sampler = new CalibrationAngleSweepSampler(reloaded);
        Require(reloaded.HasAngleSweep && reloaded.HasWideAddressBands && !reloaded.HasCrossArms
            && reloaded.SchemaVersion == 13 && old.VisibleRings.SequenceEqual(reloaded.VisibleRings)
            && old.WideBandSectorRanges.SequenceEqual(reloaded.WideBandSectorRanges),
            "same six physical boundaries, separate session rules");
        var changes = sampler.Switches;
        Require(changes.Count(c => c.Luminance == null) == 3 && changes.Zip(changes.Skip(1))
            .All(pair => pair.First.Progress < pair.Second.Progress), "ordered half-wave schedule");
        for (int i = 0; i < 3; i++)
        {
            var station = reloaded.AngleSweepStations[i];
            var (start, end) = p.Kind == CalibrationDiscKind.Cd
                ? (old.WideAddressBands[i].StartProgress, old.WideAddressBands[i].EndProgress)
                : (old.WideBandSectorRanges[i].Start / (double)p.Sectors,
                    old.WideBandSectorRanges[i].EndExclusive / (double)p.Sectors);
            Require(station.StartProgress > start && station.EndProgress < end
                && sampler.Sample((start + station.StartProgress) / 2) == 0
                && sampler.Sample((end + station.EndProgress) / 2) == 0,
                "pure guard strips preserve both original edges");
            var window = changes.Where(c => c.Progress >= station.StartProgress && c.Progress < station.EndProgress).ToArray();
            Require(window.Length > 20 && window[0].Progress == station.StartProgress, "usable carrier");
            // The clock must change colour after the integrated angular travel is pi.
            // Also replay the planar preview at actual successive track positions.
            for (int j = 1; j < window.Length; j += Math.Max(1, window.Length / 30))
            {
                double lo = window[j-1].Progress, hi = window[j].Progress, middle = (lo + hi) / 2;
                double Simpson(double q) => p.TrackLengthMm / station.VirtualRadiusAt(q);
                double phase = (hi - lo) / 6 * (Simpson(lo) + 4 * Simpson(middle) + Simpson(hi));
                Require(Math.Abs(phase - Math.PI) < 1e-7, "half turn from an independent integral");
                double radius = Math.Sqrt((1 - middle) * p.InnerRadiusMm * p.InnerRadiusMm
                    + middle * p.OuterRadiusMm * p.OuterRadiusMm);
                double angle = 2 * p.TrackLengthMm * middle / (p.InnerRadiusMm + radius);
                Require(target.Sample(radius * Math.Cos(angle), radius * Math.Sin(angle)) == sampler.Sample(middle)
                    && sampler.Sample(Math.BitDecrement(hi)) != sampler.Sample(hi), "preview and physical clock agree");
            }
        }
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException("Angular carrier: " + message);
    }
}
