using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

internal interface IDvdDataSession
{
    int CurrentMediaStatus { get; }

    long FreeSectorsOnMedia { get; }

    long TotalSectorsOnMedia { get; }

    void ConfigureMedia(bool forceOverwrite);

    void DisableVerification();

    void SetWriteSpeed(OpticalWriteSpeed speed);

    OpticalWriteSpeed CurrentWriteSpeed { get; }

    void Write(IStream content, Action<Exception>? reportFailure = null);

    void CancelWrite();

    // Optional reads are made before writing, never from event or failure callbacks.
    string? GetDiagnosticSummary() => null;
}

internal sealed class DynamicDvdDataSession(object format, Action<object>? reportWriteProgress = null)
    : IDvdDataSession
{
    private readonly dynamic _format = format;

    public int CurrentMediaStatus => Convert.ToInt32(_format.CurrentMediaStatus);

    public long FreeSectorsOnMedia => Convert.ToInt64(_format.FreeSectorsOnMedia);

    public long TotalSectorsOnMedia => Convert.ToInt64(_format.TotalSectorsOnMedia);

    // ForceOverwrite is the only IMAPI switch that lets a formatted DVD+RW/RW be
    // written again; without it WriteMedia fails with E_IMAPI_DF2DATA_MEDIA_NOT_BLANK.
    public void ConfigureMedia(bool forceOverwrite)
    {
        _format.BufferUnderrunFreeDisabled = false;
        _format.ForceMediaToBeClosed = true;
        _format.ForceOverwrite = forceOverwrite;
    }

    public void DisableVerification() => DvdBurnVerification.Disable((object)_format);

    public void SetWriteSpeed(OpticalWriteSpeed speed) =>
        _format.SetWriteSpeed(speed.SectorsPerSecond, speed.RotationTypeIsPureCav);

    public OpticalWriteSpeed CurrentWriteSpeed => new(
        Convert.ToInt32(_format.CurrentWriteSpeed),
        Convert.ToBoolean(_format.CurrentRotationTypeIsPureCAV));

    public void Write(IStream content, Action<Exception>? reportFailure = null)
    {
        void WriteContent()
        {
            try { _format.Write(content); }
            catch (Exception exception)
            {
                // Capture before event removal or cancellation registration disposal can block.
                try { reportFailure?.Invoke(exception); }
                catch { /* Optional diagnostics must preserve the original write exception. */ }
                throw;
            }
        }

        if (reportWriteProgress is null)
        {
            WriteContent();
            return;
        }

        // DDiscFormat2DataEvents.Update (Windows SDK imapi2.idl).
        Guid events = new("2735413C-7F64-5B0F-8F00-5D77AFBE261E");
        Action<object, object> update = (_, args) => reportWriteProgress(args);
        bool subscribed = false;
        try
        {
            ComEventsHelper.Combine((object)_format, events, 0x200, update);
            subscribed = true;
        }
        catch (COMException) { /* Optional telemetry must not prevent writing. */ }
        try { WriteContent(); }
        finally
        {
            if (subscribed)
            {
                try { ComEventsHelper.Remove((object)_format, events, 0x200, update); }
                catch (COMException) { /* Preserve any write error. */ }
            }
        }
    }

    public void CancelWrite()
    {
        try { _format.CancelWrite(); }
        catch { /* Cancellation must not replace the original operation outcome. */ }
    }

    public string GetDiagnosticSummary() => BurnDeviceDiagnostics.DescribeDvdSettings((object)_format);
}
