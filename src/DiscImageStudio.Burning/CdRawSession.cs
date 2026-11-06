using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

internal interface ICdRawSession
{
    bool MediaPhysicallyBlank { get; }

    long LastPossibleStartOfLeadout { get; }

    IReadOnlyList<int> SupportedSectorTypes { get; }

    int CurrentWriteSpeed { get; }

    bool CurrentRotationTypeIsPureCav { get; }

    void PrepareMedia();

    void SetRequestedSectorType(int sectorType);

    void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav);

    void WriteMedia(IStream content);

    void CancelWrite();

    void ReleaseMedia();

    // Read only while prepared and before writing. Fakes need not provide telemetry.
    string? GetDiagnosticSummary() => null;
}

// IMAPI_FORMAT2_RAW_CD_DATA_SECTOR_TYPE (imapi2.h). Both values use 2448-byte sectors:
// 2352 main-channel bytes followed by 96 P-W subchannel bytes. PQ_ONLY (1) uses 2368-byte
// sectors, which the DAO image streams do not support.
// https://learn.microsoft.com/windows/win32/api/imapi2/ne-imapi2-imapi_format2_raw_cd_data_sector_type
internal static class CdRawSectorType
{
    internal const int SubcodeIsCooked = 2;
    internal const int SubcodeIsRaw = 3;

    internal static bool IsSupported(int sectorType)
        => sectorType is SubcodeIsCooked or SubcodeIsRaw;
}

internal interface ICdRawImageSession : IDisposable
{
    long StartOfLeadout { get; }

    // sectorType must equal the type requested from the RawCD format. IMAPI cannot
    // detect a mismatch because both supported layouts use 2448-byte sectors.
    IStream CreateImage(IStream content, int sectorType);
}

internal sealed class DynamicCdRawSession(object format, Action<object>? reportWriteProgress = null) : ICdRawSession
{
    private readonly dynamic _format = format;

    public bool MediaPhysicallyBlank => Convert.ToBoolean(_format.MediaPhysicallyBlank);

    public long LastPossibleStartOfLeadout =>
        Convert.ToInt64(_format.LastPossibleStartOfLeadout);

    public IReadOnlyList<int> SupportedSectorTypes
    {
        get
        {
            if (_format.SupportedSectorTypes is not Array values)
            {
                return [];
            }

            return values.Cast<object>().Select(Convert.ToInt32).ToArray();
        }
    }

    public int CurrentWriteSpeed => Convert.ToInt32(_format.CurrentWriteSpeed);

    public bool CurrentRotationTypeIsPureCav =>
        Convert.ToBoolean(_format.CurrentRotationTypeIsPureCAV);

    public void PrepareMedia() => _format.PrepareMedia();

    public void SetRequestedSectorType(int sectorType) =>
        _format.RequestedSectorType = sectorType;

    public void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav) =>
        _format.SetWriteSpeed(sectorsPerSecond, rotationTypeIsPureCav);

    public void WriteMedia(IStream content)
    {
        if (reportWriteProgress is null)
        {
            _format.WriteMedia(content);
            return;
        }
        // DDiscFormat2RawCDEvents.Update (Windows SDK imapi2.idl).
        Guid events = new("27354142-7F64-5B0F-8F00-5D77AFBE261E");
        Action<object, object> update = (_, args) => reportWriteProgress(args);
        bool subscribed = false;
        try
        {
            ComEventsHelper.Combine((object)_format, events, 0x200, update);
            subscribed = true;
        }
        catch (COMException) { /* Optional telemetry must not prevent writing. */ }
        try { _format.WriteMedia(content); }
        finally
        {
            if (subscribed)
            {
                try { ComEventsHelper.Remove((object)_format, events, 0x200, update); }
                catch (COMException) { /* Preserve any write error. */ }
            }
        }
    }

    public void CancelWrite() => _format.CancelWrite();

    public void ReleaseMedia() => _format.ReleaseMedia();

    public string GetDiagnosticSummary() => BurnDeviceDiagnostics.DescribePreparedMedia((object)_format);
}

internal sealed class DynamicCdRawImageSession : ICdRawImageSession
{
    private const int AudioSectorType = 0;

    private readonly dynamic _creator;
    private object? _resultImage;
    private bool _disposed;

    internal DynamicCdRawImageSession()
    {
        Type type = Type.GetTypeFromProgID(
            "IMAPI2.MsftRawCDImageCreator",
            throwOnError: true)!;
        _creator = Activator.CreateInstance(type)!;
    }

    public long StartOfLeadout => Convert.ToInt64(_creator.StartOfLeadout);

    internal int ResultingImageType => Convert.ToInt32(_creator.ResultingImageType);

    public IStream CreateImage(IStream audioContent, int sectorType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audioContent);
        if (!CdRawSectorType.IsSupported(sectorType))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorType), sectorType,
                "CD DAO images require cooked R-W or raw P-W 2448-byte sectors.");
        }

        // The creator becomes read-only after CreateResultImage. It writes no R-W data, so
        // cooked and raw images carry the same bytes. This value never reaches the recorder:
        // the caller must request the same type via IDiscFormat2RawCD.RequestedSectorType
        // (default: cooked R-W), which is what announces the subchannel layout.
        _creator.ResultingImageType = sectorType;
        // AddTrack expects WAV-order PCM without a header. The RAW image creator
        // preserves the supplied sample bytes; it does not correct big-endian input.
        _creator.AddTrack(AudioSectorType, audioContent);
        _resultImage = _creator.CreateResultImage();
        return (IStream)_resultImage;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_resultImage is not null && Marshal.IsComObject(_resultImage))
        {
            Marshal.FinalReleaseComObject(_resultImage);
        }

        if (Marshal.IsComObject(_creator))
        {
            Marshal.FinalReleaseComObject(_creator);
        }
    }
}
