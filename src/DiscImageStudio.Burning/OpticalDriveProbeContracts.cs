namespace DiscImageStudio.Burning;

public enum OpticalDriveMediaFamily
{
    Unknown,
    Cd,
    Dvd,
    BluRay,
    HdDvd,
}

public enum OpticalDiscState
{
    Blank,
    Incomplete,
    Complete,
    Other,
}

public sealed record OpticalDriveInfo(
    string Id,
    string Vendor,
    string Product,
    string FirmwareRevision,
    IReadOnlyList<string> VolumePaths);

/// <summary>CD-R/RW pre-groove data. Pressed CDs do not publish ATIP information.
/// <see cref="LeadInTime"/> is the ATIP lead-in stamp, not a media identifier.</summary>
public sealed record CdAtipInfo(
    string LeadInTime,
    string LeadOutTime,
    long LeadOutSectors,
    bool IsRewritable);

/// <summary>
/// UsableSectors is the application's writable payload limit in the same caliber
/// as <see cref="OpticalMediaCapacity.WritableSectors"/>. FreeSectors is the
/// drive-reported free space and is not a replacement for that limit.
/// </summary>
public sealed record OpticalDriveMediaInfo(
    int Profile,
    string ProfileName,
    OpticalDriveMediaFamily Family,
    OpticalDiscState State,
    bool IsErasable,
    int Sessions,
    string? MediaId,
    string? MediaManufacturer,
    CdAtipInfo? Atip,
    long? TotalSectors,
    long? UsableSectors,
    long? FreeSectors,
    long? NextWritableAddress,
    IReadOnlyList<int> ReadSpeedsKbps,
    IReadOnlyList<int> WriteSpeedsKbps);

public sealed record OpticalDriveProbeResult(
    IReadOnlyList<OpticalDriveInfo> Drives,
    OpticalDriveInfo? Drive,
    OpticalDriveMediaInfo? Media);

/// <summary>
/// Reads informational media data only. No command in this path prepares,
/// formats, closes or otherwise modifies the medium.
/// </summary>
public interface IOpticalDriveProbe
{
    Task<OpticalDriveProbeResult> ProbeAsync(
        string? preferredDeviceId = null,
        CancellationToken cancellationToken = default);
}
