using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

/// <summary>Aggregates CD-Extra progress across audio tracks and the picture session.
/// Byte counts are WRITE commands acknowledged by the recorder, as for BD-R.</summary>
internal sealed class CdExtraProgressTracker
{
    private readonly Action<OpticalBurnProgress>? _report;
    private readonly long _audioBytes;
    private readonly Func<TimeSpan> _elapsed;
    private long _totalBytes;
    private long _phaseBase;
    private long _completed;
    private string _label = "正在准备";
    private int _sectorBytes = CdExtraAudioTrack.SectorBytes;
    // Null until the first report: TimeSpan.MinValue would overflow in `now - last`.
    private TimeSpan? _lastReport;
    private (TimeSpan Time, long Bytes)? _rateStart;

    internal CdExtraProgressTracker(Action<OpticalBurnProgress>? report, long audioBytes, long predictedDataBytes,
        Func<TimeSpan>? elapsed = null)
    {
        _report = report;
        _audioBytes = audioBytes;
        _totalBytes = checked(audioBytes + Math.Max(0, predictedDataBytes));
        long started = Stopwatch.GetTimestamp();
        _elapsed = elapsed ?? (() => Stopwatch.GetElapsedTime(started));
    }

    internal long AudioBytes => _audioBytes;
    internal long CompletedBytes => _completed;
    internal long TotalBytes => _totalBytes;

    internal void SetDataBytes(long dataBytes) => _totalBytes = checked(_audioBytes + Math.Max(0, dataBytes));

    internal void BeginPhase(string label, long baseBytes, int sectorBytes = CdExtraAudioTrack.SectorBytes)
    {
        _label = label;
        _phaseBase = baseBytes;
        _completed = baseBytes;
        _sectorBytes = sectorBytes;
        _rateStart = null;
        _lastReport = null;
        Publish(new("write", $"{label}…", _completed, _totalBytes, SectorBytes: _sectorBytes));
    }

    internal void ReportAccepted(long bytesInPhase)
    {
        _completed = _phaseBase + bytesInPhase;
        TimeSpan now = _elapsed();
        _rateStart ??= (now, _completed);
        if (_lastReport is { } last && now - last < TimeSpan.FromMilliseconds(250) && _completed < _totalBytes) return;
        _lastReport = now;
        double seconds = (now - _rateStart.Value.Time).TotalSeconds;
        double? rate = seconds >= 1 ? (_completed - _rateStart.Value.Bytes) / seconds : null;
        Publish(new("write", string.Create(CultureInfo.InvariantCulture,
            $"{_label}：刻录机已接收 {_completed / 1048576.0:F1} / {_totalBytes / 1048576.0:F1} MiB…"),
            _completed, _totalBytes, rate, _sectorBytes));
    }

    internal void Verify(long sectors, long total)
        => Publish(new("verify", $"正在读回校验图案：{sectors:N0} / {total:N0} 扇区",
            sectors * CdExtraDataContent.SectorBytes, total * CdExtraDataContent.SectorBytes,
            SectorBytes: CdExtraDataContent.SectorBytes));

    internal void Stage(string stage, string message) => Publish(new(stage, message, _completed, _totalBytes));

    private void Publish(OpticalBurnProgress progress)
    {
        try { _report?.Invoke(progress); }
        catch { /* Progress observers must not interrupt a physical write. */ }
    }
}

public sealed partial class WindowsImapiBurner : ICdExtraBurner
{
    public Task<CdExtraBurnResult> BurnCdExtraAsync(
        CdExtraBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return _operationGate.RunAsync(() => ImapiCom.RunOnStaThread(() =>
        {
            var tracker = new CdExtraProgressTracker(value => progress?.Report(value), request.AudioBytes,
                request.PredictedDataSectors * CdExtraDataContent.SectorBytes);
            return CdExtraReconnect.Burn(request, tracker, cancellationToken,
                () => BurnCdExtraOnDevice(request, tracker, cancellationToken),
                use => OpenOriginalCdRecorder(request.DeviceId, tracker, cancellationToken, use));
        }), cancellationToken);
    }

