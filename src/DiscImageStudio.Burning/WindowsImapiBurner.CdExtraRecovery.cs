namespace DiscImageStudio.Burning;

public sealed partial class WindowsImapiBurner
{
    public Task<long> ResumeCdExtraPictureAsync(string deviceId, string checkpointPath,
        IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
        return _operationGate.RunAsync(() => ImapiCom.RunOnStaThread(() =>
        {
            var tracker = new CdExtraProgressTracker(value => progress?.Report(value), 0, 0);
            var job = CdExtraCheckpoint.Load(checkpointPath);
            return CdExtraReconnect.Resume(job, checkpointPath, deviceId, tracker, cancellationToken,
                use => OpenOriginalCdRecorder(deviceId, tracker, cancellationToken, use)).Sectors;
        }), cancellationToken);
    }

    private static void OpenOriginalCdRecorder(string deviceId, CdExtraProgressTracker tracker, CancellationToken token,
        Action<DynamicCdExtraSession> use)
    {
        token.ThrowIfCancellationRequested();
        // Fresh enumeration also distinguishes a temporarily absent device from a bad COM parameter.
        dynamic master = ImapiCom.CreateComObject("IMAPI2.MsftDiscMaster2");
        try
        {
            int count = (int)master.Count;
            bool found = false;
            for (int i = 0; i < count && !found; i++)
                found = string.Equals((string)master.Item(i), deviceId, StringComparison.OrdinalIgnoreCase);
            if (!found) throw new IOException("Windows 尚未重新检测到原光驱。", unchecked((int)0x8007048f));
        }
        finally { ImapiCom.ReleaseComObject((object)master); }

        dynamic recorder;
        try { recorder = ImapiCom.CreateRecorder(deviceId); }
        catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x80070003 or 0x80070057)
        {
            // This ID was enumerated just above. It can disappear between enumeration and InitializeDiscRecorder.
            throw new IOException($"原光驱尚未能重新打开（0x{e.HResult:X8}）：{e.Message}", unchecked((int)0x8007048f));
        }
        dynamic? format = null;
        Exception? failure = null;
        List<string> warnings = [];
        try
        {
            format = ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            using var session = new DynamicCdExtraSession((object)recorder, (object)format,
                tracker.ReportAccepted, warnings.Add, message => tracker.Stage("diagnostic", message));
            use(session);
        }
        catch (Exception e) { failure = e; }
        finally
        {
            CompleteBurnCleanup(failure, warnings,
                () => { if (format is not null) ImapiCom.ReleaseComObject((object)format); },
                () => ImapiCom.ReleaseComObject((object)recorder));
        }
    }
}

