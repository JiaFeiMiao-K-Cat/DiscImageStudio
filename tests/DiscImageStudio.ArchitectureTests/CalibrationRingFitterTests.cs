using DiscImageStudio.Core.Calibration;

internal static class CalibrationRingFitterTests
{
    internal static void Run()
    {
        foreach (CalibrationParameters generated in new CalibrationParameters[]
        {
            new(CalibrationDiscKind.Cd, 24.5, 56.8, 359849, 1200),
            new(CalibrationDiscKind.Dvd, 24, 58, 2295104, 133.3),
            new(CalibrationDiscKind.Dvd, 24, 58, 2295104, 133.3, .04, -.09, .06),
            new(CalibrationDiscKind.BluRay, 24, 58, 12_219_392, 74.5, DrawingStartSector: 320,
                DrawingEndSectorExclusive: 12_219_104),
        })
        {
            CalibrationTarget target = CalibrationTarget.Create(generated);
            Require(target.VisibleRings.Count == 8 && target.VisibleRings[0].InnerRadiusMm > target.DrawingInnerRadiusMm
                && target.VisibleRings[^1].RadiusMm + target.VisibleRings[^1].WidthMm / 2 < target.DrawingOuterRadiusMm,
                "current target places eight complete guides inside the recorded area");
            Require(target.VisibleRings.Select((r, i) => r.RingIndex == i).All(x => x),
                "visible identities are consecutive");
            var actual = generated with { InnerRadiusMm = generated.InnerRadiusMm + .54, OuterRadiusMm = generated.OuterRadiusMm - .36 };
            var mapping = target.CreateMapping(actual, .75);
            CalibrationRingObservation[] measured = target.VisibleRings.Select(ring =>
            {
                CalibrationPoint mapped = mapping(new(ring.RadiusMm - ring.WidthMm / 2, 0));
                return new CalibrationRingObservation(ring.RingIndex, Math.Sqrt(mapped.X * mapped.X + mapped.Y * mapped.Y));
            }).ToArray();
            var fit = CalibrationRingFitter.Fit(target, measured.Reverse().ToArray());
            Require(fit.Succeeded && fit.BestParameters is { } best
                && Math.Abs(best.InnerRadiusMm - actual.InnerRadiusMm) < 1e-7
                && Math.Abs(best.OuterRadiusMm - actual.OuterRadiusMm) < 1e-7
                && best with { InnerRadiusMm = actual.InnerRadiusMm, OuterRadiusMm = actual.OuterRadiusMm } == actual
                && fit.Candidates[0].MaxErrorMm < 1e-7, "estimate radii from nonlinear equal-progress geometry");
            CalibrationRingObservation[] noisy = measured
                .Where((_, i) => i % 2 == 0 || i == measured.Length - 1)
                .Select((p, i) => p with { RadiusMm = p.RadiusMm + .07 * Math.Cos(i * .9) }).ToArray();
            var manual = CalibrationRingFitter.Fit(target, noisy);
            Require(manual.Succeeded && manual.Candidates[0].MaxErrorMm is > 0.001 and < .12, "noisy gapped manual arcs");

            CalibrationRingFitter.ValidateObservations(target, []);
            Require(!CalibrationRingFitter.Fit(target, measured[..2]).Succeeded, "two measurements lack a residual check");
            Require(!CalibrationRingFitter.Fit(target, measured[..3]).Succeeded, "three adjacent narrow stations are ill-conditioned");
            var crossed = measured.ToArray();
            crossed[2] = crossed[2] with { RadiusMm = crossed[1].RadiusMm - .1 };
            CalibrationRingFitter.ValidateObservations(target, crossed);
            Require(!CalibrationRingFitter.Fit(target, crossed).Succeeded, "crossed ring identities are rejected by fit");
            Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(target, [measured[0], measured[0]]));
            Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(target, [new(8, 40)]));
            Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(target, [new(0, double.NaN)]));
            Throws<OperationCanceledException>(() => CalibrationRingFitter.Fit(target, measured, new(true)));
        }
        TestEightCentimetreDvd();
        TestFineArtwork();
        TestArcSamples();
        TestLocalWideBandEdges();
        TestAlternatingBandEdges();
        TestDataDiscBandEdges();
        Console.WriteLine("calibration-rings: manual observations, radial coverage, 8 cm DVD and CD/DVD/BD coarse fits passed");
    }

    private static void TestDataDiscBandEdges()
    {
        var dvd = new CalibrationParameters(CalibrationDiscKind.Dvd, 23, 37, 714544, 133.3, .08, -.03, .02);
        var bd = new CalibrationParameters(CalibrationDiscKind.BluRay, 23, 57, 12_219_392, 74.5,
            BluRayInvertMapping: true, DrawingStartSector: 1_048_576, DrawingEndSectorExclusive: 10_000_000);
        foreach (var p in new[] { dvd, bd })
        {
            var target = CalibrationTarget.Create(p, artwork: CalibrationArtwork.WideAddressBands);
            int unit = p.Kind == CalibrationDiscKind.Dvd ? 1 : 32;
            long start = p.DrawingStartSector, end = p.DrawingEndSectorExclusive == 0 ? p.Sectors : p.DrawingEndSectorExclusive;
            long units = (end - start) / unit;
            double inner = 24.31, outer = p.OuterRadiusMm + .53;
            double AtSector(long sector) => Math.Sqrt(inner * inner + (outer * outer - inner * inner) * sector / p.Sectors);
            int[] positions = [35, 170, 305, 510, 705, 955];
            var marks = positions.Select((permille, i) =>
            {
                long sector = start + (long)Math.Ceiling(units * (permille / 1000m)) * unit;
                var range = target.WideBandSectorRanges[i / 2];
                Require((i % 2 == 0 ? range.Start : range.EndExclusive) == sector, "aligned absolute band address");
                double radius = AtSector(sector);
                var ring = target.VisibleRings[i / 2];
                double sourceRadius = ring.InnerRadiusMm + (i % 2 == 0 ? 0 : ring.WidthMm);
                var actual = p with { InnerRadiusMm = inner, OuterRadiusMm = outer };
                var mapped = target.MapPoint(new(sourceRadius, 0), actual);
                var restoredPoint = target.CreateInverseMapping(actual)(new(radius, 0));
                Require(Math.Abs(double.Hypot(mapped.X, mapped.Y) - radius) < 1e-5
                    && restoredPoint is {} point && Math.Abs(double.Hypot(point.X, point.Y) - sourceRadius) < 1e-5,
                    "schematic and simulated photo use fixed-pitch addresses in both directions");
                return new CalibrationRingObservation(CalibrationRingFitter.WideBandEdgeOrder[i], radius, [new(radius, 0)]);
            }).ToList();
            var boundary = Enumerable.Range(0, 8).Select(i => new CalibrationPoint(
                100 + 80 * Math.Cos(i * Math.PI / 4), 100 + 80 * Math.Sin(i * Math.PI / 4))).ToArray();
            var hole = boundary.Select(point => new CalibrationPoint(100 + (point.X - 100) / 8, 100 + (point.Y - 100) / 8)).ToArray();
            var photo = new CalibrationPhotoSession("AA==", boundary, hole, p == dvd ? 40 : 60, true, [],
                IsCoarseMode: true, RingObservations: marks, BandRaySelection: 5);
            var restored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(target, [], Photo: photo)));
            Require(restored.Target.Parameters == p, "session retains original settings including fine DVD pitch and BD range");
            var fit = CalibrationRingFitter.Fit(restored.Target, restored.Photo!.RingObservations!);
            Require(fit.Succeeded && fit.BestParameters is { } best
                && Math.Abs(best.InnerRadiusMm - inner) < 1e-5 && Math.Abs(best.OuterRadiusMm - outer) < 1e-5
                && best.PitchLinear == 0 && best.PitchQuadratic == 0 && best.PitchCubic == 0,
                "one ray recovers full-track endpoints with fixed pitch, including a cropped BD recording range");
            var intervals = CalibrationRingFitter.GetWideBandIntervals(target, marks);
            for (int i = 0; i < 2; ++i)
                Require(Math.Abs(intervals.InnerEdgeSpacingsMm[i]!.Value - intervals.PredictedInnerEdgeSpacingsMm[i]!.Value) < 1e-7,
                    "held-out spacings use the same aligned addresses as the fit");
            Throws<ArgumentException>(() => new CalibrationTarget(11, target.Id, p, target.Options, target.Cells, target.Artwork));
        }
        Require(!CalibrationWideBandPattern.Supports(bd with { DrawingEndSectorExclusive = bd.DrawingStartSector + 320 }),
            "an isolated BD recording patch cannot constrain both track ends");
    }

    private static void TestLocalWideBandEdges()
    {
        var seed = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200);
        var current = CalibrationTarget.Create(seed, artwork: CalibrationArtwork.WideAddressBands);
        var target = new CalibrationTarget(11, current.Id, seed, current.Options, current.Cells, current.Artwork);
        const double inner = 24.837775, outer = 58.020875;
        CalibrationRingObservation[] edges = new[] { .05, .28, .86 }.Select((q, i) =>
        {
            double r = Math.Sqrt(inner * inner + q * (outer * outer - inner * inner));
            var clicks = new[] { 306.0, 316, 331 }.Select(angle => OnCircle(r, angle)).ToArray();
            return new CalibrationRingObservation(i, CalibrationRingFitter.MeasureArc(clicks, 60), clicks);
        }).ToArray();
        foreach (var input in new[] { new[] { edges[2], edges[0] }, edges })
        {
            var fit = CalibrationRingFitter.Fit(target, input);
            Require(fit.Succeeded && fit.BestParameters is { } actual
                && Math.Abs(actual.InnerRadiusMm - inner) < 1e-7 && Math.Abs(actual.OuterRadiusMm - outer) < 1e-7,
                "two widely separated edges in one 25-degree clear area recover both simulated endpoints");
        }
        Require(!CalibrationRingFitter.Fit(target, edges[1..]).Succeeded, "a missing innermost edge cannot silently stand in for the pair");
        edges[1] = new(1, edges[1].RadiusMm + target.VisibleRings[1].WidthMm);
        Require(!CalibrationRingFitter.Fit(target, edges).Succeeded, "a middle band measured at its opposite edge fails the independent check");
    }

    private static void TestAlternatingBandEdges()
    {
        var seed = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200);
        var current = CalibrationTarget.Create(seed, artwork: CalibrationArtwork.WideAddressBands);
        var previous = new CalibrationTarget(11, current.Id, seed, current.Options, current.Cells, current.Artwork);
        const double inner = 24.837775, outer = 58.020875;
        foreach (var target in new[] { current, previous })
        {
            Require(CalibrationRingFitter.MeasurementCount(target) == 6, "both wide-band versions support six distinct edge identities");
            // IDs 0,1,2 remain the three inner edges in existing sessions.
            double[] progress = target.SchemaVersion == 11 ? [.05, .28, .86, .078, .32, .92] : [.035, .305, .705, .17, .51, .955];
            var edges = progress.Select((q, id) =>
            {
                double r = Math.Sqrt(inner * inner + q * (outer * outer - inner * inner));
                return new CalibrationRingObservation(id, r, [OnCircle(r, 320)]);
            }).ToArray();
            int[] order = [0, 3, 1, 4, 2, 5];
            Require(order.SequenceEqual(CalibrationRingFitter.WideBandEdgeOrder), "edges have a radial order independent of their saved IDs");
            var ordered = order.Select(i => edges[i]).ToArray();
            foreach (var sample in new[] { ordered.Reverse().ToArray(), new[] { edges[0], edges[2], edges[3], edges[5] } })
            {
                var fit = CalibrationRingFitter.Fit(target, sample);
                Require(fit.Succeeded && Math.Abs(fit.BestParameters!.InnerRadiusMm - inner) < 1e-7
                    && Math.Abs(fit.BestParameters.OuterRadiusMm - outer) < 1e-7, "four or six local edge observations recover the independent forward CD geometry");
            }
            Require(!CalibrationRingFitter.Fit(target, [edges[0], edges[2], edges[5]]).Succeeded,
                "incomplete first band does not silently use a three-edge fit");
            Require(CalibrationRingFitter.Fit(target, [edges[0], edges[2]]).Succeeded == (target.SchemaVersion == 11),
                "two-inner-edge legacy workflow applies only to v11");
            var intervals = CalibrationRingFitter.GetWideBandIntervals(target, edges);
            for (int i = 0; i < 5; i++)
                Require(Math.Abs(intervals.SegmentWidthsMm[i]!.Value - (ordered[i + 1].RadiusMm - ordered[i].RadiusMm)) < 1e-10,
                    "each dark or light width uses its own adjacent edges");
            for (int i = 0; i < 2; i++)
                Require(Math.Abs(intervals.InnerEdgeSpacingsMm[i]!.Value - (edges[i + 1].RadiusMm - edges[i].RadiusMm)) < 1e-10
                    && Math.Abs(intervals.PredictedInnerEdgeSpacingsMm[i]!.Value - intervals.InnerEdgeSpacingsMm[i]!.Value) < 1e-8,
                    "same-polarity spacing agrees with independent predictions from the measured outer edges");
            var sparse = CalibrationRingFitter.GetWideBandIntervals(target, [edges[0], edges[2], edges[3], edges[5]]);
            Require(sparse.SegmentWidthsMm[0] > 0 && sparse.SegmentWidthsMm[4] > 0
                && sparse.SegmentWidthsMm.Skip(1).Take(3).All(x => x is null) && sparse.InnerEdgeSpacingsMm.All(x => x is null),
                "missing edges leave widths unavailable, never zero");
            var noisy = edges.ToArray();
            noisy[1] = new(1, edges[1].RadiusMm + .12);
            var changed = CalibrationRingFitter.GetWideBandIntervals(target, noisy);
            Require(changed.PredictedInnerEdgeSpacingsMm.SequenceEqual(intervals.PredictedInnerEdgeSpacingsMm)
                && Math.Abs(changed.InnerEdgeSpacingsMm[0]!.Value - intervals.InnerEdgeSpacingsMm[0]!.Value - .12) < 1e-8,
                "moving a measured inner edge cannot also change its independent prediction");
            var noisyFit = CalibrationRingFitter.Fit(target, noisy);
            Require(noisyFit.Succeeded && noisyFit.BestParameters is not null, "modest manual error still permits coarse fitting");
            var fitted = noisyFit.BestParameters!;
            double di = 0, dout = 0;
            foreach (var edge in noisy)
            {
                double q = progress[edge.RingIndex];
                double r = Math.Sqrt((1 - q) * fitted.InnerRadiusMm * fitted.InnerRadiusMm + q * fitted.OuterRadiusMm * fitted.OuterRadiusMm);
                di += (r - edge.RadiusMm) * (1 - q) * fitted.InnerRadiusMm / r;
                dout += (r - edge.RadiusMm) * q * fitted.OuterRadiusMm / r;
            }
            Require(Math.Abs(di) < 1e-6 && Math.Abs(dout) < 1e-6, "the fit minimizes each absolute edge residual once");
            noisy[1] = new(1, edges[1].RadiusMm + 1);
            Require(!CalibrationRingFitter.Fit(target, noisy).Succeeded, "discordant interior edge must be remeasured");
            noisy[1] = new(1, edges[3].RadiusMm - .01);
            Require(!CalibrationRingFitter.Fit(target, noisy).Succeeded, "crossed dark/light boundaries are rejected");
            Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(target, [new(6, 50)]));
            Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(target, [new(5, 56, [new(55, 0)])]));
            Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(target, [new(5, 61, [new(61, 0)])]));
        }
        var regular = CalibrationTarget.Create(seed, artwork: CalibrationArtwork.FineCross);
        Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(regular, [new(1, 35, [new(35, 0)])]));
    }

    private static void TestFineArtwork()
    {
        var p = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200);
        var coarse = CalibrationTarget.Create(p);
        var fine = CalibrationTarget.Create(p, artwork: CalibrationArtwork.FineCross);
        var restored = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(new(fine, []))).Target;
        Require(!restored.HasAngularReference && coarse.HasAngularReference
            && restored.VisibleRings.SequenceEqual(coarse.VisibleRings), "the frozen artwork distinguishes a fine cross from its swept counterpart");
        double r = (fine.VisibleRings[3].RadiusMm + fine.VisibleRings[4].RadiusMm) / 2;
        var gap = OnCircle(r, 30);
        Require(coarse.Sample(gap.X, gap.Y) == 0 && restored.Sample(gap.X, gap.Y) == 255
            && restored.Sample(r, 0) == 0 && restored.Sample(0, -r) == 0, "clean fine target omits the sector while retaining the radial centrelines");
    }

    private static void TestEightCentimetreDvd()
    {
        var generated = new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 38, 714544, 133.3);
        var target = CalibrationTarget.Create(generated);
        Require(target.VisibleRings.Count == 8 && target.HasAngularReference,
            "a new 24–38 mm DVD scales the complete angular target to its recorded annulus");
        var actual = generated with { InnerRadiusMm = 24.4, OuterRadiusMm = 37.7 };
        var mapping = target.CreateMapping(actual);
        var measured = new[] { 0, 3, 7 }.Select(i =>
        {
            var ring = target.VisibleRings[i];
            // Generate measurements independently from the inner half-width of the known stroke.
            var p = mapping(new(ring.RadiusMm - ring.WidthMm / 2, 0));
            double radius = double.Hypot(p.X, p.Y);
            CalibrationPoint[] points = [OnCircle(radius, 19), OnCircle(radius, 26), OnCircle(radius, 34)];
            return new CalibrationRingObservation(i, CalibrationRingFitter.MeasureArc(points, 40), points);
        }).ToArray();
        var fit = CalibrationRingFitter.Fit(target, measured);
        Require(fit.Succeeded && fit.BestParameters is { } best
            && Math.Abs(best.InnerRadiusMm - actual.InnerRadiusMm) < 1e-7
            && Math.Abs(best.OuterRadiusMm - actual.OuterRadiusMm) < 1e-7,
            "three inner edges, each sampled on one side, recover both endpoints of an 8 cm DVD");

        var old = new CalibrationTarget(6, target.Id, generated, target.Options, target.Cells);
        Require(!old.HasAngularReference && old.VisibleRings.Count == 5 && old.VisibleRings[0].RadiusMm == old.RingRadiiMm[4],
            "real 8 cm records retain the original five photographed rings and identities");
        var clipped = new CalibrationTarget(6, target.Id, generated with { InnerRadiusMm = old.VisibleRings[0].RadiusMm }, target.Options, target.Cells);
        Require(clipped.VisibleRings.Count == 4 && clipped.VisibleRings[0].RadiusMm == old.VisibleRings[1].RadiusMm,
            "a stroke bisected by a custom recording boundary is excluded from ring measurements");
    }

    private static void TestArcSamples()
    {
        // Three points along one short arc, including a wrap through the -180/180 degree seam.
        CalibrationPoint[] points = [OnCircle(30.09, 175), OnCircle(29.97, -179), OnCircle(30.03, -174)];
        double radius = CalibrationRingFitter.MeasureArc(points, 40);
        Require(Math.Abs(radius - 30.03) < 1e-10, "same-side marks use a fixed centre and the mean radial distance");
        Throws<ArgumentException>(() => CalibrationRingFitter.MeasureArc(points[..2], 40));
        Throws<ArgumentException>(() => CalibrationRingFitter.MeasureArc([points[0], points[0], points[1]], 40));
        Throws<ArgumentException>(() => CalibrationRingFitter.MeasureArc(
            [OnCircle(30, 0), OnCircle(30, 2), OnCircle(30, 5)], 40));
        Throws<ArgumentException>(() => CalibrationRingFitter.MeasureArc([points[0], points[1], OnCircle(31, 160)], 40));
        Throws<ArgumentException>(() => CalibrationRingFitter.MeasureArc([points[0], points[1], new(double.NaN, 0)], 40));
        Throws<ArgumentException>(() => CalibrationRingFitter.MeasureArc([points[0], points[1], new(3, 0)], 40));
        Throws<ArgumentException>(() => CalibrationRingFitter.MeasureArc([points[0], points[1], new(41, 0)], 40));

        CalibrationTarget target = CalibrationTarget.Create(new(CalibrationDiscKind.Dvd, 24, 38, 714544, 133.3));
        CalibrationRingFitter.ValidateObservations(target, [new(2, radius, points)], 40);
        Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(target, [new(2, 31, points)], 40));
        CalibrationPoint[] crossesRim = [OnCircle(39.8, 20), OnCircle(40.1, 25), OnCircle(39.8, 31)];
        Throws<ArgumentException>(() => CalibrationRingFitter.ValidateObservations(target, [new(2, 39.9, crossesRim)], 40));
    }

    private static CalibrationPoint OnCircle(double radius, double degrees)
        => new(radius * Math.Cos(degrees * Math.PI / 180), radius * Math.Sin(degrees * Math.PI / 180));

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("Calibration ring check failed: " + message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
