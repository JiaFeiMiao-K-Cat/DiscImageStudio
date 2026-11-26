using System.Diagnostics;

namespace DiscImageStudio.Burning;

internal sealed class CdExtraPictureInterrupted(string message, Exception cause, CdExtraCheckpoint job,
    string path, OpticalWriteSpeed speed) : InvalidOperationException(message, cause)
{
    internal CdExtraCheckpoint Job { get; } = job;
    internal string Path { get; } = path;
    internal OpticalWriteSpeed Speed { get; } = speed;
}

// The callback borrows a newly initialized recorder for one attempt. All of its locks and
// COM references must be released before this delegate returns or propagates a failure.
internal delegate void OpenCdExtraRecorder(Action<DynamicCdExtraSession> use);

internal static class CdExtraReconnect
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    internal const int MaxUnavailablePolls = 60;
    private const int MaxAttemptsAtSamePosition = 3;

    internal static CdExtraBurnResult Burn(CdExtraBurnRequest request, CdExtraProgressTracker tracker, CancellationToken token,
        Func<CdExtraBurnResult> start, OpenCdExtraRecorder reopen, Action<TimeSpan, CancellationToken>? wait = null)
    {
        var elapsed = Stopwatch.StartNew();
        try { return start(); }
        catch (Exception original) when (!token.IsCancellationRequested && FindInterruption(original) is { } interrupted)
        {
            var job = interrupted.Job;
            tracker.Stage("reconnect", job.UncertainCommand is { } command
                ? $"图案命令 0x{command.Opcode:X2} 返回 0xC0AA0301，正在重连原光驱并核对已写扇区。图案断点：{interrupted.Path}"
                : $"图案刻录连接中断，保留当前任务并自动重连原光驱。图案断点：{interrupted.Path}");
            tracker.Stage("diagnostic", original.Message);
            var resumed = Resume(job, interrupted.Path, request.DeviceId, tracker, token, reopen,
                interrupted.Speed, interrupted.InnerException, wait);
            return new CdExtraBurnResult(job.DeviceId, Array.AsReadOnly(job.AudioStarts), job.StartLba,
                request.AudioBytes, resumed.Sectors * CdExtraDataContent.SectorBytes, elapsed.Elapsed, resumed.Speed)
            { VerifiedPictureSectors = resumed.Sectors };
        }
    }

    internal static (long Sectors, OpticalWriteSpeed? Speed) Resume(CdExtraCheckpoint job, string path, string deviceId,
        CdExtraProgressTracker tracker, CancellationToken token, OpenCdExtraRecorder open, OpticalWriteSpeed? speed = null,
        Exception? interruption = null, Action<TimeSpan, CancellationToken>? wait = null)
    {
        if (!string.Equals(deviceId, job.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择保存此断点时使用的刻录机。");
        tracker.Stage("media", "正在核对断点源镜像的 SHA-256…");
        int unavailable = 0, attempts = 0, attemptsAtPosition = 0;
        long lastPosition = -1;
        (byte Opcode, long Lba)? lastUnchangedRetry = null;
        bool lastWasSealed = false;
        wait ??= static (delay, cancel) => cancel.WaitHandle.WaitOne(delay);
        try
        {
            // Keep the same verified bytes pinned across every disconnect.
            using FileStream image = job.OpenReference(path, token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (interruption is not null)
                {
                    tracker.Stage("reconnect", $"正在等待并重新连接原光驱（第 {++attempts} 次），可取消。");
                    wait(PollInterval, token);
                    token.ThrowIfCancellationRequested();
                }
                try
                {
                    open(session => speed = session.ResumePicture(job, image, tracker, token, speed, ConfirmPosition));
                    break;
                }
                catch (Exception e) when (CdExtraWriteWindow.FromCommand(e, job) is { } uncertain)
                {
                    job = job.WithUncertainCommand(path, uncertain);
                    interruption = e;
                    tracker.Stage("diagnostic", $"图案命令 0x{uncertain.Opcode:X2} 返回 0xC0AA0301，重连后将核对 LBA {uncertain.FirstLba}–{uncertain.LimitLba}。");
                }
                catch (Exception e) when (!token.IsCancellationRequested && (IsTransportFailure(e) || (uint)e.GetBaseException().HResult == 0x80070005))
                {
                    interruption = e;
                    tracker.Stage("diagnostic", $"光驱连接尚未恢复或再次中断：{e.Message}");
                    if (++unavailable >= MaxUnavailablePolls)
                        throw new IOException($"连续 {unavailable} 次未能恢复原光驱的连接。", e);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException || token.IsCancellationRequested)
        { throw new OperationCanceledException($"图案续写或校验已取消。图案断点：{path}", e, token); }
        catch (Exception e) when (interruption is not null)
        { throw new IOException($"自动重连后未能完成图案：{e.Message} 图案断点：{path}", e); }
        CdExtraCheckpoint.RemoveScratch(path);
        tracker.Stage("complete", $"盘片已封盘，图案轨全部 {job.Sectors:N0} 个扇区读回校验通过。");
        return (job.Sectors, speed);

        void ConfirmPosition(long prefix, bool sealedDisc)
        {
            // A sustained outage is bounded independently of the duration of a healthy burn.
            unavailable = 0;
            if (prefix < lastPosition) throw new IOException("光驱报告的可写地址退回到此前已确认的位置之前。");
            if (prefix != lastPosition || sealedDisc != lastWasSealed)
            {
                lastPosition = prefix;
                lastWasSealed = sealedDisc;
                attemptsAtPosition = 0;
            }
            if (++attemptsAtPosition > MaxAttemptsAtSamePosition)
                throw new IOException($"光驱连续 {MaxAttemptsAtSamePosition} 次在同一位置未能完成操作（LBA {job.StartLba + prefix}）。");
            if (sealedDisc || job.UncertainCommand is not { } uncertain) return;
            if (job.StartLba + prefix == uncertain.FirstLba)
            {
                var retry = (uncertain.Opcode, uncertain.FirstLba);
                if (lastUnchangedRetry == retry)
                    throw new IOException($"光驱重试命令 0x{uncertain.Opcode:X2} 后仍返回 0xC0AA0301，位置仍为 LBA {uncertain.FirstLba}。已保留断点。");
                lastUnchangedRetry = retry;
            }
            // The old interval was checked against the disc twice. A new write may now
            // advance beyond it; a further ambiguous response records its own command.
            job = job.WithUncertainCommand(path, null);
        }
    }

    private static CdExtraPictureInterrupted? FindInterruption(Exception? error)
    {
        // A failed release may have added another diagnostic wrapper to the original error.
        for (; error is not null; error = error.InnerException)
            if (error is CdExtraPictureInterrupted interrupted) return interrupted;
        return null;
    }

    internal static bool IsTransportFailure(Exception error)
    {
        // The last hardware response is authoritative. A mismatched readback following a
        // timed-out WRITE is an integrity failure, even though the preceding WRITE timed out.
        error = error.GetBaseException();
        if (error.HResult == CdExtraWriteWindow.UnexpectedResponse) return false;
        if (error is MmcCommandException { HasValidSense: true } sense)
            return !sense.IsDeferredSense && (sense.IsOperationInProgress || sense.IsMediumNotPresent ||
                sense is { SenseKey: 6, AdditionalSenseCode: 0x29 } || sense.SenseKey == 0x0b);
        return error is TimeoutException or System.Runtime.InteropServices.InvalidComObjectException || (uint)error.HResult is
            0xc0aa020d or 0x80070079 or 0x800705b4 // command/Win32 timeout
            or 0x8007048f or 0x80070037 or 0x80070006 or 0x80070457 // disconnected, device missing, handle invalid, bus reset
            or 0x80010108 or 0x800706ba // stale or unavailable COM server
            or 0xc0aa0202 or 0xc0aa0205 or 0xc0aa0207 or 0x80070015 or 0x80070458 // no media, busy or becoming ready
            or 0xc0aa0210 or 0x80070020; // recorder locked or sharing violation
    }
}