    private static CdExtraBurnResult BurnCdExtraOnDevice(CdExtraBurnRequest request,
        CdExtraProgressTracker tracker, CancellationToken cancellationToken)
    {
        tracker.Stage("device", "正在检查刻录机和空白 CD…");
        cancellationToken.ThrowIfCancellationRequested();
        List<string> cleanupWarnings = [];
        dynamic recorder = ImapiCom.CreateRecorder(request.DeviceId);
        dynamic? format = null;
        CdExtraBurnResult? result = null;
        Exception? failure = null;
        // The same pre-write device lines as every other burn path, cached for a failure report.
        List<string> deviceContext = [];
        try
        {
            ReportDevice(() => BurnDeviceDiagnostics.DescribeEnvironment(request));
            ReportDevice(() => BurnDeviceDiagnostics.DescribeRecorder((object)recorder));
            format = ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            ReportDevice(() => BurnDeviceDiagnostics.DescribePhysicalMedia((object)format));
            ValidateRequestedMedia(OpticalBurnMediaKind.CdAudio, recorder, format);
            if (!Convert.ToBoolean(format.MediaPhysicallyBlank))
                throw new InvalidOperationException("CD-Extra 需要全新的空白 CD-R 或已擦除的 CD-RW，当前盘片不是空白盘，已取消刻录。");
            if (request.WriteSpeed is null)
            {
                IReadOnlyList<OpticalWriteSpeed> speeds = ReadSupportedWriteSpeeds(format);
                ReportDevice(() => "可选写入速度："
                    + (speeds.Count == 0 ? "none" : string.Join(" | ", speeds.Select(BurnDeviceDiagnostics.DescribeSpeed))) + "。");
                request = request with { WriteSpeed = ResolveWriteSpeed(null, speeds) };
            }
            ReportDevice(() => "请求写入速度：" + BurnDeviceDiagnostics.DescribeSpeed(request.WriteSpeed) + "。");

            using DynamicCdExtraSession session = new((object)recorder, (object)format, tracker.ReportAccepted,
                cleanupWarnings.Add, message => tracker.Stage("diagnostic", message));
            result = BurnCdExtra(session, request, tracker, cancellationToken);
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
            if (failure is not null)
            {
                try
                {
                    if (!failure.Data.Contains("Burn.DeviceContext"))
                        failure.Data["Burn.DeviceContext"] = string.Join("\n", deviceContext);
                }
                catch { /* Optional diagnostics must preserve the original failure. */ }
            }
            CompleteBurnCleanup(failure, cleanupWarnings,
                () => { if (format is not null) ImapiCom.ReleaseComObject((object)format); },
                () => ImapiCom.ReleaseComObject((object)recorder));
        }

        void ReportDevice(Func<string?> describe)
            => BurnDeviceDiagnostics.Report(message =>
            {
                deviceContext.Add(message);
                tracker.Stage("diagnostic", message);
            }, describe);

        tracker.Stage("complete", request.TestWrite
            ? "模拟写入完成：光驱接受了全部写入命令，盘片没有被改变。"
            : $"盘片已封盘，图案轨全部 {result!.VerifiedPictureSectors:N0} 个扇区读回校验通过。");
        return result!;
    }

