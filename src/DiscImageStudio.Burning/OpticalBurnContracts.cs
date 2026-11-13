namespace DiscImageStudio.Burning;

public enum OpticalBurnMediaKind
{
    CdAudio,
    DvdData,
    BluRayData,
    CdData,
}

internal static class OpticalBurnMedia
{
    internal static bool UsesCdDao(OpticalBurnMediaKind mediaKind)
        => mediaKind is OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData;

    internal static int GetSectorBytes(OpticalBurnMediaKind mediaKind)
        => mediaKind switch
        {
            OpticalBurnMediaKind.CdAudio => 2352,
            OpticalBurnMediaKind.CdData or OpticalBurnMediaKind.DvdData or OpticalBurnMediaKind.BluRayData => 2048,
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind), mediaKind, "Unknown optical media kind."),
        };
}

public sealed record OpticalBurnDevice(
    string Id,
    string DisplayName,
    IReadOnlyList<string> VolumePaths);

/// <summary>
/// The medium's geometry extent and the current write limit are separate values.
/// WritableSectors is a payload limit, not a replacement for geometry or filesystem free space.
/// </summary>
public sealed record OpticalMediaCapacity(long TotalSectors, long WritableSectors);

public sealed record OpticalWriteSpeed(
    int SectorsPerSecond,
    bool RotationTypeIsPureCav)
{
    private const double DvdBytesPerSecondAtOneX = 1_385_000.0;
    private const double BluRayBytesPerSecondAtOneX = 4_500_000.0;
    private const double CdSectorsPerSecondAtOneX = 75.0;

    public double GetMultiplier(OpticalBurnMediaKind mediaKind)
        => mediaKind switch
        {
            OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData => SectorsPerSecond / CdSectorsPerSecondAtOneX,
            OpticalBurnMediaKind.DvdData => SectorsPerSecond * 2048.0 / DvdBytesPerSecondAtOneX,
            OpticalBurnMediaKind.BluRayData => SectorsPerSecond * 2048.0 / BluRayBytesPerSecondAtOneX,
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind), mediaKind, "Unknown optical media kind."),
        };

    public double GetMegabytesPerSecond(OpticalBurnMediaKind mediaKind)
    {
        int sectorBytes = OpticalBurnMedia.GetSectorBytes(mediaKind);
        return SectorsPerSecond * (double)sectorBytes / 1_000_000.0;
    }

    internal void Validate()
    {
        if (SectorsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SectorsPerSecond),
                "Write speed must be greater than zero sectors per second.");
        }
    }
}

/// <param name="SectorBytes">User bytes per sector of the data being written when it differs
/// from the media kind's default (a CD-Extra picture track writes 2048-byte Mode 1 sectors
/// in an otherwise 2352-byte audio job); used to convert the rate into a speed multiple.</param>
public sealed record OpticalBurnProgress(
    string Stage,
    string Message,
    long CompletedBytes,
    long TotalBytes,
    double? BytesPerSecond = null,
    int? SectorBytes = null)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

/// <summary>
/// CD audio producers supply headerless 44.1 kHz, 16-bit stereo little-endian PCM,
/// as required by IMAPI_CD_SECTOR_AUDIO. ContentLength excludes any WAV header.
/// CD data, DVD and Blu-ray producers supply complete 2048-byte logical data sectors.
/// The recording backend adds physical encoding and error correction.
/// </summary>
public sealed record OpticalBurnRequest(
    string DeviceId,
    OpticalBurnMediaKind MediaKind,
    long ContentLength,
    Action<Stream, CancellationToken> ProduceContent,
    OpticalWriteSpeed? WriteSpeed = null,
    OpticalBurnBufferOptions? BufferOptions = null)
{
    public OpticalBurnBufferOptions EffectiveBufferOptions => BufferOptions
        ?? (MediaKind == OpticalBurnMediaKind.BluRayData
            ? OpticalBurnBufferOptions.BluRayDefault : OpticalBurnBufferOptions.Default);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceId);
        ArgumentNullException.ThrowIfNull(ProduceContent);
        int sectorBytes = OpticalBurnMedia.GetSectorBytes(MediaKind);
        if (ContentLength <= 0 || ContentLength % sectorBytes != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContentLength),
                $"Stream length must be a positive multiple of {sectorBytes} bytes.");
        }

        WriteSpeed?.Validate();
        EffectiveBufferOptions.Validate();
    }
}

public sealed record OpticalBurnResult(
    string DeviceId,
    OpticalBurnMediaKind MediaKind,
    long BytesWritten,
    TimeSpan Elapsed,
    OpticalWriteSpeed? ActualWriteSpeed = null);

/// <summary>One CD-Extra music track. The producer writes exactly
/// <see cref="ContentLength"/> bytes of headerless 44.1 kHz, 16-bit stereo little-endian
/// PCM (WAV sample order), already padded with silence to whole 2352-byte sectors.</summary>
public sealed record CdExtraAudioTrack(long Sectors, Action<Stream, CancellationToken> ProducePcm)
{
    public const int SectorBytes = 2352;
    public const int MinimumSectors = 300;
    public long ContentLength => checked(Sectors * SectorBytes);
}

