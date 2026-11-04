using System.Text.Json.Nodes;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;

internal static class CalibrationClockArtworkTests
{
    internal static void Run()
    {
        var cd = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200);
        var clock = CalibrationTarget.Create(cd, artwork: CalibrationArtwork.ClockRuler);
        Require(clock.SchemaVersion == 8 && clock.HasClockRuler && !clock.HasAngularReference
            && clock.Groups.Count == 0 && clock.VisibleRings.Count == 6, "clock target identity and bands");
        var roundTrip = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(clock, []))).Target;
        Require(roundTrip.Id == clock.Id && roundTrip.Artwork == clock.Artwork
            && roundTrip.VisibleRings.SequenceEqual(clock.VisibleRings), "clock band positions survive reload");
        Require(CalibrationClockRulerPattern.PeriodLengthMm(cd) == 14, "2058 CD bytes cover a nominal 14 mm");
        double range2 = cd.OuterRadiusMm * cd.OuterRadiusMm - cd.InnerRadiusMm * cd.InnerRadiusMm;
        foreach (var ring in clock.VisibleRings)
        {
            double q = (ring.InnerRadiusMm * ring.InnerRadiusMm - cd.InnerRadiusMm * cd.InnerRadiusMm) / range2;
            Require(Math.Abs(q - CalibrationClockRulerPattern.Stations[ring.RingIndex]
                + CalibrationClockRulerPattern.InkHalfWidth) < 1e-12, "inner edge has the engraved logical address");
        }
        Require(CalibrationClockRulerPattern.Supports(cd with { Sectors = 333000 })
            && CalibrationClockRulerPattern.Supports(cd with { InnerRadiusMm = 20, OuterRadiusMm = 51 }),
            "74-minute CD and imprecise starting geometry are eligible");
        foreach (var unsupported in new[] { cd with { OuterRadiusMm = 38 }, cd with { Sectors = 180000 },
            cd with { Kind = CalibrationDiscKind.Dvd }, cd with { LinearDensity = 1300 } })
        {
            Require(!CalibrationClockRulerPattern.Supports(unsupported), "unsupported target falls back");
            Throws<ArgumentException>(() => CalibrationTarget.Create(unsupported, artwork: CalibrationArtwork.ClockRuler));
        }
        string json = CalibrationSessionJson.Serialize(new(clock, []));
        var invalid = JsonNode.Parse(json)!;
        invalid["target"]!["schemaVersion"] = 7;
        Throws<ArgumentException>(() => CalibrationSessionJson.Deserialize(invalid.ToJsonString()));
        invalid = JsonNode.Parse(json)!;
        invalid["target"]!["artwork"] = "FineCross";
        Throws<ArgumentException>(() => CalibrationSessionJson.Deserialize(invalid.ToJsonString()));
        Throws<InvalidOperationException>(() => clock.Sample(30, 0));
        Throws<InvalidOperationException>(() => CalibrationPatternRenderer.RenderEncodingImage(clock));
        Throws<ArgumentException>(() => CalibrationCrossFitter.ValidateTraces(clock, [new(0, [new(36, 0)])]));

        const int size = 900;
        var preview = CalibrationPatternRenderer.Render(roundTrip, 60, size);
        byte[] pixels = new byte[size * size];
        preview.CopyPixels(pixels, size, 0);
        int Sample(double radius, double angle)
        {
            int x = (int)((60 + radius * Math.Cos(angle)) * size / 120);
            int y = (int)((60 + radius * Math.Sin(angle)) * size / 120);
            return pixels[y * size + x];
        }
        Require(Sample(0, 0) == 255 && Sample(59, 0) == 255
            && clock.VisibleRings.All(r => Sample(r.RadiusMm, 0) < 50), "schematic shows six dark address bands and a clear background");
        double resonance = 17 * CalibrationClockRulerPattern.PeriodLengthMm(cd) / Math.Tau;
        var around = Enumerable.Range(0, 360).Select(i => Sample(resonance, i * Math.PI / 180)).ToArray();
        Require(around.Max() - around.Min() > 100, "clock dashes are legible on the overview");
        Console.WriteLine("calibration-clock-artwork: passed (address geometry, versioning, eligibility and schematic preview)");
    }

    private static void Require(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
