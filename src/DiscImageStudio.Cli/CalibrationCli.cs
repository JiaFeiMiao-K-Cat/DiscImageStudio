using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Cli;

/// <summary>A command-line usage error; calibration commands exit with code 2 for these.</summary>
internal sealed class CalibrationUsageException(string message) : Exception(message);

/// <summary>
/// Strict option reader for calibration commands. Values use <c>--name value</c> or
/// <c>--name=value</c>; explicitly declared switches (for example <c>--invert</c>) take no value.
/// </summary>
internal sealed class CalibrationOptions
{
    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _flags;

    private CalibrationOptions(Dictionary<string, string> values, HashSet<string> flags)
    {
        _values = values;
        _flags = flags;
    }

    internal static CalibrationOptions Parse(IReadOnlyList<string> arguments, string command,
        IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string>? flagOptions = null,
        IReadOnlyCollection<string>? allowEmptyValues = null)
    {
        HashSet<string> values = new(valueOptions, StringComparer.OrdinalIgnoreCase);
        HashSet<string> flags = new(flagOptions ?? [], StringComparer.OrdinalIgnoreCase);
        HashSet<string> emptyAllowed = new(allowEmptyValues ?? [], StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> parsedFlags = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CalibrationUsageException(
                    $"{command} does not accept positional argument '{argument}'.");
            }

            int separator = argument.IndexOf('=');
            string name = separator < 0 ? argument[2..] : argument[2..separator];
            if (flags.Contains(name))
            {
                if (separator >= 0)
                {
                    throw new CalibrationUsageException($"--{name} does not take a value.");
                }

                if (!parsedFlags.Add(name))
                {
                    throw new CalibrationUsageException($"--{name} was specified more than once.");
                }

                continue;
            }

            if (!values.Contains(name))
            {
                throw new CalibrationUsageException($"Unknown option --{name} for {command}.");
            }

            string value;
            if (separator >= 0)
            {
                value = argument[(separator + 1)..];
            }
            else
            {
                if (++index >= arguments.Count)
                {
                    throw new CalibrationUsageException($"Missing value for --{name}.");
                }

                value = arguments[index];
            }

            if (string.IsNullOrWhiteSpace(value) && !emptyAllowed.Contains(name))
            {
                throw new CalibrationUsageException($"Empty value for --{name}.");
            }

            if (!parsed.TryAdd(name, value))
            {
                throw new CalibrationUsageException($"--{name} was specified more than once.");
            }
        }

        return new(parsed, parsedFlags);
    }

    internal bool HasFlag(string name) => _flags.Contains(name);

    internal string Require(string name)
        => _values.TryGetValue(name, out string? value)
            ? value
            : throw new CalibrationUsageException($"Missing required option --{name}.");

    internal string? GetString(string name)
        => _values.TryGetValue(name, out string? value) ? value : null;

    internal double GetDouble(string name, double fallback)
        => _values.TryGetValue(name, out string? value)
            ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
              && double.IsFinite(parsed)
                ? parsed
                : throw new CalibrationUsageException($"--{name} must be a finite number.")
            : fallback;

    internal double RequireDouble(string name)
        => _values.ContainsKey(name)
            ? GetDouble(name, 0)
            : throw new CalibrationUsageException($"Missing required option --{name}.");

    internal int GetInt(string name, int fallback)
        => _values.TryGetValue(name, out string? value)
            ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw new CalibrationUsageException($"--{name} must be an integer.")
            : fallback;

    internal long GetLong(string name, long fallback)
        => _values.TryGetValue(name, out string? value)
            ? long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : throw new CalibrationUsageException($"--{name} must be an integer.")
            : fallback;

    internal long RequireLong(string name)
        => _values.TryGetValue(name, out string? value)
            ? long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : throw new CalibrationUsageException($"--{name} must be an integer.")
            : throw new CalibrationUsageException($"Missing required option --{name}.");

    internal bool GetBool(string name, bool fallback)
        => _values.TryGetValue(name, out string? value)
            ? bool.TryParse(value, out bool parsed)
                ? parsed
                : throw new CalibrationUsageException($"--{name} must be true or false.")
            : fallback;
}