/// <summary>Where the picture session starts, read from the recorder after the audio
/// session was closed. <see cref="NextWritableAddress"/> is the absolute LBA of the data
/// track's first user sector; the picture and its filesystem must be generated for it.</summary>
public sealed record CdExtraSessionStart(
    long NextWritableAddress,
    long FreeBlocks,
    long AtipLeadoutStart,
    IReadOnlyList<long> AudioTrackStarts,
    bool Simulated = false);

/// <summary>The picture session's data track: complete 2048-byte Mode 1 user sectors that
/// start at the measured next writable address. The recorder adds headers, EDC/ECC and
/// scrambling.</summary>
public sealed record CdExtraDataContent(long Sectors, Action<Stream, CancellationToken> Produce)
{
    public const int SectorBytes = 2048;
    public long ContentLength => checked(Sectors * SectorBytes);
}

/// <summary>
/// Records an Enhanced CD (CD-Extra) in Track-at-Once mode on a blank CD-R/RW: the audio
/// tracks form session 1, which is closed with a pointer to a next session; the picture is
/// one Mode 1 data track in session 2, which finalizes the disc. The data producer is
/// created only after session 1 is closed, from the address the recorder reports.
/// With <see cref="TestWrite"/> the recorder only simulates every WRITE (no disc changes),
/// sessions are not closed and the data track uses the predicted address.
/// <see cref="PrepareAudio"/>, when set, runs once before the first audio WRITE: it checks
/// that the music sources are unchanged and returns an object that keeps them so until the
/// music session is written (it is disposed then). If it throws, nothing is written.
/// </summary>
public sealed record CdExtraBurnRequest(
    string DeviceId,
    IReadOnlyList<CdExtraAudioTrack> AudioTracks,
    Func<CdExtraSessionStart, CdExtraDataContent> CreateData,
    long PredictedSession2StartLba,
    long PredictedDataSectors,
    OpticalWriteSpeed? WriteSpeed = null,
    OpticalBurnBufferOptions? BufferOptions = null,
    bool TestWrite = false,
    Func<IDisposable>? PrepareAudio = null)
{
    public const int MaximumAudioTracks = 98;
    /// <summary>Run-out blocks the recorder writes behind every Track-at-Once track.</summary>
    public const int RunOutSectors = 2;
    /// <summary>The data track's post-gap, part of its recorded length.</summary>
    public const int DataPostgapSectors = 150;

    public OpticalBurnBufferOptions EffectiveBufferOptions => BufferOptions ?? OpticalBurnBufferOptions.Default;

    public long AudioBytes => AudioTracks.Sum(track => track.ContentLength);

    public long PredictedTotalBytes => checked(AudioBytes + PredictedDataSectors * CdExtraDataContent.SectorBytes);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceId);
        ArgumentNullException.ThrowIfNull(AudioTracks);
        ArgumentNullException.ThrowIfNull(CreateData);
        if (AudioTracks.Count is 0 or > MaximumAudioTracks)
            throw new ArgumentOutOfRangeException(nameof(AudioTracks),
                $"A CD-Extra disc needs 1..{MaximumAudioTracks} audio tracks.");
        foreach (CdExtraAudioTrack track in AudioTracks)
        {
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(track.ProducePcm);
            if (track.Sectors < CdExtraAudioTrack.MinimumSectors)
                throw new ArgumentOutOfRangeException(nameof(AudioTracks),
                    $"Each audio track needs at least {CdExtraAudioTrack.MinimumSectors} sectors.");
        }
        if (PredictedSession2StartLba <= 0 || PredictedDataSectors <= 0)
            throw new ArgumentOutOfRangeException(nameof(PredictedSession2StartLba),
                "The predicted picture session must start after LBA 0 and contain sectors.");
        WriteSpeed?.Validate();
        EffectiveBufferOptions.Validate();
    }
}

public sealed record CdExtraBurnResult(
    string DeviceId,
    IReadOnlyList<long> AudioTrackStarts,
    long Session2StartLba,
    long AudioBytesWritten,
    long DataBytesWritten,
    TimeSpan Elapsed,
    OpticalWriteSpeed? ActualWriteSpeed = null,
    bool TestWrite = false);

public interface IOpticalDiscBurner
{
    Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Read the loaded medium's total sector count without preparing it for writing.</summary>
    Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default);

    /// <summary>Read geometry and writable payload capacity without preparing or formatting media.
    /// A total-only implementation cannot safely infer the writable payload limit.</summary>
    Task<OpticalMediaCapacity> GetMediaCapacityAsync(string deviceId, OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This recorder does not expose writable media capacity.");

    Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default);

    Task<OpticalBurnResult> BurnAsync(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A recorder back end that can also record a two-session CD-Extra disc. It is a
/// separate capability, so a back end without it says so through its type and callers find
/// out before a burn is prepared rather than from an exception while it runs.</summary>
public interface ICdExtraBurner
{
    /// <summary>Records a two-session CD-Extra disc; see <see cref="CdExtraBurnRequest"/>.</summary>
    Task<CdExtraBurnResult> BurnCdExtraAsync(
        CdExtraBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
