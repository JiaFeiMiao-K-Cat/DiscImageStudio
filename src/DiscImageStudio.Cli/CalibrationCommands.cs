using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Cli;

internal static class CalibrationTargetCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        CalibrationOptions options = CalibrationOptions.Parse(arguments, "calibration-target",
        [
            "family", "r0", "r1", "sectors", "density",
            "pitch-linear", "pitch-quadratic", "pitch-cubic",
            "drawing-start", "drawing-end", "output", "session", "size",
        ], ["invert"]);
        CalibrationParameters parameters = CalibrationCli.ReadParameters(
            options, allowDrawing: true, allowInvert: true);
        int size = options.GetInt("size", CalibrationReferencePattern.PixelSize);
        if (size != CalibrationReferencePattern.PixelSize)
        {
            throw new CalibrationUsageException(
                $"calibration-target --size is fixed at {CalibrationReferencePattern.PixelSize} pixels "
                + "(the reference PNG resolution).");
        }

        CalibrationTarget target = CalibrationCli.CreateTarget(parameters);
        string output = options.Require("output");
        string sessionPath = options.Require("session");
        CalibrationCli.SaveGrayPng(output, size, CalibrationCli.RenderEncodingImage(target));
        CalibrationSessionJson.Save(sessionPath, new CalibrationSession(target, [], null, true, null));
        Console.WriteLine(
            $"Calibration target: {CalibrationCli.FamilyArgument(parameters.Kind)}, "
            + $"r0/r1={Format(parameters.InnerRadiusMm)}/{Format(parameters.OuterRadiusMm)} mm, "
            + $"{parameters.Sectors} sectors, {size}px, output={Path.GetFullPath(output)}, "
            + $"session={Path.GetFullPath(sessionPath)}");
        return 0;
    }

    private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

internal static class CalibrationCoarseTargetCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        CalibrationOptions options = CalibrationOptions.Parse(arguments, "calibration-coarse-target",
        [
            "family", "r0", "r1", "sectors", "density",
            "pitch-linear", "pitch-quadratic", "pitch-cubic",
            "drawing-start", "drawing-end", "output", "json", "size",
        ]);
        CalibrationParameters parameters = CalibrationCli.ReadParameters(
            options, allowDrawing: true, allowInvert: false);
        int size = options.GetInt("size", CalibrationReferencePattern.PixelSize);
        if (size is < 64 or > 8192)
        {
            throw new CalibrationUsageException("--size must be 64..8192 pixels.");
        }

        string outputPath = Path.GetFullPath(options.Require("output"));
        string jsonPath = options.Require("json");
        CalibrationCli.SaveGrayPng(outputPath, size, CalibrationCli.RenderCoarseImage(parameters, size));
        CalibrationCli.WriteJson(jsonPath, new CalibrationCoarseTargetP0Dto(
            CalibrationCli.FamilyArgument(parameters.Kind),
            parameters.InnerRadiusMm, parameters.OuterRadiusMm, parameters.Sectors,
            parameters.LinearDensity, parameters.PitchLinear, parameters.PitchQuadratic,
            parameters.PitchCubic, parameters.DrawingStartSector, parameters.DrawingEndSectorExclusive,
            outputPath));
        Console.WriteLine(
            $"Coarse calibration target: {CalibrationCli.FamilyArgument(parameters.Kind)}, "
            + $"r0/r1={parameters.InnerRadiusMm:0.###}/{parameters.OuterRadiusMm:0.###} mm, "
            + $"{parameters.Sectors} sectors, {size}px, output={outputPath}, json={Path.GetFullPath(jsonPath)}");
        return 0;
    }
}

internal static class CalibrationPhotoCommand
{
    private const int MaximumPhotoDimension = 2000;

