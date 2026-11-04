using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.BluRay;
using DvdImageSolver.Encoding;
using System.Reflection;
using System.Text.Json.Nodes;

internal static class CalibrationCoreTests
{
    internal static void Run()
    {
        CalibrationStagedArtworkTests.Run();
        TestTargetAndSession();
        TestDraftsAndInsufficientInput();
        foreach (CalibrationDiscKind kind in Enum.GetValues<CalibrationDiscKind>())
        {
            TestEncoderGeometry(kind);
            TestOneArmWithUnknownSourceRadii(kind);
            TestNoisyCurveAndArmIdentity(kind);
            TestMappedOutline(kind);
            TestBoundarySignalAndWideRecovery(kind);
        }
        Console.WriteLine("calibration-core: concentric guides, continuous single-arm fitting, winding, sessions and cancellation passed");
    }

    private static CalibrationParameters Parameters(CalibrationDiscKind kind = CalibrationDiscKind.Cd)
        => kind switch
        {
            CalibrationDiscKind.Cd => new(kind, 24.5, 56.8, 359849, 1200),
            CalibrationDiscKind.BluRay => new(kind, 24, 58, 12_219_392, 74.5),
            _ => new(kind, 24, 58, 2295104, 133.33),
        };

    private static void TestTargetAndSession()
    {
        CalibrationTarget target = new(6, Guid.NewGuid().ToString("N"), Parameters(), new CalibrationPatternOptions(), []);
        Check(target.SchemaVersion == 6 && target.Cells.Count == 0 && target.Groups.Count == 4
            && target.ControlCells.Count == 4 && target.ControlPoints.Count == 8,
            "only four radial arms provide control geometry; rings are observation guides");
        Check(target.RingRadiiMm.Zip(new[] { 490.0, 570, 650, 730, 810, 890, 970, 1050, 1130 })
                .All(pair => Math.Abs(pair.First - pair.Second * target.Parameters.OuterRadiusMm / 1200) < 1e-12),
            "the nine source circles scale with the ordinary CD image mapping");
        foreach (double radius in target.RingRadiiMm.Where(radius => radius >= target.Parameters.InnerRadiusMm && radius <= target.Parameters.OuterRadiusMm))
        foreach (double angle in new[] { -3.0, -1.1, 0.0, 0.75, 2.99 })
        {
            CalibrationPoint point = Polar(radius, angle);
            Check(target.Sample(point.X, point.Y) == 0, "circle stays complete at all angles");
        }
        double between = (target.RingRadiiMm[4] + target.RingRadiiMm[5]) / 2;
        CalibrationPoint blank = Polar(between, Math.PI / 4);
        Check(target.Sample(blank.X, blank.Y) == 255, "space between guides is white");
        Check(target.Sample(0, 0) == 255 && target.Sample(60, 0) == 255, "hole and exterior stay clear");
        foreach (CalibrationGroup group in target.Groups)
        {
            Near(-90 + group.Index * 90, group.AngleDegrees, 1e-12, "four clockwise arm identities");
            Check(group.ArcRadiiMm.Count == 0, "rings have no arm identity or fitting observations");
            string id = group.Id + "-spine";
            CalibrationCell cell = target.ControlCells.Single(cell => cell.Id == id);
            Check(cell.Corners.Count == 2 && !cell.IsOutlineClosed && cell.RadialLine is not null, "two-ended open arm");
            IReadOnlyList<IReadOnlyList<CalibrationPoint>> paths = target.GetGroupContours(group.Id, 20);
            Check(paths.Count == 1 && paths[0].Count == 21 && paths[0][0] == cell.Corners[0]
                && paths[0][^1] == cell.Corners[1], "group preview contains the complete actual arm only");
            foreach (CalibrationPoint point in paths[0].Skip(1).SkipLast(1))
                Check(target.Sample(point.X, point.Y) == 0, "preview and engraved arm agree");
            Near(group.SpineInnerRadiusMm, Radius(cell.Corners[0]), 1e-10, "arm inner bound");
            Near(group.SpineOuterRadiusMm, Radius(cell.Corners[1]), 1e-10, "arm outer bound");
        }
        CalibrationPoint endpoint = target.GetCorner("C-0-spine", 1);
        var session = new CalibrationSession(target, [new("C-0-spine", 1, endpoint.X, endpoint.Y)],
            new(RadiusRangeMm: 0.017), CdInterleave: false);
        string json = CalibrationSessionJson.Serialize(session);
        CalibrationSession restored = CalibrationSessionJson.Deserialize(json);
        Check(restored.Target.Id == target.Id && !restored.CdInterleave
            && restored.Target.RingRadiiMm.SequenceEqual(target.RingRadiiMm), "complete generation snapshot round trip");
        Check(restored.Target.ControlPoints.Select(point => point.Id).SequenceEqual(target.ControlPoints.Select(point => point.Id)),
            "derived arm endpoint identities round trip");
        Check(CalibrationSessionJson.Serialize(restored) == json, "stable session serialization");
        Check(!json.Contains("controlPoints", StringComparison.Ordinal) && !json.Contains("radialLine", StringComparison.Ordinal),
            "derived geometry is reconstructed from schema rules");
        foreach (int version in new[] { 1, 2, 3, 4, 5, CalibrationTarget.CurrentSchemaVersion + 1 })
        {
            JsonNode old = JsonNode.Parse(json)!;
            old["target"]!["schemaVersion"] = version;
            Throws<ArgumentException>(() => CalibrationSessionJson.Deserialize(old.ToJsonString()), "unsupported schema rejected");
        }
        Throws<ArgumentException>(() => target.GetCorner("C-0-spine", 2), "endpoint bound");
        Throws<ArgumentException>(() => target.GetCorner("C-0-01-R", 0), "removed comb controls are unavailable");
    }

