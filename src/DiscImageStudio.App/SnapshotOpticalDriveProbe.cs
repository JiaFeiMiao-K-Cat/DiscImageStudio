using System.IO;
using DiscImageStudio.Burning;

namespace DiscImageStudio;

/// <summary>Deterministic drive results for ui-snapshot. No recorder, COM object or medium is
/// touched; the fixture feeds the same probe contract the IMAPI implementation fulfils so the
/// card renders through its normal view-model path.</summary>
internal sealed class SnapshotOpticalDriveProbe : IOpticalDriveProbe
{
    private readonly string _fixture;

    internal SnapshotOpticalDriveProbe(string fixture)
    {
        _fixture = fixture switch
        {
            "none" or "empty" or "cd" or "dvd" or "bd" or "error" => fixture,
            _ => throw new ArgumentException(
                "Drive fixture must be none, empty, cd, dvd, bd or error.", nameof(fixture)),
        };
    }

    public Task<OpticalDriveProbeResult> ProbeAsync(
        string? preferredDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fixture == "none")
        {
            return Task.FromResult(new OpticalDriveProbeResult([], null, null));
        }

        if (_fixture == "error")
        {
            return Task.FromException<OpticalDriveProbeResult>(
                new IOException("Snapshot drive fixture: probe failure."));
        }

        OpticalDriveInfo drive = new(
            "snapshot-drive-0", "PIONEER", "BD-RW BDR-XD07", "1.03", ["D:\\"]);
        OpticalDriveMediaInfo? media = _fixture switch
        {
            "empty" => null,
            "cd" => new OpticalDriveMediaInfo(
                0x09, "CD-R", OpticalDriveMediaFamily.Cd, OpticalDiscState.Blank,
                IsErasable: false, Sessions: 0, MediaId: null, MediaManufacturer: null,
                Atip: new CdAtipInfo("97m34s21f", "79:59:71", 359996, IsRewritable: false),
                TotalSectors: 359996, UsableSectors: 359994, FreeSectors: 359846,
                NextWritableAddress: 0,
                ReadSpeedsKbps: [4234],
                WriteSpeedsKbps: [8467, 4234, 2822]),
            "dvd" => new OpticalDriveMediaInfo(
                0x1b, "DVD+R", OpticalDriveMediaFamily.Dvd, OpticalDiscState.Blank,
                IsErasable: false, Sessions: 0,
                MediaId: "MBIPG101 R05", MediaManufacturer: "Moser Baer India",
                Atip: null,
                TotalSectors: 2295104, UsableSectors: 2295104, FreeSectors: 2295104,
                NextWritableAddress: 0,
                ReadSpeedsKbps: [11080],
                WriteSpeedsKbps: [22160, 11080, 5540]),
            _ => new OpticalDriveMediaInfo(
                0x41, "BD-R", OpticalDriveMediaFamily.BluRay, OpticalDiscState.Blank,
                IsErasable: false, Sessions: 0,
                MediaId: null, MediaManufacturer: null,
                Atip: null,
                TotalSectors: 12219392, UsableSectors: 12219392, FreeSectors: 12219392,
                NextWritableAddress: 0,
                ReadSpeedsKbps: [17983],
                WriteSpeedsKbps: [35965, 17983]),
        };
        return Task.FromResult(new OpticalDriveProbeResult([drive], drive, media));
    }
}