internal static class CalibrationCli
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static void WriteJson<T>(string path, T value)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(fullPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static CalibrationPoint[] ParsePoints(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        string[] chunks = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var points = new CalibrationPoint[chunks.Length];
        for (int index = 0; index < chunks.Length; index++)
        {
            string[] parts = chunks[index].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                || !double.IsFinite(x) || !double.IsFinite(y))
            {
                throw new CalibrationUsageException(
                    $"Invalid point '{chunks[index]}'; expected \"x,y;x,y\" with finite millimetre values.");
            }

            points[index] = new(x, y);
        }

        return points;
    }

    internal static void EnsureParentDirectory(string path)
        => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

    internal static CalibrationDiscKind ParseFamily(string value) => value.Trim().ToLowerInvariant() switch
    {
        "cd" => CalibrationDiscKind.Cd,
        "dvd" => CalibrationDiscKind.Dvd,
        "bd" or "bluray" or "blu-ray" => CalibrationDiscKind.BluRay,
        _ => throw new CalibrationUsageException("--family must be cd, dvd or bd."),
    };

    internal static string FamilyArgument(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Cd => "cd",
        CalibrationDiscKind.Dvd => "dvd",
        _ => "bd",
    };

    /// <summary>Reads the common generation geometry shared by the target, coarse-target and
    /// coarse-fit commands.</summary>
    internal static CalibrationParameters ReadParameters(
        CalibrationOptions options, bool allowDrawing, bool allowInvert)
    {
        CalibrationDiscKind kind = ParseFamily(options.Require("family"));
        double inner = options.RequireDouble("r0");
        double outer = options.RequireDouble("r1");
        long sectors = options.RequireLong("sectors");
        double density = options.RequireDouble("density");
        if (kind == CalibrationDiscKind.BluRay && density != 74.5)
        {
            throw new CalibrationUsageException("BD calibration uses a fixed 74.5 nm channel bit length.");
        }

        double pitchLinear = options.GetDouble("pitch-linear", 0);
        double pitchQuadratic = options.GetDouble("pitch-quadratic", 0);
        double pitchCubic = options.GetDouble("pitch-cubic", 0);
        bool invert = allowInvert && options.HasFlag("invert");
        long drawingStart = allowDrawing ? options.GetLong("drawing-start", 0) : 0;
        long drawingEnd = allowDrawing ? options.GetLong("drawing-end", 0) : 0;
        CalibrationParameters parameters = new(kind, inner, outer, sectors, density,
            pitchLinear, pitchQuadratic, pitchCubic, invert, drawingStart, null, drawingEnd);
        try
        {
            parameters.Validate();
        }
        catch (ArgumentException error)
        {
            throw new CalibrationUsageException(error.Message);
        }

        return parameters;
    }

    internal static CalibrationTarget CreateTarget(CalibrationParameters parameters)
    {
        try
        {
            return CalibrationTarget.Create(parameters);
        }
        catch (ArgumentException error)
        {
            throw new CalibrationUsageException(error.Message);
        }
    }

    /// <summary>The exact reference PNG cropped to the generated annulus, matching the
    /// Windows encoder's 2400-pixel image coordinates.</summary>
    internal static byte[] RenderEncodingImage(CalibrationTarget target)
    {
        int size = CalibrationReferencePattern.PixelSize;
        byte[] pixels = CalibrationReferencePattern.ReadGrayPixels();
        CalibrationParameters parameters = target.Parameters;
        double innerSquared = target.DrawingInnerRadiusMm * target.DrawingInnerRadiusMm;
        double outerSquared = target.DrawingOuterRadiusMm * target.DrawingOuterRadiusMm;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                CalibrationPoint point = CalibrationReferencePattern.PixelToDisc(
                    x, y, parameters.OuterRadiusMm, parameters.Kind);
                double radiusSquared = (point.X * point.X) + (point.Y * point.Y);
                if (radiusSquared < innerSquared - 1e-10 || radiusSquared > outerSquared + 1e-10)
                {
                    pixels[(y * size) + x] = 255;
                }
            }
        }

        return pixels;
    }

    /// <summary>Left-black / right-white coarse target through the disc centre, cropped to
    /// the generated annulus. The split is evaluated with the encoder's image conventions.</summary>
    internal static byte[] RenderCoarseImage(CalibrationParameters parameters, int size)
    {
        byte[] pixels = new byte[checked(size * size)];
        double centre = parameters.Kind == CalibrationDiscKind.Dvd
            ? (size - 1) / 2.0
            : size / 2.0;
        double scale = centre / parameters.OuterRadiusMm;
        double innerSquared = parameters.DrawingInnerRadiusMm * parameters.DrawingInnerRadiusMm;
        double outerSquared = parameters.DrawingOuterRadiusMm * parameters.DrawingOuterRadiusMm;
        for (int y = 0; y < size; y++)
        {
            double yMm = (y - centre) / scale;
            for (int x = 0; x < size; x++)
            {
                double xMm = (x - centre) / scale;
                double radiusSquared = (xMm * xMm) + (yMm * yMm);
                pixels[(y * size) + x] = radiusSquared >= innerSquared - 1e-10
                    && radiusSquared <= outerSquared + 1e-10 && xMm < 0 ? (byte)0 : (byte)255;
            }
        }

        return pixels;
    }

    internal static void SaveGrayPng(string path, int size, byte[] pixels)
    {
        string fullPath = Path.GetFullPath(path);
        EnsureParentDirectory(fullPath);
        RasterCodec.SavePng(fullPath, size, size, pixels, grayscale: true);
    }

    internal static BgraRaster LoadPhoto(string path, int maximumDimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        BgraRaster source;
        try
        {
            source = RasterCodec.LoadBgra32(Path.GetFullPath(path),
                maxFileBytes: 256L * 1024 * 1024, maxPixels: 64L * 1024 * 1024);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            throw new CalibrationUsageException($"Cannot read photo '{path}': {error.Message}");
        }

        if (maximumDimension is < 64 or > 4096)
        {
            throw new CalibrationUsageException("Photo dimension limit must be 64..4096 pixels.");
        }

        int longest = Math.Max(source.Width, source.Height);
        if (longest <= maximumDimension)
        {
            return source;
        }

        double scale = (double)maximumDimension / longest;
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return Resize(source, width, height);
    }

    internal static BgraRaster Resize(BgraRaster source, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            double sourceY = Math.Clamp(((y + 0.5) * source.Height / height) - 0.5, 0, source.Height - 1);
            int y0 = (int)Math.Floor(sourceY);
            int y1 = Math.Min(y0 + 1, source.Height - 1);
            double fy = sourceY - y0;
            for (int x = 0; x < width; x++)
            {
                double sourceX = Math.Clamp(((x + 0.5) * source.Width / width) - 0.5, 0, source.Width - 1);
                int x0 = (int)Math.Floor(sourceX);
                int x1 = Math.Min(x0 + 1, source.Width - 1);
                double fx = sourceX - x0;
                int destination = ((y * width) + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    double top = (source.Pixels[((y0 * source.Width) + x0) * 4 + channel] * (1 - fx))
                        + (source.Pixels[((y0 * source.Width) + x1) * 4 + channel] * fx);
                    double bottom = (source.Pixels[((y1 * source.Width) + x0) * 4 + channel] * (1 - fx))
                        + (source.Pixels[((y1 * source.Width) + x1) * 4 + channel] * fx);
                    pixels[destination + channel] = (byte)Math.Clamp(
                        (int)Math.Round((top * (1 - fy)) + (bottom * fy)), 0, 255);
                }
            }
        }

        return new(width, height, pixels);
    }

    internal static void SaveBgraPng(string path, BgraRaster image)
    {
        string fullPath = Path.GetFullPath(path);
        EnsureParentDirectory(fullPath);
        RasterCodec.SavePng(fullPath, image.Width, image.Height, image.Pixels, grayscale: false);
    }

    internal static BgraRaster Rectify(BgraRaster photo, CalibrationPhotoTransform transform,
        double canvasOuterRadiusMm, int size)
    {
        if (!double.IsFinite(canvasOuterRadiusMm) || canvasOuterRadiusMm is <= 0 or > 150)
        {
            throw new CalibrationUsageException("Rectification canvas radius is invalid.");
        }

        if (size is < 32 or > 4096)
        {
            throw new CalibrationUsageException("Rectified image size must be 32..4096 pixels.");
        }

        if (!transform.IsValidForDisc(canvasOuterRadiusMm))
        {
            throw new CalibrationUsageException(
                "The photo transform degenerates on this canvas; re-fit the contours or reduce the size.");
        }

        double[] matrix = transform.DiscToSourceMatrix9;
        byte[] pixels = new byte[checked(size * size * 4)];
        double step = 2 * canvasOuterRadiusMm / size;
        for (int y = 0; y < size; y++)
        {
            double yMm = ((y + 0.5) * step) - canvasOuterRadiusMm;
            for (int x = 0; x < size; x++)
            {
                double xMm = ((x + 0.5) * step) - canvasOuterRadiusMm;
                int destination = ((y * size) + x) * 4;
                double divisor = (matrix[6] * xMm) + (matrix[7] * yMm) + matrix[8];
                double sourceX = divisor == 0 ? double.NaN : ((matrix[0] * xMm) + (matrix[1] * yMm) + matrix[2]) / divisor;
                double sourceY = divisor == 0 ? double.NaN : ((matrix[3] * xMm) + (matrix[4] * yMm) + matrix[5]) / divisor;
                if ((xMm * xMm) + (yMm * yMm) > canvasOuterRadiusMm * canvasOuterRadiusMm
                    || !double.IsFinite(sourceX) || !double.IsFinite(sourceY)
                    || sourceX < 0 || sourceY < 0 || sourceX > photo.Width - 1 || sourceY > photo.Height - 1)
                {
                    pixels[destination] = pixels[destination + 1] = 255;
                    pixels[destination + 2] = pixels[destination + 3] = 255;
                    continue;
                }

                int x0 = (int)Math.Floor(sourceX), y0 = (int)Math.Floor(sourceY);
                int x1 = Math.Min(x0 + 1, photo.Width - 1), y1 = Math.Min(y0 + 1, photo.Height - 1);
                double fx = sourceX - x0, fy = sourceY - y0;
                for (int channel = 0; channel < 4; channel++)
                {
                    double value = (photo.Pixels[((y0 * photo.Width) + x0) * 4 + channel] * (1 - fx) * (1 - fy))
                        + (photo.Pixels[((y0 * photo.Width) + x1) * 4 + channel] * fx * (1 - fy))
                        + (photo.Pixels[((y1 * photo.Width) + x0) * 4 + channel] * (1 - fx) * fy)
                        + (photo.Pixels[((y1 * photo.Width) + x1) * 4 + channel] * fx * fy);
                    pixels[destination + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        }

        return new(size, size, pixels);
    }

    internal static int DiscSizeToOuterRadius(int discSize) => discSize switch
    {
        120 => 60,
        80 => 40,
        _ => throw new CalibrationUsageException("--disc-size must be 120 or 80."),
    };

    /// <summary>Reads width/height from a PNG header without decoding the whole image.</summary>
    internal static (int Width, int Height)? TryReadPngSize(string base64)
    {
        try
        {
            string head = base64.Length > 64 ? base64[..64] : base64;
            byte[] bytes = Convert.FromBase64String(head.PadRight((head.Length + 3) / 4 * 4, '='));
            if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
            {
                return null;
            }

            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0 ? (width, height) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static byte[] ReadPhotoPng(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CalibrationUsageException($"Cannot read photo '{path}': {error.Message}");
        }

        if (bytes.Length is 0 or > 14_000_000)
        {
            throw new CalibrationUsageException("The embedded calibration photo must be a PNG of at most 14 MB.");
        }

        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            throw new CalibrationUsageException("The calibration photo must be a PNG file.");
        }

        return bytes;
    }
}

internal sealed record CalibrationPointDto(double X, double Y);

internal sealed record CalibrationCandidateDto(
    double InnerRadiusMm, double OuterRadiusMm,
    double PitchLinear, double PitchQuadratic, double PitchCubic,
    double RotationRadians, double RmsErrorMm, double MaxErrorMm, int OutlierCount,
    long Sectors, bool InvertMapping,
    IReadOnlyList<CalibrationPointDto> Predicted);

internal sealed record CalibrationFitResultDto(
    bool Succeeded, bool Reliable, bool BoundaryLimited, IReadOnlyList<string> Warnings,
    int BestIndex, IReadOnlyList<CalibrationCandidateDto> Candidates);

internal sealed record CalibrationCoarseResultDto(
    bool Succeeded, IReadOnlyList<string> Warnings, int BestIndex,
    IReadOnlyList<CalibrationCandidateDto> Candidates);

internal sealed record CalibrationPhotoResultDto(
    bool Succeeded, string Message, int PhotoWidth, int PhotoHeight,
    int? RectifiedSize, double? CanvasOuterRadiusMm, int? DiscSize,
    double? OuterRmsPixels, double? HoleRmsPixels, double[]? TransformDiscToSource,
    string? ResizedPhotoPath, string? RectifiedImagePath);

internal sealed record CalibrationSessionSummaryDto(
    string? Family, double? InnerRadiusMm, double? OuterRadiusMm, long? Sectors, double? Density,
    double? PitchLinear, double? PitchQuadratic, double? PitchCubic, bool? InvertMapping,
    bool? HasPhoto, bool? RectificationConfirmed, int? TracePointCount,
    int? PhotoWidth, int? PhotoHeight, long? DrawingStartSector, long? DrawingEndSector);

internal sealed record CalibrationCoarseTargetP0Dto(
    string Family, double R0, double R1, long Sectors, double Density,
    double PitchLinear, double PitchQuadratic, double PitchCubic,
    long DrawingStart, long DrawingEnd, string OutputPath);
