using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

public sealed partial class WindowsImapiBurner
{
    /// <summary>
    /// A picture-only audio CD with CD-TEXT. IMAPI's RawCD image creator cannot write R-W
    /// subcode, so the single audio track is written Session-at-Once through the same raw MMC
    /// path as CD-Extra and the disc is finalized. If the recorder cannot write CD-TEXT and the
    /// user agrees to burn without it, the request goes through the unchanged IMAPI DAO path.
    /// </summary>
    private static OpticalBurnResult BurnCdAudioWithCdText(OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress, CancellationToken cancellationToken)
    {
        var tracker = new CdExtraProgressTracker(value => progress?.Report(value), request.ContentLength, 0);
        tracker.Stage("device", "正在检查刻录机和空白 CD…");
        dynamic recorder = ImapiCom.CreateRecorder(request.DeviceId);
        dynamic? format = null;
        List<string> cleanupWarnings = [];
        Exception? failure = null;
        OpticalBurnResult? result = null;
        string? skipped = null;
        try
        {
            BurnDeviceDiagnostics.Report(message => tracker.Stage("diagnostic", message),
                () => BurnDeviceDiagnostics.DescribeRecorder((object)recorder));
            format = ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            ValidateRequestedMedia(OpticalBurnMediaKind.CdAudio, recorder, format);
            if (!Convert.ToBoolean(format.MediaPhysicallyBlank))
                throw new InvalidOperationException("CD 不是空白盘，已取消刻录。");
            OpticalWriteSpeed? requested = request.WriteSpeed
                ?? ResolveWriteSpeed(null, ReadSupportedWriteSpeeds(format));
            using DynamicCdExtraSession session = new((object)recorder, (object)format, tracker.ReportAccepted,
                cleanupWarnings.Add, message => tracker.Stage("diagnostic", message));
            (result, skipped) = BurnCdAudioSessionAtOnce(session, request with { WriteSpeed = requested }, tracker, cancellationToken);
        }
        catch (COMException exception)
        {
            failure = new InvalidOperationException(
                $"Windows 刻录接口返回错误 0x{exception.HResult:X8}：{exception.Message}", exception);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            CompleteBurnCleanup(failure, cleanupWarnings,
                () => { if (format is not null) ImapiCom.ReleaseComObject((object)format); },
                () => ImapiCom.ReleaseComObject((object)recorder));
        }

        if (result is not null) return result;
        // The recorder cannot write CD-TEXT and the user chose to burn without it: nothing was
        // written, the medium is still blank, and the original IMAPI path takes over.
        OpticalBurnResult fallback = Burn(request with { CdText = null }, progress, cancellationToken);
        return fallback with { CdText = new CdTextBurnOutcome(false, null, null, skipped) };
    }

    /// <summary>Returns null (and the reason) when CD-TEXT cannot be written and the caller
    /// should use the IMAPI path; otherwise the finished, finalized burn.</summary>
    internal static (OpticalBurnResult? Result, string? Skipped) BurnCdAudioSessionAtOnce(ICdExtraSession session,
        OpticalBurnRequest request, CdExtraProgressTracker tracker, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        string operation = "准备 CD 刻录";
        bool acquired = false, writeStarted = false;
        Exception? failure = null;
        long sectors = request.ContentLength / CdSaoWriter.AudioBlockBytes;
        try
        {
            Set("media", "锁定刻录机");
            session.Acquire();
            acquired = true;
            CdExtraMediaInfo media = session.ReadMedia();
            Diagnose($"CD 介质：Profile=0x{media.Profile:X4}；DiscStatus={media.DiscStatus}；AtipLeadoutStart={media.AtipLeadoutStart}。");
            if (!DynamicCdExtraSession.IsRecordableCdProfile(media.Profile))
                throw new InvalidOperationException($"需要 CD-R 或 CD-RW 盘片（当前 Profile 0x{media.Profile:X4}）。");
            if (media.DiscStatus != CdDiscInformation.Empty)
                throw new InvalidOperationException("CD 不是空白盘，已取消刻录。");
            Set("speed", "设置写入速度");
            OpticalWriteSpeed speed = session.ConfigureWrite(request.WriteSpeed);
            tracker.Stage("speed", $"设备设置速度={speed.GetMultiplier(OpticalBurnMediaKind.CdAudio):0.#}×（设备返回的设置值）。");

            (CdSaoLayout? layout, long leadIn, string? skipped) = PrepareSessionAtOnce(session, request.CdText!,
                [sectors], finalize: true, testWrite: false, request.ConfirmWithoutCdText, Set, Diagnose,
                cancellationToken, leadoutLimit: media.AtipLeadoutStart);
            if (layout is null) return (null, skipped);

            writeStarted = true;
            long written = WriteSessionAtOnce(session, layout, leadIn, request.CdText!,
                [(sectors, request.ContentLength, request.ProduceContent)], request.EffectiveBufferOptions, tracker, Set,
                cancellationToken);
            Set("finalize", "完成会话并封盘（光驱写入含 CD-TEXT 的导入区与导出区，请等待完成）");
            session.CompleteSessionAtOnce(finalize: true);
            CdTextBurnOutcome outcome = ReadBackCdText(session, Set, Diagnose);
            Set("release", "释放刻录机");
            tracker.Stage("complete", "音频 CD 已写入并封盘（含 CD-TEXT）。");
            return (new OpticalBurnResult(request.DeviceId, OpticalBurnMediaKind.CdAudio, written, timer.Elapsed, speed)
                { CdText = outcome }, null);
        }
        catch (Exception exception)
        {
            failure = exception;
            string state = writeStarted ? "SAO 会话已开始写入，这张盘可能无法使用。" : "尚未向盘片写入数据。";
            if (exception is OperationCanceledException || cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException($"CD 刻录已取消（{operation}）。{state}", exception, cancellationToken);
            throw new InvalidOperationException($"CD 刻录在“{operation}”时失败：{exception.Message}{state}", exception);
        }
        finally
        {
            if (acquired)
            {
                if (failure is not null && writeStarted)
                {
                    try { session.Flush(); }
                    catch (Exception flushFailure) { Diagnose($"失败后清理：Flush failed；{flushFailure.Message}"); }
                }
                try { session.Release(); }
                catch (Exception releaseFailure) when (failure is not null)
                {
                    Diagnose($"失败后清理：Release failed；{releaseFailure.Message}");
                }
            }
        }

        void Set(string stage, string value)
        {
            operation = value;
            tracker.Stage(stage, value + "…");
        }

        void Diagnose(string message)
        {
            try { tracker.Stage("diagnostic", message); }
            catch { }
        }
    }
}