    internal static int Run(IReadOnlyList<string> arguments)
    {
        CalibrationOptions options = CalibrationOptions.Parse(arguments, "calibration-photo",
        [
            "photo", "disc-size", "outer", "hole", "resized-out", "rectified-out", "json", "size",
        ]);
        string photoPath = options.Require("photo");
        int discSize = options.GetInt("disc-size", 0);
        if (discSize == 0)
        {
            throw new CalibrationUsageException("Missing required option --disc-size.");
        }

        double physicalOuterRadius = CalibrationCli.DiscSizeToOuterRadius(discSize);
        int size = options.GetInt("size", 1200);
        if (size is < 32 or > 4096)
        {
            throw new CalibrationUsageException("--size must be 32..4096 pixels.");
        }

        string resizedPath = Path.GetFullPath(options.Require("resized-out"));
        string jsonPath = options.Require("json");
        string? outerText = options.GetString("outer");
        string? holeText = options.GetString("hole");
        bool hasOuter = !string.IsNullOrWhiteSpace(outerText);
        bool hasHole = !string.IsNullOrWhiteSpace(holeText);
        if (hasOuter != hasHole)
        {
            throw new CalibrationUsageException("Provide both --outer and --hole, or neither.");
        }

        BgraRaster resized = CalibrationCli.LoadPhoto(photoPath, MaximumPhotoDimension);
        CalibrationCli.SaveBgraPng(resizedPath, resized);
        if (!hasOuter)
        {
            CalibrationCli.WriteJson(jsonPath, new CalibrationPhotoResultDto(
                true, string.Empty, resized.Width, resized.Height,
                null, null, discSize, null, null, null, resizedPath, null));
            Console.WriteLine($"Calibration photo: {resized.Width}x{resized.Height}px, resized={resizedPath}");
            return 0;
        }

        CalibrationPoint[] outer = CalibrationCli.ParsePoints(outerText!);
        CalibrationPoint[] hole = CalibrationCli.ParsePoints(holeText!);
        string rectifiedPath = Path.GetFullPath(options.Require("rectified-out"));
        CalibrationPhotoFitResult fit = CalibrationPhotoRectifier.Fit(
            outer, hole, physicalOuterRadius, 7.5, CancellationToken.None);
        if (!fit.Succeeded || fit.Transform is null)
        {
            CalibrationCli.WriteJson(jsonPath, new CalibrationPhotoResultDto(
                false, fit.Message, resized.Width, resized.Height,
                null, null, discSize,
                double.IsFinite(fit.OuterRmsPixels) ? fit.OuterRmsPixels : null,
                double.IsFinite(fit.HoleRmsPixels) ? fit.HoleRmsPixels : null,
                null, resizedPath, null));
            Console.WriteLine($"Calibration photo failed: {fit.Message}");
            return 0;
        }

        BgraRaster rectified = CalibrationCli.Rectify(resized, fit.Transform, physicalOuterRadius, size);
        CalibrationCli.SaveBgraPng(rectifiedPath, rectified);
        CalibrationCli.WriteJson(jsonPath, new CalibrationPhotoResultDto(
            true, fit.Message, resized.Width, resized.Height,
            size, physicalOuterRadius, discSize,
            fit.OuterRmsPixels, fit.HoleRmsPixels, fit.Transform.DiscToSourceMatrix9,
            resizedPath, rectifiedPath));
        Console.WriteLine(
            $"Calibration photo: {resized.Width}x{resized.Height}px, rectified {size}x{size}, "
            + $"outerRms={fit.OuterRmsPixels:0.###}px, holeRms={fit.HoleRmsPixels:0.###}px, output={rectifiedPath}");
        return 0;
    }
}

