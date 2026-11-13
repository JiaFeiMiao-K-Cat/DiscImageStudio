using System.IO;
using System.Security;
using DiscImageStudio.Cd;
using DiscImageStudio.Localization;

namespace DiscImageStudio.Services;

/// <summary>One music file as read for the design page. <see cref="Error"/> is worded each
/// time it is read, so a language switch re-words it without reading the file again.</summary>
internal sealed record CdExtraTrackProbe(string Path, CdMusicTrackInfo? Info, Exception? Failure = null)
{
    public string? Error => Failure is null ? null : CdExtraPlanner.DescribeProbeError(Failure);
}

internal enum CdExtraPlanProblem
{
    None,
    NoMusic,
    UnreadableMusic,
    /// <summary>The music leaves too little room for the picture session.</summary>
    Capacity,
    /// <summary>The picture session fits, but not together with the disc files.</summary>
    DataCapacity,
    /// <summary>The picture session's filesystem cannot be planned: the folder is missing or
    /// unreadable, the volume label is invalid, or a file is not supported.</summary>
    DataError,
}

/// <summary>A CD-Extra plan for previews, export and burning: the music layout of session 1
/// and the picture session's data track. Without a measured address the session 2 start is
/// the Track-at-Once prediction; the burner replaces it with the recorder's value. With
/// <see cref="CdExtraPlanProblem.DataCapacity"/> or <see cref="CdExtraPlanProblem.DataError"/>
/// the music layout is still known, so the rings and shares can be shown with the problem.</summary>
internal sealed record CdExtraPlan(
    IReadOnlyList<CdExtraTrackProbe> Tracks,
    CdExtraLayout? Layout,
    CdDataLayoutPlan? DataPlan,
    CdExtraPlanProblem Problem,
    Exception? DataFailure = null)
{
    public bool IsValid => Layout is not null && DataPlan is not null && Problem == CdExtraPlanProblem.None;

    public TimeSpan MusicDuration => TimeSpan.FromTicks(Tracks.Sum(track => track.Info?.Duration.Ticks ?? 0));
}

/// <summary>Single CD-Extra planning path shared by the design guide, previews, export and
/// burning, so all of them see the same rings and the same picture session.</summary>
internal static class CdExtraPlanner
{
    internal static IReadOnlyList<CdExtraTrackProbe> Probe(IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<CdExtraTrackProbe> probes = new(paths.Count);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                probes.Add(new CdExtraTrackProbe(path, CdMusicFile.Inspect(path)));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                probes.Add(new CdExtraTrackProbe(path, null, exception));
            }
        }
        return probes;
    }

    internal static CdExtraPlan Plan(CdDiscParameters parameters, IReadOnlyList<string> trackPaths,
        string? dataDirectory, string volumeLabel, bool filesAtOuterRing,
        long? measuredSession2StartLba = null, long? dataTrackSectorLimit = null,
        CancellationToken cancellationToken = default)
        => Plan(parameters, Probe(trackPaths, cancellationToken), dataDirectory, volumeLabel, filesAtOuterRing,
            measuredSession2StartLba, dataTrackSectorLimit);

    internal static CdExtraPlan Plan(CdDiscParameters parameters, IReadOnlyList<CdExtraTrackProbe> probes,
        string? dataDirectory, string volumeLabel, bool filesAtOuterRing,
        long? measuredSession2StartLba = null, long? dataTrackSectorLimit = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(probes);
        if (probes.Count == 0)
            return new CdExtraPlan(probes, null, null, CdExtraPlanProblem.NoMusic);
        if (probes.Any(probe => probe.Info is null))
            return new CdExtraPlan(probes, null, null, CdExtraPlanProblem.UnreadableMusic);
        if (probes.Count > CdExtraLayout.MaximumAudioTracks)
            return new CdExtraPlan(probes, null, null, CdExtraPlanProblem.Capacity);

        CdExtraLayout layout;
        try
        {
            layout = CdExtraLayout.Plan(parameters,
                probes.Select(probe => probe.Info!.RecordedSectors).ToArray(),
                measuredSession2StartLba, dataTrackSectorLimit);
        }
        catch (Exception exception) when (IsCapacity(exception))
        {
            return new CdExtraPlan(probes, null, null, CdExtraPlanProblem.Capacity, exception);
        }

        try
        {
            CdDataLayoutPlan dataPlan = PrepareSession(parameters, layout, dataDirectory, volumeLabel, filesAtOuterRing);
            return new CdExtraPlan(probes, layout, dataPlan, CdExtraPlanProblem.None);
        }
        catch (Exception exception) when (IsCapacity(exception))
        {
            // When an empty filesystem still fits, the folder is what does not fit;
            // otherwise the music left too little room for the picture session.
            return dataDirectory is not null && FitsWithoutFiles(parameters, layout, volumeLabel)
                ? new CdExtraPlan(probes, layout, null, CdExtraPlanProblem.DataCapacity, exception)
                : new CdExtraPlan(probes, null, null, CdExtraPlanProblem.Capacity, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or SecurityException)
        {
            return new CdExtraPlan(probes, layout, null, CdExtraPlanProblem.DataError, exception);
        }
    }

    /// <summary>The localized reason a plan cannot be exported, previewed or burned.</summary>
    internal static string DescribeProblem(CdExtraPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Problem switch
        {
            CdExtraPlanProblem.NoMusic => L.T("CD-Extra 需要至少一首音乐：请在设计页添加 WAV 音乐，或切换回“整盘图片”。"),
            CdExtraPlanProblem.UnreadableMusic => L.T("有音乐文件无法使用，请删除或替换标红的音轨。"),
            CdExtraPlanProblem.DataCapacity => L.T("随盘文件太大：图片区段放不下这些文件。"),
            CdExtraPlanProblem.DataError => L.F($"图片区段无法规划：{plan.DataFailure?.Message}"),
            _ => L.T("音乐太长：音乐区之后没有足够空间写入图片区段。"),
        };
    }

    internal static string DescribeProbeError(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => L.T("文件不存在"),
        InvalidDataException => L.T("需要 44.1 kHz、16 位、立体声 PCM 的 WAV 文件"),
        UnauthorizedAccessException => L.T("没有读取权限"),
        _ => exception.Message,
    };

    private static CdDataLayoutPlan PrepareSession(CdDiscParameters parameters, CdExtraLayout layout,
        string? dataDirectory, string volumeLabel, bool filesAtOuterRing)
        => CdDataImageGenerator.PrepareSessionLayout(parameters, layout.Session2StartLba, layout.DataTrackSectors,
            dataDirectory, volumeLabel, filesAtOuterRing);

    private static bool FitsWithoutFiles(CdDiscParameters parameters, CdExtraLayout layout, string volumeLabel)
    {
        try
        {
            PrepareSession(parameters, layout, null, volumeLabel, filesAtOuterRing: false);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }
    }

    private static bool IsCapacity(Exception exception)
        => exception is CdExtraCapacityException
            || exception is ArgumentException argument
                && (argument.Message.Contains("leaves no CD sectors for drawing")
                    || argument.Message.Contains("exceed the disc capacity")
                    || argument.Message.Contains("disc has only")
                    || argument.Message.Contains("too small for filesystem metadata")
                    || argument.Message.Contains("does not fit the"));
}
