using DiscImageStudio.Core.Calibration;

internal static class CalibrationBoundaryFitterTests
{
    internal static void Run()
    {
        foreach (var kind in Enum.GetValues<CalibrationDiscKind>())
        {
            foreach (var offsets in new[] { (.005, .005), (-.005, -.005), (.005, -.005), (-.005, .005), (0d, 0d) })
                CheckLocalSegments(kind, offsets.Item1, offsets.Item2);
        }
        CheckContinuousBoundaries();
        CheckDrawingOrderIndependence();
        CheckWideAnglePieces();
        CheckWideNearCircularPieces();
        CheckWavyBoundaryWindows();
        CheckWavyBoundaryNoise();
        CheckNativeDvdNoiseAndDuplication();
        CheckInputGuards();
        Console.WriteLine("half-disc boundary fitting: independent CD/DVD/BD spirals, separated 40-degree pieces, sign reversals, straight edges and draft guards passed");
    }

    private static CalibrationParameters Parameters(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Cd => new(kind, 24, 58, 359848, 1200),
        CalibrationDiscKind.Dvd => new(kind, 24, 58, 2297888, 133.3),
        _ => new(kind, 24, 58, 12219392, 74.5),
    };

    private static void CheckLocalSegments(CalibrationDiscKind kind, double innerOffset, double outerOffset)
    {
        var actual = Parameters(kind);
        var generated = actual with { InnerRadiusMm = actual.InnerRadiusMm + innerOffset, OuterRadiusMm = actual.OuterRadiusMm + outerOffset };
        var segments = IndependentSegments(generated, actual);
        var target = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.HalfDisc);
        var result = CalibrationBoundaryFitter.Fit(target, segments);
        string name = $"{kind} {innerOffset:+0.000;-0.000;0}/{outerOffset:+0.000;-0.000;0}";
        Check(result.Succeeded, name + " succeeds: " + string.Join(" | ", result.Warnings));
        Near(actual.InnerRadiusMm, result.BestParameters!.InnerRadiusMm, 2e-6, name + " inner");
        Near(actual.OuterRadiusMm, result.BestParameters.OuterRadiusMm, 2e-6, name + " outer");
        Check(segments.SelectMany(s => s.Points).All(p => Math.Abs(Math.Atan2(p.X, -p.Y)) < .36),
            name + " all evidence lies in one 40-degree window");
        // Segment order and point order cannot manufacture a different inter-segment unwrap.
        if (kind == CalibrationDiscKind.Dvd && innerOffset == .005 && outerOffset == .005)
        {
            var reversed = segments.Reverse().Select(s => s with { Points = s.Points.Reverse().ToArray() }).ToArray();
            var again = CalibrationBoundaryFitter.Fit(target, reversed);
            Check(again.Succeeded, "reordered disconnected pieces still succeed");
            Near(actual.InnerRadiusMm, again.BestParameters!.InnerRadiusMm, 2e-6, "order-independent inner");
            Near(actual.OuterRadiusMm, again.BestParameters.OuterRadiusMm, 2e-6, "order-independent outer");
        }
    }

    /// <summary>
    /// Independent physical construction from Archimedean arc primitives. No production
    /// calibration mapping, prediction or fitter geometry generates these observations.
    /// Black/white edges are picked where the accumulated phase enters one visible window;
    /// invisible complete turns are deliberately absent from the input.
    /// </summary>
    private static CalibrationBoundarySegment[] IndependentSegments(CalibrationParameters generated, CalibrationParameters actual,
        bool wavy = false, double viewingDegrees = 0, bool subdivide = true,
        double viewingHalfWidthDegrees = 20, bool closePair = false)
    {
        double bg = Growth(generated), ba = Growth(actual);
        double Phase(double radius)
        {
            double source;
            if (actual.Kind == CalibrationDiscKind.Cd)
            {
                double q = (radius * radius - actual.InnerRadiusMm * actual.InnerRadiusMm)
                    / (actual.OuterRadiusMm * actual.OuterRadiusMm - actual.InnerRadiusMm * actual.InnerRadiusMm);
                source = Math.Sqrt(generated.InnerRadiusMm * generated.InnerRadiusMm
                    + q * (generated.OuterRadiusMm * generated.OuterRadiusMm - generated.InnerRadiusMm * generated.InnerRadiusMm));
            }
            else
            {
                double length = Arc(actual.InnerRadiusMm, radius, ba);
                source = Math.Sqrt(generated.InnerRadiusMm * generated.InnerRadiusMm + 2 * bg * length);
                for (int iteration = 0; iteration < 5; iteration++)
                    source -= (Arc(generated.InnerRadiusMm, source, bg) - length) * bg / Math.Sqrt(source * source + bg * bg);
            }
            double profile = wavy ? Math.PI / 2 * Math.Sin(6 * Math.PI
                * (source - generated.InnerRadiusMm) / (generated.OuterRadiusMm - generated.InnerRadiusMm)) : 0;
            return (radius - actual.InnerRadiusMm) / ba - (source - generated.InnerRadiusMm) / bg + profile;
        }
        const int count = 8193;
        double first = actual.InnerRadiusMm + .2, last = actual.OuterRadiusMm - .2;
        double step = (last - first) / (count - 1);
        double limit = viewingHalfWidthDegrees * Math.PI / 180;
        double viewing = viewingDegrees * Math.PI / 180;
        var intervals = new List<(double First, double Last)>();
        double? beginning = null;
        for (int i = 0; i < count; i++)
        {
            double radius = first + step * i;
            double wrapped = HalfTurn(Phase(radius) - viewing);
            bool visible = Math.Abs(wrapped) <= limit;
            if (visible && beginning is null) beginning = radius;
            if ((!visible || i == count - 1) && beginning is double start)
            {
                double end = visible ? radius : radius - step;
                if (end - start > .05) intervals.Add((start, end));
                beginning = null;
            }
        }
        var pieces = new List<(double First, double Last)>();
        int subdivisions = subdivide ? Math.Max(1, (int)Math.Ceiling(8d / intervals.Count)) : 1;
        foreach (var interval in intervals)
            for (int j = 0; j < subdivisions; j++)
            {
                double start = interval.First + (interval.Last - interval.First) * j / subdivisions;
                double end = interval.First + (interval.Last - interval.First) * (j + 1) / subdivisions;
                pieces.Add((start, end));
            }
        if (pieces.Count > 9)
            pieces = Enumerable.Range(0, 9).Select(i => pieces[(int)Math.Round(i * (pieces.Count - 1d) / 8)]).ToList();
        return pieces.Select((piece, index) => new CalibrationBoundarySegment(index,
            Enumerable.Range(0, wavy ? 7 : 5).Select(i =>
            {
                double fraction = closePair && i == 1 ? .001 : (double)i / (wavy ? 6 : 4);
                double radius = piece.First + (piece.Last - piece.First) * fraction;
                double angle = -Math.PI / 2 + viewing + HalfTurn(Phase(radius) - viewing);
                return new CalibrationPoint(radius * Math.Cos(angle), radius * Math.Sin(angle));
            }).ToArray())).Where(segment =>
            {
                // Do not ask a person to mark a sub-pixel sliver clipped by the
                // drawing edge. These are excluded by the production input guard too.
                double meanX = segment.Points.Average(p => p.X), meanY = segment.Points.Average(p => p.Y);
                return segment.Points.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y))
                    - segment.Points.Min(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)) >= .03
                    && segment.Points.Average(p => (p.X - meanX) * (p.X - meanX) + (p.Y - meanY) * (p.Y - meanY)) >= .35 * .35;
            }).ToArray();
    }

    private static void CheckInputGuards()
    {
        var actual = Parameters(CalibrationDiscKind.Dvd);
        var generated = actual with { InnerRadiusMm = 24.005, OuterRadiusMm = 58.005 };
        var target = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.HalfDisc);
        var segments = IndependentSegments(generated, actual);
        Check(!CalibrationBoundaryFitter.Fit(target, segments.Take(3).ToArray()).Succeeded, "three pieces are insufficient");
        Check(!CalibrationBoundaryFitter.Fit(target, segments.Select(s => s with { Points = s.Points.Take(1).ToArray() }).ToArray()).Succeeded,
            "one ray intersection per piece cannot establish local direction");
        Check(!CalibrationBoundaryFitter.Fit(target, segments, new(RadiusRangeMm: .1)).Succeeded,
            "broad unbounded alias search is rejected");
        CalibrationBoundaryFitter.ValidateSegments(target, [new(0, []), new(1, [new(0, -30)])]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { CalibrationBoundaryFitter.Fit(target, segments, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("boundary fitting observes cancellation");
    }

    private static void CheckContinuousBoundaries()
    {
        var actual = Parameters(CalibrationDiscKind.Dvd);
        foreach (var offset in new[] { (0d, 0d), (.0001, .0001), (.0003, -.0003), (.0005, .0005) })
        {
            var generated = actual with { InnerRadiusMm = 24 + offset.Item1, OuterRadiusMm = 58 + offset.Item2 };
            var target = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.HalfDisc);
            double bg = Growth(generated), ba = Growth(actual);
            var points = Enumerable.Range(0, 19).Select(i =>
            {
                double radius = 24.4 + 33.2 * i / 18;
                double length = Arc(actual.InnerRadiusMm, radius, ba);
                double source = Math.Sqrt(generated.InnerRadiusMm * generated.InnerRadiusMm + 2 * bg * length);
                for (int j = 0; j < 5; j++)
                    source -= (Arc(generated.InnerRadiusMm, source, bg) - length) * bg / Math.Sqrt(source * source + bg * bg);
                double angle = -Math.PI / 2 + .2 + (radius - actual.InnerRadiusMm) / ba - (source - generated.InnerRadiusMm) / bg;
                return new CalibrationPoint(radius * Math.Cos(angle), radius * Math.Sin(angle));
            }).ToArray();
            CalibrationBoundarySegment[] segments = [new(0, points)];
            Check(CalibrationBoundaryFitter.HasEnoughPoints(segments), "one complete continuous edge enables fitting");
            var fit = CalibrationBoundaryFitter.Fit(target, segments, new(RadiusRangeMm: .01));
            string name = $"single continuous edge {offset.Item1:R}/{offset.Item2:R}";
            Check(fit.Succeeded, name + " succeeds: " + string.Join(" | ", fit.Warnings));
            Near(actual.InnerRadiusMm, fit.BestParameters!.InnerRadiusMm, 2e-6, name + " inner");
            Near(actual.OuterRadiusMm, fit.BestParameters.OuterRadiusMm, 2e-6, name + " outer");
            // Additional short pieces are valid evidence; no forced segmentation of
            // an already visible straight or bent edge is necessary.
            var additional = segments.Concat(new[] { new CalibrationBoundarySegment(1, points.Skip(3).Take(3).ToArray()) }).ToArray();
            var combined = CalibrationBoundaryFitter.Fit(target, additional, new(RadiusRangeMm: .01));
            Check(combined.Succeeded, "a long edge can be accompanied by another short visible piece");
            Near(actual.InnerRadiusMm, combined.BestParameters!.InnerRadiusMm, 2e-6, "continuous plus partial inner");
            CalibrationBoundarySegment[] twoExtended = [new(0, points.Take(7).ToArray()), new(1, points.Skip(12).ToArray())];
            var two = CalibrationBoundaryFitter.Fit(target, twoExtended, new(RadiusRangeMm: .01));
            Check(two.Succeeded, "two extended visible pieces are enough: " + string.Join(" | ", two.Warnings));
            Near(actual.InnerRadiusMm, two.BestParameters!.InnerRadiusMm, 2e-6, "two long pieces inner");
            Near(actual.OuterRadiusMm, two.BestParameters.OuterRadiusMm, 2e-6, "two long pieces outer");
        }
    }

    private static void CheckWavyBoundaryWindows()
    {
        var actual = Parameters(CalibrationDiscKind.Dvd);
        foreach (var offset in new[] { (0d, 0d), (.005, .005), (-.005, -.005), (.005, -.005), (-.005, .005), (.0001, .0001) })
            foreach (double viewing in new[] { 0d, 45d, 90d, 135d })
            {
                var generated = actual with { InnerRadiusMm = 24 + offset.Item1, OuterRadiusMm = 58 + offset.Item2 };
                var target = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.WavyBoundary);
                var pieces = IndependentSegments(generated, actual, wavy: true, viewingDegrees: viewing, subdivide: false);
                var fit = CalibrationBoundaryFitter.Fit(target, pieces, new(RadiusRangeMm: .01));
                string name = $"wavy {offset.Item1:R}/{offset.Item2:R} in {viewing} degree window";
                Check(fit.Succeeded, name + " succeeds: " + string.Join(" | ", fit.Warnings));
                Near(actual.InnerRadiusMm, fit.BestParameters!.InnerRadiusMm, 2e-6, name + " inner");
                Near(actual.OuterRadiusMm, fit.BestParameters.OuterRadiusMm, 2e-6, name + " outer");
            }
    }

    private static void CheckDrawingOrderIndependence()
    {
        var parameters = Parameters(CalibrationDiscKind.Dvd);
        var target = CalibrationTarget.Create(parameters, artwork: CalibrationArtwork.WavyBoundary);
        // A known source boundary with several angular reversals and seam crossings.
        // The disc and generation parameters match, so this fixture is independent
        // of the production forward mapping and does not assume a winding error.
        var points = Enumerable.Range(0, 31).Select(i =>
        {
            double radius = 24.3 + 33.4 * i / 30;
            double phase = Math.PI / 2 * Math.Sin(6 * Math.PI * (radius - 24) / 34);
            double angle = 2.9 + phase;
            return new CalibrationPoint(radius * Math.Cos(angle), radius * Math.Sin(angle));
        }).ToArray();
        var options = new CalibrationFitOptions(RadiusRangeMm: .005);
        var expected = CalibrationBoundaryFitter.Fit(target, [new(0, points)], options);
        Check(expected.Succeeded, "ordered wavy boundary fits before changing drawing order: " + string.Join(" | ", expected.Warnings));
        Near(parameters.InnerRadiusMm, expected.BestParameters!.InnerRadiusMm, 2e-6, "ordered wavy inner");
        Near(parameters.OuterRadiusMm, expected.BestParameters.OuterRadiusMm, 2e-6, "ordered wavy outer");
        int middle = points.Length / 2;
        var drawingOrders = new[]
        {
            points.Reverse().ToArray(),
            new[] { points[middle] }.Concat(points.Take(middle).Reverse()).Concat(points.Skip(middle + 1)).ToArray(),
        };
        foreach (var drawingOrder in drawingOrders)
        {
            var before = drawingOrder.ToArray();
            var result = CalibrationBoundaryFitter.Fit(target, [new(0, drawingOrder)], options);
            Check(result.Succeeded == expected.Succeeded && result.BestParameters == expected.BestParameters
                && result.RmsErrorMm == expected.RmsErrorMm && result.RotationRadians == expected.RotationRadians
                && result.Candidates.SequenceEqual(expected.Candidates) && result.Warnings.SequenceEqual(expected.Warnings),
                "reversed or centre-first drawing produces the same fit as radial input");
            Check(drawingOrder.SequenceEqual(before), "fitting never changes the saved visible polyline order");
        }
    }

    private static void CheckWideAnglePieces()
    {
        var parameters = Parameters(CalibrationDiscKind.Dvd);
        var target = CalibrationTarget.Create(parameters, artwork: CalibrationArtwork.WavyBoundary);
        var options = new CalibrationFitOptions(RadiusRangeMm: .005);
        foreach (double rotation in new[] { .3, 2.9 })
        {
            // Independent source geometry: each 2.4 mm radial piece follows one
            // sine zero crossing for about 111 degrees. None qualifies as a long
            // radial boundary; the rotated fixture also crosses the +/- pi seam.
            var pieces = new[] { 1, 2, 4, 5 }.Select((crossing, id) =>
                new CalibrationBoundarySegment(id, Enumerable.Range(0, 9).Select(i =>
                {
                    double radius = 24 + 34d * crossing / 6 - 1.2 + .3 * i;
                    double angle = rotation + Math.PI / 2 * Math.Sin(6 * Math.PI * (radius - 24) / 34);
                    return new CalibrationPoint(radius * Math.Cos(angle), radius * Math.Sin(angle));
                }).ToArray())).ToArray();
            Check(pieces.All(s =>
                Math.Abs(2 * Math.PI / 2 * Math.Sin(6 * Math.PI * 1.2 / 34)) > Math.PI / 2
                && s.Points.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y))
                    - s.Points.Min(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)) < 3.4),
                "wide-angle fixtures have more than 90 degrees but less than 10 percent radial coverage per piece");
            if (rotation == 2.9)
                Check(pieces.All(s => s.Points.Any(p => Math.Atan2(p.Y, p.X) > 2)
                    && s.Points.Any(p => Math.Atan2(p.Y, p.X) < -2)),
                    "wide-angle fixture crosses the angular seam within each piece");
            var expected = CalibrationBoundaryFitter.Fit(target, pieces, options);
            Check(expected.Succeeded, "wide-angle short radial pieces fit: " + string.Join(" | ", expected.Warnings));
            Near(parameters.InnerRadiusMm, expected.BestParameters!.InnerRadiusMm, 2e-6, "wide-angle inner");
            Near(parameters.OuterRadiusMm, expected.BestParameters.OuterRadiusMm, 2e-6, "wide-angle outer");
            foreach (bool fromMiddle in new[] { false, true })
            {
                var reordered = pieces.Reverse().Select(s => s with
                {
                    Points = fromMiddle
                        ? new[] { s.Points[4] }.Concat(s.Points.Take(4).Reverse()).Concat(s.Points.Skip(5)).ToArray()
                        : s.Points.Reverse().ToArray(),
                }).ToArray();
                var before = reordered.Select(s => s.Points.ToArray()).ToArray();
                var again = CalibrationBoundaryFitter.Fit(target, reordered, options);
                Check(again.Succeeded, "wide-angle reverse or centre-first drawing fits");
                Near(expected.BestParameters.InnerRadiusMm, again.BestParameters!.InnerRadiusMm, 1e-9,
                    "wide-angle drawing-order independent inner");
                Near(expected.BestParameters.OuterRadiusMm, again.BestParameters.OuterRadiusMm, 1e-9,
                    "wide-angle drawing-order independent outer");
                Near(expected.RmsErrorMm, again.RmsErrorMm, 1e-7, "wide-angle drawing-order independent residual");
                Check(reordered.Select((s, i) => s.Points.SequenceEqual(before[i])).All(s => s),
                    "wide-angle fitting preserves saved point order");
            }

            CalibrationPoint Polar(double radius, double angle)
                => new(radius * Math.Cos(angle), radius * Math.Sin(angle));
            var missing = pieces.ToArray();
            missing[0] = new(0, [Polar(29, 0), Polar(29.2, Math.PI * .98), Polar(29.4, Math.PI * .99)]);
            var rejected = CalibrationBoundaryFitter.Fit(target, missing, options);
            Check(!rejected.Succeeded && rejected.Candidates.Count == 0
                && rejected.Warnings.Any(w => w.Contains("接近半圈")),
                "a near-half-turn point gap is rejected even in a short radial piece");
        }
    }

    private static void CheckWideNearCircularPieces()
    {
        var actual = Parameters(CalibrationDiscKind.Dvd);
        var generated = actual with { InnerRadiusMm = 24.01, OuterRadiusMm = 58.01 };
        var target = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.WavyBoundary);
        var clean = IndependentSegments(generated, actual, wavy: true, viewingDegrees: 270,
            subdivide: false, viewingHalfWidthDegrees: 55, closePair: true);
        double Radius(CalibrationPoint p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
        double Span(CalibrationBoundarySegment s)
        {
            double first = Math.Atan2(s.Points[0].Y, s.Points[0].X);
            var angles = s.Points.Select(p => Math.Atan2(Math.Sin(Math.Atan2(p.Y, p.X) - first),
                Math.Cos(Math.Atan2(p.Y, p.X) - first))).ToArray();
            return angles.Max() - angles.Min();
        }
        Check(clean.Count(s => Span(s) > Math.PI / 2
            && s.Points.Max(Radius) - s.Points.Min(Radius) < 2) >= 4,
            "independent winding fixture includes four wide-angle narrow radial pieces");
        var noisy = clean.Select(s => s with
        {
            Points = s.Points.Select((p, i) =>
            {
                double radius = Radius(p), noise = i % 2 == 0 ? .003 : -.003;
                return new CalibrationPoint(p.X * (radius + noise) / radius, p.Y * (radius + noise) / radius);
            }).ToArray(),
        }).ToArray();
        Check(noisy.Any(s => Radius(s.Points[0]) > Radius(s.Points[1])),
            "small radial click noise reverses the radius order of a close point pair");
        Check(noisy.Any(s => s.Points.Any(p => Math.Atan2(p.Y, p.X) > 2)
            && s.Points.Any(p => Math.Atan2(p.Y, p.X) < -2)),
            "near-circular fixture crosses the angular seam");
        var options = new CalibrationFitOptions(RadiusRangeMm: .015);
        var fit = CalibrationBoundaryFitter.Fit(target, noisy, options);
        Check(fit.Succeeded, "wide near-circular pieces tolerate small radial picking errors: " + string.Join(" | ", fit.Warnings));
        Near(actual.InnerRadiusMm, fit.BestParameters!.InnerRadiusMm, .0002, "wide near-circular inner");
        Near(actual.OuterRadiusMm, fit.BestParameters.OuterRadiusMm, .0002, "wide near-circular outer");
        var reversed = noisy.Select(s => s with { Points = s.Points.Reverse().ToArray() }).ToArray();
        var before = reversed.Select(s => s.Points.ToArray()).ToArray();
        var again = CalibrationBoundaryFitter.Fit(target, reversed, options);
        Check(again.Succeeded && again.BestParameters == fit.BestParameters
            && again.RmsErrorMm == fit.RmsErrorMm && again.RotationRadians == fit.RotationRadians,
            "reversing noisy near-circular drawing leaves the fitted candidate unchanged");
        Check(reversed.Select((s, i) => s.Points.SequenceEqual(before[i])).All(s => s),
            "fitting noisy near-circular pieces does not reorder the saved drawing");
    }

    private static void CheckWavyBoundaryNoise()
    {
        var actual = Parameters(CalibrationDiscKind.Dvd);
        foreach (var sample in new[] { (0d, 0d, 90d), (.005, .005, 90d), (.005, -.005, 0d) })
        {
            var generated = actual with { InnerRadiusMm = 24 + sample.Item1, OuterRadiusMm = 58 + sample.Item2 };
            var target = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.WavyBoundary);
            var clean = IndependentSegments(generated, actual, wavy: true, viewingDegrees: sample.Item3, subdivide: false);
            var random = new Random(491603);
            double Noise() => Math.Sqrt(-2 * Math.Log(Math.Max(1e-15, random.NextDouble())))
                * Math.Cos(Math.Tau * random.NextDouble()) * .05;
            var noisy = clean.Select(s => s with { Points = s.Points.Select(p =>
                new CalibrationPoint(p.X + Noise(), p.Y + Noise())).ToArray() }).ToArray();
            var fit = CalibrationBoundaryFitter.Fit(target, noisy, new(RadiusRangeMm: .01));
            Check(fit.Succeeded, $"noisy wavy {sample}: " + string.Join(" | ", fit.Warnings));
            Near(actual.InnerRadiusMm, fit.BestParameters!.InnerRadiusMm, .0003, "noisy wavy inner");
            Near(actual.OuterRadiusMm, fit.BestParameters.OuterRadiusMm, .0003, "noisy wavy outer");
        }
    }

    private static void CheckNativeDvdNoiseAndDuplication()
    {
        // Independent fixed fixture generated by walking the native DVD arc lengths,
        // with the Verbatim AZO 43533 pitch coefficients retained in both geometries.
        const double inner = 23.991460627731737, outer = 57.964893657925366;
        var generated = new CalibrationParameters(CalibrationDiscKind.Dvd, inner + .005, outer + .005,
            2297888, 133.3, -3.9256854319227684e-5, 6.964795915429256e-5, -4.670918950137523e-5);
        var target = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.HalfDisc);
        var options = new CalibrationFitOptions(generated with { PitchLinear = .01, PitchQuadratic = 0, PitchCubic = 0 },
            RadiusRangeMm: .01, FitDvdPitch: true);
        var source = NativeDvdSegments();
        foreach (int count in new[] { 3, 5 })
        {
            var random = new Random(491500);
            double Noise() => Math.Sqrt(-2 * Math.Log(Math.Max(1e-15, random.NextDouble())))
                * Math.Cos(Math.Tau * random.NextDouble()) * .05;
            var noisy = source.Select(s => s with { Points = (count == 3
                    ? new[] { s.Points[0], s.Points[2], s.Points[4] } : s.Points)
                .Select(p => new CalibrationPoint(p.X + Noise(), p.Y + Noise())).ToArray() }).ToArray();
            var fit = CalibrationBoundaryFitter.Fit(target, noisy, options);
            Check(fit.Succeeded, $"six native pieces with {count} points and 0.05 mm noise: " + string.Join(" | ", fit.Warnings));
            Near(inner, fit.BestParameters!.InnerRadiusMm, .0002, "noisy native inner");
            Near(outer, fit.BestParameters.OuterRadiusMm, .0002, "noisy native outer");
            Check(fit.BestParameters.PitchLinear == generated.PitchLinear
                && fit.BestParameters.PitchQuadratic == generated.PitchQuadratic
                && fit.BestParameters.PitchCubic == generated.PitchCubic,
                "middle fitting keeps every written DVD pitch coefficient despite options");
        }
        var ambiguousRandom = new Random(494001);
        double AmbiguousNoise() => Math.Sqrt(-2 * Math.Log(Math.Max(1e-15, ambiguousRandom.NextDouble())))
            * Math.Cos(Math.Tau * ambiguousRandom.NextDouble()) * .08;
        var ambiguous = new[] { 0, 1, 4, 5 }.Select(i => source[i]).Select((s, i) => new CalibrationBoundarySegment(i,
            new[] { s.Points[0], s.Points[2], s.Points[4] }
                .Select(p => new CalibrationPoint(p.X + AmbiguousNoise(), p.Y + AmbiguousNoise())).ToArray())).ToArray();
        var rejected = CalibrationBoundaryFitter.Fit(target, ambiguous, options);
        var repeated = Enumerable.Range(0, 4).SelectMany(n => ambiguous.Select(s => s with { Id = s.Id + n * 4 })).ToArray();
        var stillRejected = CalibrationBoundaryFitter.Fit(target, repeated, options);
        Check(!rejected.Succeeded && !stillRejected.Succeeded, "copying uncertain segments cannot remove winding ambiguity");
        Near(rejected.RmsErrorMm, stillRejected.RmsErrorMm, 1e-7, "duplicate geometry leaves measured residual unchanged");
        Check(rejected.BestParameters is null && stillRejected.BestParameters is null,
            "ambiguous candidates cannot be accidentally applied as calibrated radii");
    }

    private static double Growth(CalibrationParameters parameters)
    {
        if (parameters.Kind == CalibrationDiscKind.Cd)
            return (parameters.OuterRadiusMm * parameters.OuterRadiusMm - parameters.InnerRadiusMm * parameters.InnerRadiusMm)
                / (2 * parameters.TrackLengthMm);
        double low = 1e-12, high = 1;
        for (int i = 0; i < 100; i++)
        {
            double middle = (low + high) / 2;
            if (Arc(parameters.InnerRadiusMm, parameters.OuterRadiusMm, middle) > parameters.TrackLengthMm) low = middle;
            else high = middle;
        }
        return (low + high) / 2;
    }

    private static double Arc(double first, double last, double b)
    {
        double Primitive(double r) => .5 * (r * Math.Sqrt(r * r + b * b) + b * b * Math.Log(r + Math.Sqrt(r * r + b * b)));
        return (Primitive(last) - Primitive(first)) / b;
    }
    private static double HalfTurn(double angle) => .5 * Math.Atan2(Math.Sin(2 * angle), Math.Cos(2 * angle));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance, string message)
        => Check(Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected:R}, actual {actual:R}");
    private static CalibrationBoundarySegment[] NativeDvdSegments() =>
    [
        new(0, [
            new(-23.50531263643005, 7.6373390416883495),
            new(-24.502096843352177, 3.880750895311925),
            new(-24.90057009853322, 3.0494403468113e-15),
            new(-24.68638609053677, -3.9099394445850804),
            new(-23.860225087186127, -7.752657087377283),
        ]),
        new(1, [
            new(-27.40284620214961, 8.903724463919506),
            new(-28.570291372617564, 4.525089608960964),
            new(-29.040347076790344, 3.556416809371949e-15),
            new(-28.79588519691869, -4.560820160563562),
            new(-27.8373030753408, -9.044888059183076),
        ]),
        new(2, [
            new(-32.142532381650994, 10.4437418576024),
            new(-33.515604355953414, 5.308350238056276),
            new(-34.07070785127044, 4.1724583314742945e-15),
            new(-33.78747594228597, -5.351410467097693),
            new(-32.666110224073215, -10.613862611116499),
        ]),
        new(3, [
            new(-37.79656267645064, 12.280847663480351),
            new(-39.41155737992286, 6.242177457937435),
            new(-40.06460615646356, 4.906499168861244e-15),
            new(-39.73173257069894, -6.292888226334296),
            new(-38.41316245252275, -12.481193075418966),
        ]),
        new(4, [
            new(-44.37248963359026, 14.417495852843938),
            new(-46.264173347684356, 7.327525202750321),
            new(-47.026295313407616, 5.759060203132281e-15),
            new(-46.63103310918235, -7.38563008075276),
            new(-45.07899373264135, -14.647052950094908),
        ]),
        new(5, [
            new(-51.81060553731652, 16.834286212827823),
            new(-54.00988202655086, 8.554324936769497),
            new(-54.88984437081053, 6.722067221440948e-15),
            new(-54.41872336678057, -8.619079043623298),
            new(-52.59795773024024, -17.090112448180896),
        ]),
    ];

}