internal static class CalibrationFitCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        CalibrationOptions options = CalibrationOptions.Parse(arguments, "calibration-fit",
            ["session", "points", "json", "radius-range", "auto-range", "fit-pitch"],
            allowEmptyValues: ["points"]);
        string sessionPath = options.Require("session");
        CalibrationPoint[] points = CalibrationCli.ParsePoints(options.Require("points"));
        string jsonPath = options.Require("json");
        double? radiusRange = options.GetString("radius-range") is null
            ? null
            : options.GetDouble("radius-range", 0);
        bool autoRange = options.GetBool("auto-range", true);
        bool fitPitch = options.GetBool("fit-pitch", true);

        CalibrationSession session;
        try
        {
            session = CalibrationSessionJson.Load(sessionPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot read calibration session '{sessionPath}': {error.Message}", error);
        }

        CalibrationPhotoSession photo = session.Photo
            ?? throw new InvalidOperationException(
                "The calibration session has no photo section; run calibration-photo and save a session first.");

        CalibrationFitOptions baseOptions = session.FitOptions ?? new();
        CalibrationFitOptions fitOptions = baseOptions with
        {
            SearchCenter = baseOptions.SearchCenter ?? session.Target.Parameters,
            RadiusRangeMm = radiusRange ?? baseOptions.RadiusRangeMm,
            FitDvdPitch = fitPitch,
            AdaptiveRobustScale = true,
            AutoRange = autoRange,
        };
        try
        {
            fitOptions.Validate(session.Target);
        }
        catch (ArgumentException error)
        {
            throw new CalibrationUsageException(error.Message);
        }

        CalibrationCrossTrace trace = new(0, points);
        CalibrationPhotoSession updatedPhoto = photo with
        {
            RectificationConfirmed = true,
            Traces = [trace],
        };
        CalibrationSession updatedSession = session with
        {
            Photo = updatedPhoto,
            FitOptions = fitOptions,
        };

        CalibrationFitResult result;
        try
        {
            (result, _, _) = FitWithAutoRange(session.Target, [trace], fitOptions, autoRange);
        }
        catch (ArgumentException error)
        {
            CalibrationCli.WriteJson(jsonPath, new CalibrationFitResultDto(
                false, false, false, [error.Message], -1, []));
            Console.WriteLine($"Calibration fit failed: {error.Message}");
            return 0;
        }

        var warnings = new List<string>(result.Warnings);
        try
        {
            CalibrationSessionJson.Save(sessionPath, updatedSession);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException)
        {
            warnings.Add($"会话写回失败：{error.Message}");
        }

        var candidates = new List<CalibrationCandidateDto>();
        try
        {
            candidates.AddRange(BuildCandidates(session.Target, points, result));
        }
        catch (ArgumentException error)
        {
            warnings.Add($"预测折线生成失败：{error.Message}");
        }

        int bestIndex = result.Succeeded && candidates.Count > 0 ? 0 : -1;
        CalibrationCli.WriteJson(jsonPath, new CalibrationFitResultDto(
            result.Succeeded, result.IsReliable, result.BoundaryLimited, warnings, bestIndex, candidates));
        Console.WriteLine(
            result.Succeeded
                ? $"Calibration fit: {candidates.Count} candidates, best r0/r1="
                    + $"{candidates[0].InnerRadiusMm:0.###}/{candidates[0].OuterRadiusMm:0.###} mm, "
                    + $"rms={candidates[0].RmsErrorMm:0.###} mm, warnings={warnings.Count}"
                : $"Calibration fit failed: {(warnings.Count > 0 ? warnings[0] : "no candidates")}");
        return 0;
    }

    private static IEnumerable<CalibrationCandidateDto> BuildCandidates(
        CalibrationTarget target, IReadOnlyList<CalibrationPoint> points, CalibrationFitResult result)
    {
        if (points.Count == 0 || result.Candidates.Count == 0)
        {
            yield break;
        }

        double minimumRadius = points.Min(point => Math.Sqrt((point.X * point.X) + (point.Y * point.Y)));
        double maximumRadius = points.Max(point => Math.Sqrt((point.X * point.X) + (point.Y * point.Y)));
        foreach (CalibrationFitCandidate candidate in result.Candidates)
        {
            IReadOnlyList<CalibrationPointDto> predicted;
            try
            {
                predicted = CalibrationCrossPrediction.GetOutline(
                        target, candidate.Parameters, minimumRadius, maximumRadius,
                        candidate.RotationRadians, armIndex: 0, maxPoints: 4000)
                    .Select(point => new CalibrationPointDto(point.X, point.Y))
                    .ToArray();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                predicted = [];
            }

            yield return new CalibrationCandidateDto(
                candidate.Parameters.InnerRadiusMm, candidate.Parameters.OuterRadiusMm,
                candidate.Parameters.PitchLinear, candidate.Parameters.PitchQuadratic,
                candidate.Parameters.PitchCubic, candidate.RotationRadians,
                candidate.RmsErrorMm, candidate.MaxErrorMm, candidate.OutlierCount,
                candidate.Parameters.Sectors, candidate.Parameters.BluRayInvertMapping, predicted);
        }
    }

    /// <summary>Auto mode widens the radius box until the optimum is interior (same rule as
    /// the Windows calibration page).</summary>
    private static (CalibrationFitResult Result, double RangeMm, bool Expanded) FitWithAutoRange(
        CalibrationTarget target, IReadOnlyList<CalibrationCrossTrace> traces,
        CalibrationFitOptions options, bool autoRange)
    {
        if (!autoRange)
        {
            return (CalibrationCrossFitter.Fit(target, traces, options), options.RadiusRangeMm, false);
        }

        double[] ranges = [options.RadiusRangeMm, 0.1, 0.5, 2, 5];
        CalibrationFitResult? best = null;
        CalibrationFitResult? last = null;
        double bestRange = options.RadiusRangeMm;
        foreach (double range in ranges.Where(range => range >= options.RadiusRangeMm)
                     .Distinct().OrderBy(range => range))
        {
            CalibrationFitResult result = CalibrationCrossFitter.Fit(
                target, traces, options with { RadiusRangeMm = range });
            last = result;
            bool unusableBluRay = target.Parameters.Kind == CalibrationDiscKind.BluRay
                && result.RmsErrorMm > 0.8;
            if (result.Succeeded && !result.BoundaryLimited && !unusableBluRay)
            {
                return (result, range, range > options.RadiusRangeMm);
            }

            if (result.Succeeded && (best is null || result.RmsErrorMm < best.RmsErrorMm))
            {
                best = result;
                bestRange = range;
            }

            if (!result.Succeeded && IsInputFailure(result))
            {
                break;
            }
        }

        CalibrationFitResult fallback = best ?? last ?? CalibrationCrossFitter.Fit(target, traces, options);
        return (fallback, bestRange, bestRange > options.RadiusRangeMm);
    }

    private static bool IsInputFailure(CalibrationFitResult result)
        => !result.Succeeded && result.Warnings.Any(warning =>
            warning.Contains("至少标出", StringComparison.Ordinal)
            || warning.Contains("覆盖范围太窄", StringComparison.Ordinal)
            || warning.Contains("向内回退", StringComparison.Ordinal)
            || warning.Contains("接近半圈", StringComparison.Ordinal));
}

