using System.Globalization;
using DiscImageStudio.AutoCalibration;
using DiscImageStudio.AutoCalibration.Drives;

namespace DiscImageStudio.Services;

public sealed record DiscDriveInfo(
    string Id,
    string Vendor,
    string Product,
    string Revision,
    bool MediaPresent,
    string MediaType,
    long TotalSectors,
    bool IsBlank,
    bool Simulated,
    bool Supported);

public sealed record CalibrationDiscCheck(uint Sectors, bool Foreign);

/// <summary>
/// Coordinates the C# auto-calibration engine with optical-drive access for the wizard. Debug mode
/// substitutes simulated measurement data and must not be interpreted as hardware calibration.
/// </summary>
public sealed class AutoCalibrationService
{
    private readonly IOpticalDriveProvider _driveProvider;

    public AutoCalibrationService(IOpticalDriveProvider? driveProvider = null) =>
        _driveProvider = driveProvider ?? OpticalDriveProvider.CreateForCurrentPlatform();

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<DiscDriveInfo>> ListDrivesAsync(
        bool debug,
        Action<string>? progress,
        CancellationToken cancellationToken = default,
        string mediaType = "dvd")
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (debug)
        {
            CalibrationMedia media = CalibrationMediaProfile.Parse(mediaType);
            string discType = media == CalibrationMedia.Cd ? "CD-R" : "DVD-R";
            return
            [
                new(
                    $"sim-{mediaType}",
                    "DiscImageStudio",
                    "模拟光驱",
                    "SIM",
                    MediaPresent: true,
                    MediaType: discType,
                    TotalSectors: CalibrationImageGenerator.DefaultSectors(media),
                    IsBlank: false,
                    Simulated: true,
                    Supported: true),
            ];
        }

        IReadOnlyList<CalibrationDriveInfo> drives = await Task.Run(
            () => _driveProvider.ListDrives(cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return drives.Select(drive => new DiscDriveInfo(
            drive.Id,
            drive.Vendor,
            drive.Product,
            drive.Revision,
            drive.MediaPresent,
            drive.MediaType,
            drive.TotalSectors,
            drive.IsBlank,
            drive.Simulated,
            drive.Supported)).ToArray();
    }

    public async Task<AutoCalibrationResult> MeasureAsync(
        string driveId,
        string mediaType,
        bool debug,
        (int MaxTargets, int Reps, double RangeLo, double RangeHi) options,
        Action<string>? progress,
        CancellationToken cancellationToken = default)
    {
        CalibrationMedia media = CalibrationMediaProfile.Parse(mediaType);
        var ladderOptions = new CalibrationLadderOptions(
            options.MaxTargets,
            options.Reps,
            options.RangeLo,
            options.RangeHi);
        if (!ladderOptions.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "自动标定高级参数无效。");
        }

        CalibrationMeasurementResult result = await Task.Run(() =>
        {
            using ITimedOpticalDrive drive = debug
                ? new SimulatedOpticalDrive(
                    media == CalibrationMedia.Cd ? CalibrationReferenceGeometry.Cd() : CalibrationReferenceGeometry.Dvd,
                    seed: 0x5EED,
                    paced: true)
                : _driveProvider.Open(driveId, cancellationToken);
            return CalibrationLadderRunner.Run(
                drive,
                media,
                ladderOptions,
                onRung: rung => progress?.Invoke(
                    $"[标定] 第 {rung.RungIndex + 1} / {rung.TotalRungs} 阶 LBA={rung.Lba} 半径={rung.RadiusMm:0.000} mm 相位={rung.PhaseDegrees:0.00}°"),
                onStatus: status => progress?.Invoke($"[标定] {status}"),
                cancellationToken: cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        if (result.SpecProblem is not null)
        {
            throw new InvalidOperationException($"盘片测量异常：{result.SpecProblem}");
        }

        if (result.Suspect)
        {
            throw new InvalidOperationException("测量结果不稳定（suspect），请重新读取标定盘后再试。");
        }

        return new AutoCalibrationResult(
            mediaType,
            result.Geometry.InnerRadiusMm,
            result.Geometry.OuterRadiusMm,
            result.TotalSectors,
            result.Geometry.PitchLinear,
            result.Geometry.PitchQuadratic,
            result.Geometry.PitchCubic,
            result.RmsResidualDegrees);
    }

    public async Task GenerateIsoAsync(
        string outputPath,
        string mediaType,
        Action<string>? progress,
        long? sectors = null,
        CancellationToken cancellationToken = default)
    {
        CalibrationMedia media = CalibrationMediaProfile.Parse(mediaType);
        uint count = sectors is > 0
            ? checked((uint)sectors.Value)
            : CalibrationImageGenerator.DefaultSectors(media);
        var reporter = new Progress<CalibrationImageProgress>(value =>
            progress?.Invoke(string.Format(
                CultureInfo.InvariantCulture,
                "[镜像] {0}% {1:0.0} MB/s",
                value.Percent,
                value.MegabytesPerSecond)));
        await Task.Run(
            () => CalibrationImageGenerator.GenerateAsync(outputPath, count, reporter, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalibrationDiscCheck> CheckAsync(
        string driveId,
        string mediaType,
        Action<string>? progress,
        CancellationToken cancellationToken = default)
    {
        CalibrationMedia media = CalibrationMediaProfile.Parse(mediaType);
        var reporter = new InlineProgress<CalibrationDiscCheckProgress>(value =>
            progress?.Invoke(string.Format(
                CultureInfo.InvariantCulture,
                "[校验] {0:0}% LBA={1}",
                value.Percent,
                value.Lba)));
        return await Task.Run(() =>
        {
            using ITimedOpticalDrive drive = _driveProvider.Open(driveId, cancellationToken);
            DiscImageStudio.AutoCalibration.CalibrationDiscCheck check =
                CalibrationDiscChecker.Check(drive, media, reporter, cancellationToken);
            return new CalibrationDiscCheck(check.Sectors, check.Foreign);
        }, cancellationToken).ConfigureAwait(false);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