internal sealed partial class DynamicCdExtraSession
{
    internal OpticalWriteSpeed? ResumePicture(CdExtraCheckpoint job, Stream image, CdExtraProgressTracker tracker,
        CancellationToken token, OpticalWriteSpeed? speed, Action<long, bool> confirmPosition)
    {
        Acquire();
        bool verified = false;
        try
        {
            token.ThrowIfCancellationRequested();
            CdDiscInformation disc = _commands.ReadDiscInformation();
            if (disc.DiscId is { } current && job.DiscId is { } expected && current != expected)
                throw new IOException("盘片标识与图案断点不符。");
            CdTrackInformation track = MatchTracks(job, disc);
            if (disc.DiscStatus != CdDiscInformation.Complete)
            {
                MatchWritableDisc(job, disc);
                // After a device reset some drives only report a usable NWA in the original TAO mode.
                var parameters = CdWriteParameters.FinalDataSession(_media!.TaoFeature.BufferUnderrunFree, false);
                if (!parameters.Matches(_writeParametersPage))
                {
                    SelectWriteParameters(parameters);
                    track = ReadCurrentPictureTrack();
                }
                long prefix = RecordedPrefix(job, track);
                job.UncertainCommand?.CheckPosition(job.StartLba + prefix);
                tracker.Stage("verify", $"正在核对断点前已写入的 {prefix:N0} 个扇区…");
                tracker.Verify(0, prefix);
                VerifyPicture(image, job.StartLba, prefix, done => tracker.Verify(done, prefix), token);
                token.ThrowIfCancellationRequested();

                long remaining = job.Sectors - prefix;
                if (remaining > 0 && speed is not null)
                {
                    try { speed = ConfigureWrite(speed); }
                    catch (Exception e) when (e is ArgumentOutOfRangeException || (uint)e.HResult is 0xc0aa0208 or 0xc0aa020f or 0x80070057)
                    {
                        // Some recorders cannot change or report speed in the middle of a TAO track.
                        tracker.Stage("diagnostic", $"光驱未接受或未报告重连后的设置速度，将使用设备当前速度：{e.Message}");
                        speed = null;
                    }
                }

                track = ReadCurrentPictureTrack();
                if (RecordedPrefix(job, track) != prefix)
                    throw new IOException("核对期间可写地址发生变化，已停止续写。");
                token.ThrowIfCancellationRequested();
                confirmPosition(prefix, false);
                if (remaining > 0)
                {
                    long lba = job.StartLba + prefix;
                    tracker.Stage("diagnostic", $"已逐字节核对 {prefix:N0} 个扇区，从 LBA {lba} 续写图案。");
                    tracker.SetDataBytes(job.Sectors * CdTaoWriter.DataBlockBytes);
                    tracker.BeginPhase("续写图案", tracker.AudioBytes + prefix * CdTaoWriter.DataBlockBytes, CdTaoWriter.DataBlockBytes);
                    image.Position = prefix * CdTaoWriter.DataBlockBytes;
                    using var content = new GeneratedContentComStream(remaining * CdTaoWriter.DataBlockBytes,
                        (output, cancel) => CdExtraCheckpoint.CopyImage(image, output, cancel), null, token,
                        OpticalBurnBufferOptions.Default);
                    content.WaitUntilPrebuffered(token);
                    CdTaoWriter.Write(_transport, content, lba, remaining, CdTaoWriter.DataBlockBytes,
                        tracker.ReportAccepted, () => token.IsCancellationRequested, _sleep,
                        (address, bytes) => _commands.CheckPictureWrite(track, address, bytes), _reportDiagnostic);
                }
                token.ThrowIfCancellationRequested();
                if (track.NextWritableAddressValid) _commands.SynchronizeCache(picture: true);
                token.ThrowIfCancellationRequested();
                tracker.Stage("finalize", "图案轨完整，正在关闭第二会话并封盘…");
                CloseSession(finalize: true);

                CdTrackInformation ReadCurrentPictureTrack()
                {
                    CdDiscInformation live = _commands.ReadDiscInformation();
                    if (live.DiscStatus != CdDiscInformation.Appendable || live.DiscId != disc.DiscId)
                        throw new IOException("核对期间盘片状态发生变化，已停止续写。");
                    return MatchTracks(job, live);
                }
            }
            else confirmPosition(job.Sectors, true);
            tracker.Stage("verify", "盘片已封盘，正在读回校验整条图案轨…");
            tracker.Verify(0, job.Sectors);
            try { VerifyPicture(image, job.StartLba, job.Sectors, done => tracker.Verify(done, job.Sectors), token); }
            catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or System.Runtime.InteropServices.COMException)
            { throw new IOException($"盘片已封盘，图案校验未通过：{e.Message}", e); }
            verified = true;
            return speed;
        }
        finally
        {
            try { Release(); } catch (Exception) when (!verified) { }
        }
    }

    private CdTrackInformation MatchTracks(CdExtraCheckpoint job, CdDiscInformation disc)
    {
        int number = job.TrackNumber;
        if (disc.DiscStatus is not (CdDiscInformation.Appendable or CdDiscInformation.Complete))
            throw new IOException("盘片状态与已写完音乐的断点不符。");
        bool awaitingSecond = disc.Sessions == 1 && disc.IsAppendableAfterClose && disc.LastTrackInLastSession == number - 1;
        if (!awaitingSecond && (disc.Sessions != 2 || disc.FirstTrackInLastSession != number ||
            (disc.LastTrackInLastSession != number && !(disc.DiscStatus == CdDiscInformation.Appendable
                && disc.LastTrackInLastSession == number + 1 && _commands.ReadTrackInformation(number + 1).Blank))))
            throw new IOException("盘片会话数或图案轨编号与断点不符。");
        for (int index = 0; index < job.AudioStarts.Length; index++)
        {
            CdTrackInformation audio = _commands.ReadTrackInformation(index + 1);
            if (audio.SessionNumber != 1 || audio.Blank || (audio.TrackMode & 4) != 0
                || audio.TrackStartAddress != job.AudioStarts[index])
                throw new IOException($"第 {index + 1} 条音乐轨与断点不符。");
        }
        CdTrackInformation track = awaitingSecond ? _commands.ReadNextWritableTrack() : _commands.ReadTrackInformation(number);
        if (track.TrackNumber != number || track.SessionNumber != 2 || track.TrackStartAddress != job.StartLba
            || !track.Blank && ((track.TrackMode & 4) == 0 || track.DataMode != 1)
            || awaitingSecond && !track.Blank)
            throw new IOException("图案轨的会话、格式或起始地址与断点不符。");
        if (disc.DiscStatus == CdDiscInformation.Complete && (track.Blank || track.TrackSize < job.Sectors))
            throw new IOException("盘片已封盘，图案轨的长度不足，无法续写。");
        return track;
    }

    private void MatchWritableDisc(CdExtraCheckpoint job, CdDiscInformation disc)
    {
        CdExtraMediaInfo media = ReadMedia();
        if (media.DiscStatus != CdDiscInformation.Appendable || media.Profile != job.Profile
            || media.AtipLeadoutStart != job.AtipLeadout || !media.TaoFeature.Current
            || media.TaoFeature.DataTypes != 0 && !media.TaoFeature.SupportsDataBlockType(CdWriteParameters.Mode1Block))
            throw new IOException("当前介质或写入能力与断点不符。");
        if (job.DiscId is { } id)
        {
            if (disc.DiscId != id) throw new IOException("盘片标识与断点不符。");
        }
        else if (job.AudioFingerprint is null || !string.Equals(_commands.AudioFingerprint(job.AudioStarts),
            job.AudioFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new IOException("无法通过已录音轨的指纹确认原盘，已停止续写。");
    }

    private static long RecordedPrefix(CdExtraCheckpoint job, CdTrackInformation track)
    {
        if (!track.NextWritableAddressValid)
        {
            if (!track.Blank && track.TrackSize >= job.Sectors && track.TrackSize <= job.Sectors + CdExtraBurnRequest.RunOutSectors)
                return job.Sectors;
            throw new IOException("光驱未提供可续写地址，或已关闭的图案轨长度不足。");
        }
        long prefix = track.NextWritableAddress - job.StartLba;
        if (prefix < 0 || prefix > job.Sectors || track.Blank && prefix != 0 || track.FreeBlocks < job.Sectors - prefix)
            throw new IOException($"断点地址或剩余容量不符（光驱报告 LBA {track.NextWritableAddress}）。");
        return prefix;
    }
}