    /// <summary>
    /// Records both sessions and verifies the finalized picture against its saved image.
    /// A failed picture write leaves the track open for recovery. Earlier failures drain accepted audio.
    /// </summary>
    internal static CdExtraBurnResult BurnCdExtra(ICdExtraSession session, CdExtraBurnRequest request,
        CdExtraProgressTracker tracker, CancellationToken cancellationToken, Action<string>? reportDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tracker);
        request.Validate();
        Stopwatch timer = Stopwatch.StartNew();
        string operation = "准备 CD-Extra 刻录";
        bool acquired = false;
        bool writeStarted = false;
        bool session1Closed = false;
        bool finalized = false;
        string? checkpoint = null;
        CdExtraCheckpoint? picture = null;
        OpticalWriteSpeed? speed = null;
        FileStream? reference = null;
        List<long> trackStarts = [];
        long dataBytes = 0;
        Exception? failure = null;
        try
        {
            Set("media", "锁定刻录机");
            session.Acquire();
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            Set("media", "检查空白 CD 与 Track-at-Once 写入能力");
            CdExtraMediaInfo media = session.ReadMedia();
            Diagnose($"CD-Extra 介质：Profile=0x{media.Profile:X4}；DiscStatus={media.DiscStatus}；"
                + $"LastSessionState={media.LastSessionState}；AtipLeadoutStart={media.AtipLeadoutStart}；"
                + $"TAO Current={media.TaoFeature.Current}；BUF={media.TaoFeature.BufferUnderrunFree}；"
                + $"TestWrite={media.TaoFeature.TestWrite}；DataTypes=0x{media.TaoFeature.DataTypes:X4}；"
                + $"Page05Length={media.WriteParametersPageLength}。");
            ValidateCdExtraMedia(request, media);
            if (media.TaoFeature.DataTypes == 0)
            {
                Set("media", "TAO 格式位图为空，设置并读回 Mode 1 数据和 CD-DA 音频参数");
                cancellationToken.ThrowIfCancellationRequested();
                session.ConfirmTaoWriteFormats(request.TestWrite);
                Diagnose("光驱已通过 05h 模式页确认 Mode 1 数据和 CD-DA 音频两种写入参数。");
            }

            Set("speed", "设置写入速度");
            speed = session.ConfigureWrite(request.WriteSpeed);
            tracker.Stage("speed", $"设备设置速度={speed.GetMultiplier(OpticalBurnMediaKind.CdAudio):0.#}×（设备返回的设置值）。");

            // CD-TEXT needs the lead-in, which only Session-at-Once lets the host write. Every
            // check happens before the first WRITE, so a refusal leaves a blank disc that the
            // unchanged Track-at-Once sequence below can still record.
            CdSaoLayout? sao = null;
            long leadInStart = 0;
            string? cdTextSkipped = null;
            if (request.CdText is { } cdText)
            {
                (sao, leadInStart, cdTextSkipped) = PrepareSessionAtOnce(session, request, cdText,
                    tracks: request.AudioTracks.Select(track => track.Sectors).ToArray(), finalize: false,
                    Set, Diagnose, cancellationToken);
            }

            long audioBase = 0;
            IDisposable? pinnedAudio = null;
            try
            {
                if (request.PrepareAudio is not null)
                {
                    // Every source is checked (and kept unchanged) before the first WRITE, so a
                    // file edited after planning cannot abort the session half-way.
                    Set("media", "确认音乐文件未被修改");
                    pinnedAudio = request.PrepareAudio();
                }
                if (sao is not null)
                {
                    writeStarted = true;
                    audioBase = WriteSessionAtOnce(session, sao, leadInStart, request.CdText!,
                        request.AudioTracks.Select(track => (track.Sectors, track.ContentLength, track.ProducePcm)).ToArray(),
                        request.EffectiveBufferOptions, tracker, Set, cancellationToken);
                    trackStarts.AddRange(sao.TrackStarts);
                }
                for (int index = 0; sao is null && index < request.AudioTracks.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CdExtraAudioTrack track = request.AudioTracks[index];
                    string label = $"第 1 区段：写入第 {index + 1}/{request.AudioTracks.Count} 首音乐";
                    Set("write", label);
                    tracker.BeginPhase(label, audioBase);
                    using GeneratedContentComStream pcm = new(track.ContentLength, track.ProducePcm, null,
                        cancellationToken, request.EffectiveBufferOptions);
                    pcm.WaitUntilPrebuffered(cancellationToken);
                    writeStarted = true;
                    using (cancellationToken.Register(session.CancelWrite))
                        trackStarts.Add(session.WriteAudioTrack(pcm, track.Sectors, request.TestWrite));
                    audioBase += track.ContentLength;
                }
            }
            finally
            {
                pinnedAudio?.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            CdExtraSessionStart start;
            if (request.TestWrite)
            {
                if (sao is not null) session.Flush();
                Set("flush", "模拟写入：跳过关闭第 1 区段");
                start = new CdExtraSessionStart(request.PredictedSession2StartLba,
                    Math.Max(0, media.AtipLeadoutStart - request.PredictedSession2StartLba - CdExtraBurnRequest.RunOutSectors),
                    media.AtipLeadoutStart, trackStarts.AsReadOnly(), Simulated: true);
            }
            else
            {
                if (sao is null)
                {
                    Set("close-session", "关闭第 1 区段（写入导入区与导出区，请等待完成）");
                    session.CloseSession(finalize: false);
                }
                else
                {
                    Set("close-session", "完成第 1 区段（光驱写入含 CD-TEXT 的导入区与导出区，请等待完成）");
                    session.CompleteSessionAtOnce(finalize: false);
                }
                session1Closed = true;
                Set("media", "读取第 2 区段起始地址");
                (long nwa, long free) = session.ReadNextSessionStart();
                start = new CdExtraSessionStart(nwa, free, media.AtipLeadoutStart, trackStarts.AsReadOnly());
                Diagnose($"CD-Extra 第 2 区段：预测 LBA {request.PredictedSession2StartLba}，光驱报告 LBA {nwa}"
                    + $"（相差 {nwa - request.PredictedSession2StartLba}），可写 {free} 扇区；音轨起点="
                    + string.Join(",", trackStarts) + "。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Set("media", $"按 LBA {start.NextWritableAddress:N0} 生成第 2 区段图片");
            CdExtraDataContent data = request.CreateData(start)
                ?? throw new InvalidOperationException("没有生成第 2 区段的图片数据。");
            ArgumentNullException.ThrowIfNull(data.Produce);
            if (data.Sectors < CdExtraAudioTrack.MinimumSectors)
                throw new InvalidOperationException($"第 2 区段图片数据只有 {data.Sectors:N0} 扇区，至少需要 {CdExtraAudioTrack.MinimumSectors} 扇区。");
            if (!request.TestWrite && data.Sectors > start.FreeBlocks)
                throw new InvalidOperationException($"第 2 区段图片数据需要 {data.Sectors:N0} 扇区，光驱报告只剩 {start.FreeBlocks:N0} 扇区。");
            dataBytes = data.ContentLength;
            tracker.SetDataBytes(dataBytes);

            Action<Stream, CancellationToken> produce = data.Produce;
            if (!request.TestWrite)
            {
                Set("media", "保存图案断点和源镜像");
                var snapshot = CdExtraCheckpoint.Prepare(data, request, media, start,
                    session.ReadPictureIdentity(trackStarts), cancellationToken);
                checkpoint = snapshot.Path;
                picture = snapshot.Job;
                reference = snapshot.Image;
                produce = (output, token) => CdExtraCheckpoint.CopyImage(snapshot.Image, output, token);
                Diagnose($"图案断点：{checkpoint}");
            }

            const string dataLabel = "第 2 区段：写入图片数据轨";
            Set("write", dataLabel);
            tracker.BeginPhase(dataLabel, audioBase, CdExtraDataContent.SectorBytes);
            using (GeneratedContentComStream content = new(data.ContentLength, produce, null,
                cancellationToken, request.EffectiveBufferOptions))
            {
                content.WaitUntilPrebuffered(cancellationToken);
                using (cancellationToken.Register(session.CancelWrite))
                    session.WriteDataTrack(content, data.Sectors, start.NextWritableAddress, request.TestWrite);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (request.TestWrite)
                Set("flush", "模拟写入：跳过封盘");
            else
            {
                Set("finalize", "关闭第 2 区段并封盘（请等待完成）");
                session.CloseSession(finalize: true);
                finalized = true;
                Set("verify", "盘片已封盘，正在读回校验整条图案轨");
                tracker.Verify(0, data.Sectors);
                session.VerifyPicture(reference!, start.NextWritableAddress, data.Sectors,
                    done => tracker.Verify(done, data.Sectors), cancellationToken);
                Diagnose($"图案校验通过：LBA {start.NextWritableAddress} 起的 {data.Sectors} 个扇区与源镜像逐字节一致。");
            }

            CdTextBurnOutcome? cdTextOutcome = request.CdText is null ? null
                : sao is null ? new CdTextBurnOutcome(false, null, null, cdTextSkipped)
                : request.TestWrite ? new CdTextBurnOutcome(false, null, null, "模拟写入不会写入 CD-TEXT。")
                : ReadBackCdText(session, Set, Diagnose);

            Set("release", "释放刻录机");
            return new CdExtraBurnResult(request.DeviceId, trackStarts.AsReadOnly(), start.NextWritableAddress,
                audioBase, dataBytes, timer.Elapsed, speed, request.TestWrite)
            { VerifiedPictureSectors = finalized ? data.Sectors : 0, CdText = cdTextOutcome };
        }
        catch (Exception exception)
        {
            failure = exception;
            string state = finalized ? "盘片已封盘，图案读回校验未通过或未完成。" : session1Closed
                ? "第 1 区段（音乐）已经关闭，音乐会话已写入，图片区段的完成状态未能确认。"
                : writeStarted && !request.TestWrite
                    ? "第 1 区段没有关闭，这张盘可能无法在 CD 机上播放。"
                    : "尚未向盘片写入数据。";
            if (checkpoint is not null) state += $" 图案断点：{checkpoint}";
            try
            {
                exception.Data["Burn.Operation"] = operation;
                exception.Data["Burn.CdExtraContext"] = $"CD-Extra 失败：Operation={operation}；"
                    + $"ElapsedMs={timer.Elapsed.TotalMilliseconds:F0}；WriteStarted={writeStarted}；"
                    + $"Session1Closed={session1Closed}；TrackStarts={string.Join(",", trackStarts)}；"
                    + $"CancellationRequested={cancellationToken.IsCancellationRequested}。";
            }
            catch { }
            var uncertain = picture is null ? null : CdExtraWriteWindow.FromCommand(exception, picture);
            if (uncertain is not null) picture = picture!.WithUncertainCommand(checkpoint!, uncertain);
            if (exception is OperationCanceledException || cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException($"CD-Extra 刻录已取消（{operation}）。{state}", exception,
                    cancellationToken);
            string message = $"CD-Extra 刻录在“{operation}”时失败：{exception.Message}{state}";
            if (picture is not null && (uncertain is not null || CdExtraReconnect.IsTransportFailure(exception)))
                throw new CdExtraPictureInterrupted(message, exception, picture, checkpoint!, speed!);
            throw new InvalidOperationException(message, exception);
        }
        finally
        {
            reference?.Dispose();
            if (acquired)
            {
                // A TAO SYNCHRONIZE CACHE can finish a partial track and destroy its writable address.
                if (failure is not null && writeStarted && checkpoint is null)
                {
                    // Drain accepted blocks, but never close a failed or cancelled session.
                    try { session.Flush(); }
                    catch (Exception flushFailure)
                    {
                        Diagnose($"CD-Extra 失败后清理：Flush failed；HRESULT=0x{flushFailure.HResult:X8}；{flushFailure.Message}");
                    }
                }
                try { session.Release(); }
                catch (Exception releaseFailure) when (failure is not null)
                {
                    Diagnose($"CD-Extra 失败后清理：Release failed；HRESULT=0x{releaseFailure.HResult:X8}；{releaseFailure.Message}");
                }
            }
            if (failure is null && checkpoint is not null) CdExtraCheckpoint.RemoveScratch(checkpoint);
        }

        void Set(string stage, string value)
        {
            operation = value;
            tracker.Stage(stage, value + "…");
        }

        void Diagnose(string message)
        {
            try { reportDiagnostic?.Invoke(message); tracker.Stage("diagnostic", message); }
            catch { }
        }
    }

    /// <summary>Decides whether the audio session is written Session-at-Once with CD-TEXT. Only
    /// device queries and a cue sheet are sent; when the recorder cannot (no current CD
    /// Mastering feature, no SAO, no test write in SAO, or it refuses the cue sheet), the user
    /// decides whether to burn without CD-TEXT, and the Track-at-Once path runs unchanged.</summary>
    internal static (CdSaoLayout? Layout, long LeadInStart, string? SkippedReason) PrepareSessionAtOnce(
        ICdExtraSession session, CdTextLeadIn cdText, IReadOnlyList<long> tracks, bool finalize, bool testWrite,
        Func<string, bool>? confirmWithoutCdText, Action<string, string> set, Action<string> diagnose,
        CancellationToken cancellationToken, long? leadoutLimit = null)
    {
        set("media", "检查光驱能否写入 CD-TEXT（Session-at-Once）");
        string? refusal = null;
        // A finalized single-session disc needs no TAO-equal lead-out position.
        CdSaoLayout layout = new(tracks, HasCdText: true, finalize ? 0 : CdSaoLayout.TrailingSilenceSectors);
        long leadIn = 0;
        try
        {
            CdSaoCapability capability = session.ReadSaoCapability();
            diagnose($"CD-TEXT 写入能力：{capability.Describe()}。");
            if (!capability.CanWriteCdText)
                refusal = "所选光驱没有报告 Session-at-Once（SAO）写入，无法写入 CD-TEXT。";
            else if (testWrite && !capability.TestWrite)
                refusal = "所选光驱不支持 SAO 模式的模拟写入，无法在模拟写入中包含 CD-TEXT。";
            else
            {
                leadIn = session.ReadLeadInStart();
                byte[] cue = layout.BuildCueSheet();
                if (leadoutLimit is { } limit && layout.LeadoutLba > limit)
                    refusal = $"SAO 布局的导出区 LBA {layout.LeadoutLba:N0} 超出盘片可写范围（LBA {limit:N0}）。";
                else if (capability.MaximumCueSheetLength > 0 && cue.Length > capability.MaximumCueSheetLength)
                    refusal = $"光驱的 cue sheet 上限为 {capability.MaximumCueSheetLength} 字节，本盘需要 {cue.Length} 字节。";
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    session.BeginSessionAtOnce(cue, finalize, testWrite);
                    diagnose($"已发送 SAO cue sheet：{cue.Length / 8} 项；导入区起点 LBA {leadIn}；CD-TEXT {cdText.PackCount} 个数据包；"
                        + $"音轨起点 {string.Join(",", layout.TrackStarts)}；导出区 LBA {layout.LeadoutLba}。");
                    return (layout, leadIn, null);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or COMException or TimeoutException or NotSupportedException)
        {
            refusal = $"光驱拒绝了带 CD-TEXT 的 Session-at-Once 写入：{exception.Message}";
        }

        diagnose(refusal + " 尚未向盘片写入任何数据。");
        if (confirmWithoutCdText?.Invoke(refusal) != true)
            throw new OperationCanceledException($"{refusal} 已按你的选择取消刻录，盘片未被写入。");
        diagnose("已确认不带 CD-TEXT 继续，改用原有写入方式。");
        return (null, 0, refusal);
    }

    private static (CdSaoLayout? Layout, long LeadInStart, string? SkippedReason) PrepareSessionAtOnce(
        ICdExtraSession session, CdExtraBurnRequest request, CdTextLeadIn cdText, IReadOnlyList<long> tracks,
        bool finalize, Action<string, string> set, Action<string> diagnose, CancellationToken cancellationToken)
        => PrepareSessionAtOnce(session, cdText, tracks, finalize, request.TestWrite, request.ConfirmWithoutCdText,
            set, diagnose, cancellationToken);

    /// <summary>Writes the whole SAO audio session: lead-in with CD-TEXT, the 150-block pre-gap,
    /// each track (later tracks behind a 152-block silent pre-gap) and two closing blocks of
    /// silence. Returns the music bytes written.</summary>
    internal static long WriteSessionAtOnce(ICdExtraSession session, CdSaoLayout layout, long leadInStart,
        CdTextLeadIn cdText, IReadOnlyList<(long Sectors, long Bytes, Action<Stream, CancellationToken> Produce)> tracks,
        OpticalBurnBufferOptions buffer, CdExtraProgressTracker tracker, Action<string, string> set,
        CancellationToken cancellationToken)
    {
        set("write", "写入导入区 CD-TEXT");
        using (cancellationToken.Register(session.CancelWrite))
            session.WriteSaoLeadIn(leadInStart, cdText);
        using (cancellationToken.Register(session.CancelWrite))
            session.WriteSaoSilence(-CdSaoLayout.FirstPregapSectors, CdSaoLayout.FirstPregapSectors);
        IReadOnlyList<long> starts = layout.TrackStarts;
        long audioBase = 0;
        for (int index = 0; index < tracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
            {
                using (cancellationToken.Register(session.CancelWrite))
                    session.WriteSaoSilence(starts[index] - CdSaoLayout.LaterPregapSectors, CdSaoLayout.LaterPregapSectors);
            }
            string label = $"第 1 区段：写入第 {index + 1}/{tracks.Count} 首音乐（SAO，含 CD-TEXT）";
            set("write", label);
            tracker.BeginPhase(label, audioBase);
            using GeneratedContentComStream pcm = new(tracks[index].Bytes, tracks[index].Produce, null,
                cancellationToken, buffer);
            pcm.WaitUntilPrebuffered(cancellationToken);
            using (cancellationToken.Register(session.CancelWrite))
                session.WriteSaoAudio(pcm, starts[index], tracks[index].Sectors);
            audioBase += tracks[index].Bytes;
        }
        if (layout.TrailingSilence > 0)
        {
            using (cancellationToken.Register(session.CancelWrite))
                session.WriteSaoSilence(starts[^1] + tracks[^1].Sectors, layout.TrailingSilence);
        }
        return audioBase;
    }

    /// <summary>Reads the CD-TEXT back from the finished disc. A failure is reported, never
    /// thrown: the music and picture are already written and verified.</summary>
    internal static CdTextBurnOutcome ReadBackCdText(ICdExtraSession session, Action<string, string> set, Action<string> diagnose)
    {
        set("cdtext-verify", "从盘片读回 CD-TEXT");
        try
        {
            byte[] packs = session.ReadCdTextPacks();
            diagnose($"CD-TEXT 读回：{packs.Length / CdTextLeadIn.PackBytes} 个数据包。");
            return new CdTextBurnOutcome(true, packs, packs.Length == 0 ? "光驱没有从盘片读出 CD-TEXT。" : null);
        }
        catch (Exception exception) when (exception is IOException or COMException or TimeoutException)
        {
            diagnose($"CD-TEXT 读回失败：{exception.Message}");
            return new CdTextBurnOutcome(true, null, exception.Message);
        }
    }

    internal static void ValidateCdExtraMedia(CdExtraBurnRequest request, CdExtraMediaInfo media)
    {
        if (!DynamicCdExtraSession.IsRecordableCdProfile(media.Profile))
            throw new InvalidOperationException($"CD-Extra 需要 CD-R 或 CD-RW 盘片（当前 Profile 0x{media.Profile:X4}）。");
        if (media.DiscStatus != CdDiscInformation.Empty)
            throw new InvalidOperationException("CD-Extra 需要全新的空白 CD-R 或已擦除的 CD-RW，当前盘片已有数据，已取消刻录。");
        CdTaoFeature tao = media.TaoFeature;
        if (!tao.Current || tao.DataTypes != 0 && (!tao.SupportsDataBlockType(CdWriteParameters.RawAudioBlock)
            || !tao.SupportsDataBlockType(CdWriteParameters.Mode1Block)))
            throw new InvalidOperationException("所选光驱没有报告 CD 轨道一次写入（Track-at-Once）所需的音频与 Mode 1 数据块格式。");
        if (request.TestWrite && !tao.TestWrite)
            throw new InvalidOperationException("所选光驱不支持模拟写入（Test Write）。");
        // The data track ends two run-out blocks before the ATIP lead-out start.
        long available = media.AtipLeadoutStart - CdExtraBurnRequest.RunOutSectors - request.PredictedSession2StartLba;
        if (available < CdExtraAudioTrack.MinimumSectors + CdExtraBurnRequest.DataPostgapSectors)
            throw new InvalidOperationException(
                $"盘片容量不足：音乐结束后第 2 区段预计从 LBA {request.PredictedSession2StartLba:N0} 开始，"
                + $"到预刻槽引出区 LBA {media.AtipLeadoutStart:N0} 只剩 {Math.Max(0, available):N0} 扇区。");
    }
}