internal static class CalibrationCoarseFitCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        CalibrationOptions options = CalibrationOptions.Parse(arguments, "calibration-coarse-fit",
        [
            "family", "r0", "r1", "sectors", "density",
            "pitch-linear", "pitch-quadratic", "pitch-cubic", "points", "json",
        ]);
        CalibrationParameters parameters = CalibrationCli.ReadParameters(
            options, allowDrawing: false, allowInvert: false);
        CalibrationPoint[] points = CalibrationCli.ParsePoints(options.Require("points"));
        string jsonPath = options.Require("json");

        CalibrationCoarseFitResult result = CalibrationCoarseFitter.Fit(parameters, points);
        var candidates = new List<CalibrationCandidateDto>();
        if (result.Succeeded && points.Length > 0)
        {
            double minimumRadius = points.Min(point => Math.Sqrt((point.X * point.X) + (point.Y * point.Y)));
            double maximumRadius = points.Max(point => Math.Sqrt((point.X * point.X) + (point.Y * point.Y)));
            foreach (CalibrationCoarseCandidate candidate in result.Candidates)
            {
                IReadOnlyList<CalibrationPointDto> predicted;
                try
                {
                    predicted = CalibrationCoarseFitter.PredictBoundary(
                            parameters, candidate, minimumRadius, maximumRadius, 400)
                        .Select(point => new CalibrationPointDto(point.X, point.Y))
                        .ToArray();
                }
                catch (ArgumentException)
                {
                    predicted = [];
                }

                candidates.Add(new CalibrationCandidateDto(
                    candidate.Parameters.InnerRadiusMm, candidate.Parameters.OuterRadiusMm,
                    candidate.Parameters.PitchLinear, candidate.Parameters.PitchQuadratic,
                    candidate.Parameters.PitchCubic, candidate.RotationRadians,
                    candidate.RmsErrorMm, candidate.MaxErrorMm, candidate.OutlierCount,
                    candidate.Parameters.Sectors, candidate.Parameters.BluRayInvertMapping, predicted));
            }
        }

        CalibrationCli.WriteJson(jsonPath, new CalibrationCoarseResultDto(
            result.Succeeded, result.Warnings, result.BestIndex, candidates));
        Console.WriteLine(
            result.Succeeded && candidates.Count > 0
                ? $"Coarse calibration fit: {candidates.Count} candidates, bestIndex={result.BestIndex}, "
                    + $"best r0/r1={candidates[0].InnerRadiusMm:0.###}/{candidates[0].OuterRadiusMm:0.###} mm, "
                    + $"rms={candidates[0].RmsErrorMm:0.###} mm, warnings={result.Warnings.Count}"
                : $"Coarse calibration fit failed: {(result.Warnings.Count > 0 ? result.Warnings[0] : "no candidates")}");
        return 0;
    }
}