    private static void TestDraftsAndInsufficientInput()
    {
        CalibrationTarget target = CalibrationTarget.Create(Parameters());
        CalibrationCrossFitter.ValidateTraces(target, []);
        CalibrationCrossFitter.ValidateTraces(target, [new(0, []), new(1, [new(30, 0)])]);
        Check(!CalibrationCrossFitter.Fit(target, []).Succeeded, "empty draft cannot fit");
        Check(!CalibrationCrossFitter.Fit(target, [new(0, [new(30, 0)])]).Succeeded, "one point cannot fit");
        Throws<ArgumentException>(() => CalibrationCrossFitter.ValidateTraces(target, [new(0, []), new(0, [])]), "duplicate arm identity");
        Throws<ArgumentException>(() => CalibrationCrossFitter.ValidateTraces(target, [new(4, [])]), "arm index bound");
        Throws<ArgumentException>(() => CalibrationCrossFitter.ValidateTraces(target, [new(0, [new(double.NaN, 30)])]), "nonfinite trace rejected");
        var local = new CalibrationCrossTrace(0, Enumerable.Range(0, 12).Select(i => new CalibrationPoint(0, -35 - i * 0.05)).ToArray());
        Check(!CalibrationCrossFitter.Fit(target, [local]).Succeeded, "narrow radial station cannot separate two radii");
        CalibrationCrossTrace valid = Trace(target, target.Parameters, 0.4, 0, 12);
        Check(!CalibrationCrossFitter.Fit(target, [valid with { Points = valid.Points.Reverse().ToArray() }]).Succeeded,
            "reversed or switched path is not silently reinterpreted as another arm");
        CalibrationPoint[] sparse = [Polar(27, 0), Polar(31, Math.PI), Polar(36, 2.9), Polar(42, 3), Polar(48, 3.1), Polar(54, 3.2)];
        Check(!CalibrationCrossFitter.Fit(target, [new(0, sparse)]).Succeeded, "a half-turn jump asks for intermediate points");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => CalibrationCrossFitter.Fit(target, [valid], cancellationToken: cancellation.Token),
            "curve fit honors cancellation before searching");
    }

    private static void TestOneArmWithUnknownSourceRadii(CalibrationDiscKind kind)
    {
        CalibrationTarget target = CalibrationTarget.Create(Parameters(kind));
        CalibrationParameters actual = target.Parameters with { InnerRadiusMm = target.Parameters.InnerRadiusMm + 0.004,
            OuterRadiusMm = target.Parameters.OuterRadiusMm + 0.004 };
        const double rotation = 2.71;
        // This one arm winds by multiple turns. Samples have arbitrary source radii,
        // deliberately unrelated to guide-circle intersections or fixed control points.
        CalibrationCrossTrace trace = Trace(target, actual, rotation, 0, 97);
        Check(Math.Abs(UnwrappedSpan(trace.Points)) > 2 * Math.PI, "synthetic arm genuinely includes a full extra turn");
        CalibrationFitResult fit = CalibrationCrossFitter.Fit(target, [trace]);
        Check(fit.Succeeded && fit.BestParameters is not null && fit.RequiresShapeConfirmation, $"{kind} one continuous arm fits");
        Near(actual.InnerRadiusMm, fit.BestParameters!.InnerRadiusMm, 3e-7, $"{kind} single-arm inner radius");
        Near(actual.OuterRadiusMm, fit.BestParameters.OuterRadiusMm, 3e-7, $"{kind} single-arm outer radius");
        Near(0, Normalize(fit.RotationRadians - rotation), 5e-5, $"{kind} arbitrary photograph rotation");
        Check(fit.RmsErrorMm < 1e-5, $"{kind} exact continuous residual");
        Check(fit.Warnings.Any(warning => warning.Contains("整圈", StringComparison.Ordinal)),
            "sparse inputs never claim to prove that no unrecorded turn exists");
        CalibrationFitResult wrongRange = CalibrationCrossFitter.Fit(target, [trace], new(RadiusRangeMm: 0.0001));
        Check(!wrongRange.Succeeded || wrongRange.RmsErrorMm > 0.5,
            "a near-straight model cannot erase the continuous winding by independently wrapping samples");
        IReadOnlyList<CalibrationPoint> preview = target.GetMappedGroupContours("C-0", fit.BestParameters, fit.RotationRadians)[0];
        Check(trace.Points.All(point => DistanceToPolyline(point, preview) < 0.12),
            "selected candidate renders the entire fitted arm, including intervening turns");
    }

    private static void TestNoisyCurveAndArmIdentity(CalibrationDiscKind kind)
    {
        CalibrationTarget target = CalibrationTarget.Create(Parameters(kind));
        CalibrationParameters actual = target.Parameters with { InnerRadiusMm = target.Parameters.InnerRadiusMm + 0.0017,
            OuterRadiusMm = target.Parameters.OuterRadiusMm - 0.0011 };
        const double rotation = -1.13;
        CalibrationCrossTrace trace = Trace(target, actual, rotation, 0, 25, 0.025);
        CalibrationFitResult noisy = CalibrationCrossFitter.Fit(target, [trace], new(HuberDeltaMm: 0.15));
        Check(noisy.Succeeded && noisy.BestParameters is not null, $"{kind} noisy manual points fit");
        Near(actual.InnerRadiusMm, noisy.BestParameters!.InnerRadiusMm, 0.0003, $"{kind} noisy inner recovery");
        Near(actual.OuterRadiusMm, noisy.BestParameters.OuterRadiusMm, 0.0003, $"{kind} noisy outer recovery");
        Check(noisy.RmsErrorMm < 0.07, $"{kind} residual follows normal drawing error rather than amplified tangential error");
        CalibrationPoint[] withOutlier = trace.Points.ToArray();
        CalibrationPoint misplaced = withOutlier[12];
        withOutlier[12] = Polar(Radius(misplaced), Math.Atan2(misplaced.Y, misplaced.X) + 0.05);
        CalibrationFitResult robust = CalibrationCrossFitter.Fit(target,
            [trace with { Points = withOutlier }], new(HuberDeltaMm: 0.08));
        Check(robust.Succeeded && robust.BestParameters is not null, $"{kind} isolated wrong click has a robust solution");
        Near(actual.InnerRadiusMm, robust.BestParameters!.InnerRadiusMm, 0.0003, $"{kind} outlier-resistant inner radius");
        Near(actual.OuterRadiusMm, robust.BestParameters.OuterRadiusMm, 0.0003, $"{kind} outlier-resistant outer radius");
        CalibrationCrossTrace arbitraryArm = Trace(target, actual, rotation, 2, 23) with { ArmIndex = 0 };
        CalibrationFitResult relabeled = CalibrationCrossFitter.Fit(target, [arbitraryArm]);
        Near(actual.InnerRadiusMm, relabeled.BestParameters!.InnerRadiusMm, 3e-7, "any one visible arm can be selected");
        Near(0, Normalize(relabeled.RotationRadians - rotation - Math.PI), 5e-5, "single-arm relabeling only changes free rotation");
        CalibrationCrossTrace[] all = Enumerable.Range(0, 4).Select(arm => Trace(target, actual, rotation, arm, 13 + 4 * arm)).ToArray();
        CalibrationFitResult combined = CalibrationCrossFitter.Fit(target, all);
        Near(actual.InnerRadiusMm, combined.BestParameters!.InnerRadiusMm, 3e-7, "unequal arm sampling densities retain the same physical model");
        Near(actual.OuterRadiusMm, combined.BestParameters.OuterRadiusMm, 3e-7, "different arms share both fitted radii");
        Check(combined.Candidates.Zip(combined.Candidates.Skip(1)).All(pair => pair.First.RobustCost <= pair.Second.RobustCost),
            "candidate ordering uses the robust continuous-curve objective");
    }

    private static void TestBoundarySignalAndWideRecovery(CalibrationDiscKind kind)
    {
        // BluRay spans far more turns per millimetre and deliberately keeps the
        // positive-radius continuation; this CD/DVD-shaped trace is not a valid
        // winding fixture for it. BD calibration has its own observed-curve tests.
        if (kind == CalibrationDiscKind.BluRay) return;
        CalibrationParameters generated = Parameters(kind);
        CalibrationParameters actual = generated with
        {
            InnerRadiusMm = generated.InnerRadiusMm + 0.05,
            OuterRadiusMm = generated.OuterRadiusMm - 0.04,
        };
        CalibrationTarget target = CalibrationTarget.Create(generated);
        CalibrationCrossTrace trace = Trace(target, actual, 0.37, 0, 70);
        CalibrationFitResult narrow = CalibrationCrossFitter.Fit(target, [trace],
            new(RadiusRangeMm: 0.02, FitDvdPitch: false));
        Check(narrow.Succeeded && narrow.BoundaryLimited,
            $"{kind} narrow search marks its boundary-clamped optimum");
        Check(narrow.RmsErrorMm > 0.5, $"{kind} clamped optimum is visibly off the trace");
        CalibrationFitResult wide = CalibrationCrossFitter.Fit(target, [trace],
            new(RadiusRangeMm: 0.1, FitDvdPitch: false));
        Check(wide.Succeeded && !wide.BoundaryLimited, $"{kind} wider search finds an interior optimum");
        Near(actual.InnerRadiusMm, wide.BestParameters!.InnerRadiusMm, 3e-7, $"{kind} wide-search inner radius");
        Near(actual.OuterRadiusMm, wide.BestParameters.OuterRadiusMm, 3e-7, $"{kind} wide-search outer radius");
        Check(wide.RmsErrorMm < 0.05 && wide.Candidates.All(candidate => candidate.MaxErrorMm > 0),
            $"{kind} wide-search residual and per-point maximum are populated");
    }

    private static void TestMappedOutline(CalibrationDiscKind kind)
    {
        CalibrationTarget target = CalibrationTarget.Create(Parameters(kind));
        CalibrationParameters actual = target.Parameters with { InnerRadiusMm = target.Parameters.InnerRadiusMm + 0.04,
            OuterRadiusMm = target.Parameters.OuterRadiusMm + 0.04 };
        IReadOnlyList<CalibrationPoint> mapped = target.GetMappedOutline("C-0-spine", actual, 0.8);
        Check(mapped[0] != mapped[^1] && mapped.Count > 100, $"{kind} complete mapped open arm");
        foreach (var pair in mapped.Zip(mapped.Skip(1)))
            Check(Math.Abs(Normalize(Math.Atan2(pair.Second.Y, pair.Second.X) - Math.Atan2(pair.First.Y, pair.First.X)))
                <= Math.PI / 36 + 1e-7, "bounded phase step in full candidate preview");
        Throws<InvalidOperationException>(() => target.GetMappedOutline("C-0-spine", actual, maxPoints: 16),
            "outline budget cannot silently omit turns");
        CalibrationPoint[] dense = target.GetOutline("C-0-spine", 2048).Select(target.CreateMapping(actual, 0.8)).ToArray();
        Check(dense.All(point => DistanceToPolyline(point, mapped) < 0.12), "adaptive arm outline includes independently sampled turns");
        Check(target.GetMappedGroupContours("C-0", actual, 0.8).Count == 1, "candidate excludes guide-circle observations");
    }

    private static void TestEncoderGeometry(CalibrationDiscKind kind)
    {
        CalibrationParameters generated = Parameters(kind);
        CalibrationParameters actual = generated with { InnerRadiusMm = generated.InnerRadiusMm + 0.0013,
            OuterRadiusMm = generated.OuterRadiusMm - 0.0007 };
        CalibrationTarget target = CalibrationTarget.Create(generated);
        const double rotation = 0.43;
        CalibrationPoint source, expected;
        if (kind == CalibrationDiscKind.Cd)
        {
            var generatedCd = new CdDiscParameters(generated.InnerRadiusMm, generated.OuterRadiusMm, generated.Sectors, generated.LinearDensity);
            var actualCd = new CdDiscParameters(actual.InnerRadiusMm, actual.OuterRadiusMm, actual.Sectors, actual.LinearDensity);
            long channelPosition = generatedCd.TotalBytes * 37 / 100;
            (double r0, double a0) = generatedCd.PolarFromByte(channelPosition);
            (double r1, double a1) = actualCd.PolarFromByte(channelPosition);
            source = Polar(r0, a0);
            expected = Polar(r1, a1 + rotation);
        }
        else if (kind == CalibrationDiscKind.BluRay)
        {
            var generatedBd = new BluRayTrackGeometry(new(checked((uint)generated.Sectors),
                InnerRadiusMm: generated.InnerRadiusMm, OuterRadiusMm: generated.OuterRadiusMm));
            var actualBd = new BluRayTrackGeometry(new(checked((uint)actual.Sectors),
                InnerRadiusMm: actual.InnerRadiusMm, OuterRadiusMm: actual.OuterRadiusMm));
            BluRayTrackPoint sourceBd = generatedBd.AtLayerFraction(0, 0.37);
            BluRayTrackPoint actualPoint = actualBd.AtLayerFraction(0, 0.37);
            source = Polar(sourceBd.RadiusMm, -sourceBd.AngleRadians);
            expected = Polar(actualPoint.RadiusMm, -actualPoint.AngleRadians + rotation);
        }
        else
        {
            Check(CalibrationParameters.DvdChannelBitsPerSector == DvdEccBlockEncoder.ChannelBitsPerSector, "DVD physical bit count");
            Type spiral = typeof(DvdEccBlockEncoder).Assembly.GetType("DvdImageSolver.Solver.ArchimedeanSpiral", throwOnError: true)!;
            MethodInfo create = spiral.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo radiusAt = spiral.GetMethod("RadiusAtArcLength", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo angleAt = spiral.GetMethod("AngleAtArcLength", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object generatedSpiral = create.Invoke(null, [generated.InnerRadiusMm, generated.OuterRadiusMm,
                generated.TrackLengthMm, generated.PitchLinear, generated.PitchQuadratic, generated.PitchCubic])!;
            object actualSpiral = create.Invoke(null, [actual.InnerRadiusMm, actual.OuterRadiusMm,
                actual.TrackLengthMm, actual.PitchLinear, actual.PitchQuadratic, actual.PitchCubic])!;
            double distance = generated.TrackLengthMm * 0.37;
            source = Polar((double)radiusAt.Invoke(generatedSpiral, [distance])!, (double)angleAt.Invoke(generatedSpiral, [distance])!);
            expected = Polar((double)radiusAt.Invoke(actualSpiral, [distance])!, (double)angleAt.Invoke(actualSpiral, [distance])! + rotation);
        }
        CalibrationPoint mapped = target.MapPoint(source, actual, rotation);
        Near(0, Distance(expected, mapped), 2e-6, $"{kind} mapping matches encoder geometry");
        CalibrationPoint? restored = target.CreateInverseMapping(actual, rotation)(mapped);
        Check(restored is not null, "inverse inside annulus");
        Near(0, Distance(source, restored!), 2e-6, $"{kind} inverse round trip");
        Check(target.CreateInverseMapping(actual)(new(0, 0)) is null, "inverse excludes hole");
    }

    private static CalibrationCrossTrace Trace(CalibrationTarget target, CalibrationParameters actual,
        double rotation, int arm, int count, double noise = 0)
    {
        Func<CalibrationPoint, CalibrationPoint> map = target.CreateMapping(actual, rotation);
        double band = target.Parameters.OuterRadiusMm - target.Parameters.InnerRadiusMm;
        var points = Enumerable.Range(0, count).Select(index =>
        {
            double radius = target.Parameters.InnerRadiusMm + band * (0.065 + 0.87 * Math.Pow(index / (count - 1.0), 1.25));
            CalibrationPoint point = map(Polar(radius, -Math.PI / 2 + arm * Math.PI / 2));
            return new CalibrationPoint(point.X + noise * Math.Sin(index * 2.3 + 0.5),
                point.Y + noise * Math.Cos(index * 1.7 - 0.2));
        }).ToArray();
        return new(arm, points);
    }
    private static double UnwrappedSpan(IReadOnlyList<CalibrationPoint> points) => points.Zip(points.Skip(1))
        .Sum(pair => Normalize(Math.Atan2(pair.Second.Y, pair.Second.X) - Math.Atan2(pair.First.Y, pair.First.X)));
    private static double Normalize(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
    private static CalibrationPoint Polar(double radius, double radians) => new(radius * Math.Cos(radians), radius * Math.Sin(radians));
    private static double Radius(CalibrationPoint point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);
    private static double Distance(CalibrationPoint a, CalibrationPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double DistanceToPolyline(CalibrationPoint point, IReadOnlyList<CalibrationPoint> line)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
        {
            double dx = line[i].X - line[i - 1].X, dy = line[i].Y - line[i - 1].Y;
            double t = Math.Clamp(((point.X - line[i - 1].X) * dx + (point.Y - line[i - 1].Y) * dy) / Math.Max(1e-30, dx * dx + dy * dy), 0, 1);
            best = Math.Min(best, Distance(point, new(line[i - 1].X + t * dx, line[i - 1].Y + t * dy)));
        }
        return best;
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("calibration-core: " + name);
    }
    private static void Near(double expected, double actual, double tolerance, string name)
        => Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"{name}; expected {expected:G17}, actual {actual:G17}, tolerance {tolerance:G3}");
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("calibration-core: expected " + typeof(T).Name + "; " + name);
    }
}
