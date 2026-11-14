using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.AutoCalibration;

/// <summary>Writes a Discratcher measurement out as the three DiscImageStudio auxiliary
/// calibration records (粗标定 / 边界标定 / 精细标定). Each record keeps only the measured
/// geometry into a freshly generated target: there are no pixel observations and no photo,
/// so opening it hands every stage a ready-to-edit base target without locking in any markings.</summary>
public static class CalibrationSessionExporter
{
    public const string FileExtension = ".calibration.json";

    public sealed record Stage(string Suffix, CalibrationArtwork Artwork, double RadiusRangeMm, bool? AutoRange);

    /// <summary>The suffix matches DiscImageStudio's auxiliary calibration page names so each
    /// exported record is opened by the stage it belongs to. Radius/AutoRange echo the values
    /// the in-app workflow uses when it first creates that stage's target.</summary>
    public static readonly IReadOnlyList<Stage> Stages =
    [
        new("粗标定", CalibrationArtwork.AngleSweepBands, 0.02, null),
        new("边界标定", CalibrationArtwork.WavyBoundary, 0.01, false),
        new("精细标定", CalibrationArtwork.ReferenceCross, 0.02, null),
    ];

    /// <summary>Exports three records into <paramref name="directory"/> and returns the written
    /// file names. A record is a CalibrationSession with an empty observation list and a null
    /// photo, so it round-trips through the same .calibration.json import path as a manual one.</summary>
    public static IReadOnlyList<string> Export(
        AutoCalibrationResult result, string projectName, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("导出文件夹不能为空。", nameof(directory));

        string safeProjectName = SanitizeProjectName(projectName);
        Directory.CreateDirectory(directory);

        string[] paths = Stages
            .Select(stage => $"{safeProjectName}-{stage.Suffix}-auto{FileExtension}")
            .ToArray();
        string[] existingFiles = paths
            .Where(fileName => File.Exists(Path.Combine(directory, fileName)))
            .ToArray();
        if (existingFiles.Length > 0)
        {
            throw new IOException(
                $"为避免覆盖已有数据，导出已取消：{string.Join("、", existingFiles)}");
        }

        IReadOnlyList<CalibrationSession> sessions = CreateSessions(result);
        for (int i = 0; i < Stages.Count; i++)
        {
            CalibrationSessionJson.Save(Path.Combine(directory, paths[i]), sessions[i]);
        }

        return paths;
    }

    public static IReadOnlyList<CalibrationSession> CreateSessions(AutoCalibrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        CalibrationParameters parameters = AutoCalibrationGeometry.ToParameters(result);
        var sessions = new List<CalibrationSession>(Stages.Count);
        foreach (Stage stage in Stages)
        {
            CalibrationTarget target = CalibrationTarget.Create(parameters, artwork: stage.Artwork);
            var fitOptions = new CalibrationFitOptions(
                SearchCenter: parameters, RadiusRangeMm: stage.RadiusRangeMm, AutoRange: stage.AutoRange);
            var session = new CalibrationSession(target, [], fitOptions);
            session.Validate();
            sessions.Add(session);
        }

        return sessions;
    }

    private static string SanitizeProjectName(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("工程名称不能为空。", nameof(projectName));

        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        invalid.UnionWith(['<', '>', ':', '"', '/', '\\', '|', '?', '*']);
        string safeName = new(projectName.Trim().Select(character =>
            char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray());
        safeName = safeName.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
            throw new ArgumentException("工程名称无法用于导出文件名。", nameof(projectName));
        return safeName;
    }
}