internal static class CalibrationSessionWriteCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        CalibrationOptions options = CalibrationOptions.Parse(arguments, "calibration-session-write",
            ["session", "photo", "disc-size", "outer", "hole", "points", "output"],
            allowEmptyValues: ["points"]);
        string basePath = options.Require("session");
        string photoPath = options.Require("photo");
        int discSize = options.GetInt("disc-size", 0);
        if (discSize == 0)
        {
            throw new CalibrationUsageException("Missing required option --disc-size.");
        }

        double physicalOuterRadius = CalibrationCli.DiscSizeToOuterRadius(discSize);
        CalibrationPoint[] outer = CalibrationCli.ParsePoints(options.Require("outer"));
        CalibrationPoint[] hole = CalibrationCli.ParsePoints(options.Require("hole"));
        CalibrationPoint[] points = CalibrationCli.ParsePoints(options.Require("points"));
        string output = options.Require("output");

        CalibrationSession baseSession;
        try
        {
            baseSession = CalibrationSessionJson.Load(basePath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot read base calibration session '{basePath}': {error.Message}", error);
        }

        byte[] photoBytes = CalibrationCli.ReadPhotoPng(photoPath);
        CalibrationPhotoSession photo = new(
            Convert.ToBase64String(photoBytes), outer, hole, physicalOuterRadius,
            RectificationConfirmed: true, [new CalibrationCrossTrace(0, points)]);
        try
        {
            photo.Validate();
        }
        catch (ArgumentException error)
        {
            throw new CalibrationUsageException(error.Message);
        }

        CalibrationSession session = new(
            baseSession.Target, baseSession.Observations, baseSession.FitOptions,
            baseSession.CdInterleave, photo);
        try
        {
            session.Validate();
        }
        catch (ArgumentException error)
        {
            throw new CalibrationUsageException(error.Message);
        }

        CalibrationSessionJson.Save(output, session);
        (int Width, int Height)? size = CalibrationCli.TryReadPngSize(photo.ImagePngBase64);
        Console.WriteLine(
            $"Calibration session written: {Path.GetFullPath(output)}, photo="
            + $"{(size is { } value ? $"{value.Width}x{value.Height}px" : "png")}, "
            + $"traces={points.Length}, disc={discSize} mm");
        return 0;
    }
}

internal static class CalibrationSessionReadCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        CalibrationOptions options = CalibrationOptions.Parse(arguments, "calibration-session-read",
            ["session", "json"]);
        string sessionPath = options.Require("session");
        string jsonPath = options.Require("json");
        CalibrationSession session;
        try
        {
            session = CalibrationSessionJson.Load(sessionPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot read calibration session '{sessionPath}': {error.Message}", error);
        }

        CalibrationParameters parameters = session.Target.Parameters;
        (int Width, int Height)? photoSize = session.Photo is null
            ? null
            : CalibrationCli.TryReadPngSize(session.Photo.ImagePngBase64);
        int tracePointCount = session.Photo?.Traces.Sum(trace => trace.Points.Count) ?? 0;
        CalibrationSessionSummaryDto summary = new(
            CalibrationCli.FamilyArgument(parameters.Kind),
            parameters.InnerRadiusMm, parameters.OuterRadiusMm, parameters.Sectors,
            parameters.LinearDensity, parameters.PitchLinear, parameters.PitchQuadratic,
            parameters.PitchCubic, parameters.BluRayInvertMapping,
            session.Photo is not null, session.Photo?.RectificationConfirmed,
            session.Photo is null ? null : tracePointCount,
            photoSize?.Width, photoSize?.Height,
            parameters.DrawingStartSector, parameters.DrawingEndSectorExclusive);
        CalibrationCli.WriteJson(jsonPath, summary);
        Console.WriteLine(
            $"Calibration session: family={summary.Family}, r0/r1="
            + $"{summary.InnerRadiusMm:0.###}/{summary.OuterRadiusMm:0.###} mm, "
            + $"sectors={summary.Sectors}, photo={(summary.HasPhoto == true ? "yes" : "no")}, "
            + $"traces={summary.TracePointCount ?? 0}, json={Path.GetFullPath(jsonPath)}");
        return 0;
    }
}
