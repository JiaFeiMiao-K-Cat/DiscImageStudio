using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

public sealed class WindowsImapiBurner : IOpticalDiscBurner
{
    private readonly OpticalBurnOperationGate _operationGate = new();
    private const int CdRawSubcodeIsCooked = 2;
    private const int CdDaoFallbackSpeedSectorsPerSecond = 1200;
    // DAO avoids the two-sector TAO track overhead observed on the tested drive.
    // Keep only a very small physical overburn allowance for nearby measured presets.
    private const long CdDaoOverburnAllowanceSectors = 3;

    private const int BlankMediaState = 0x2;
    // IMAPI_MEDIA_PHYSICAL_TYPE and IMAPI_PROFILE_TYPE (imapi2.h).
    // https://learn.microsoft.com/windows/win32/api/imapi2/ne-imapi2-imapi_media_physical_type
    // https://learn.microsoft.com/windows/win32/api/imapi2/ne-imapi2-imapi_profile_type
    private const int BluRayRecordableMedia = 0x12;
    private const int BluRaySequentialProfile = 0x41;
    private const uint BluRaySingleLayerSectors = 12_219_392;

    public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
        => _operationGate.RunAsync(() => RunOnStaThread(() => EnumerateDevices(cancellationToken)),
            cancellationToken);

    public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _ = OpticalBurnMedia.GetSectorBytes(mediaKind);
        return _operationGate.RunAsync(() => RunOnStaThread(() => EnumerateWriteSpeeds(
            deviceId,
            mediaKind,
            cancellationToken)), cancellationToken);
    }

    public Task<OpticalBurnResult> BurnAsync(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return _operationGate.RunAsync(() => RunOnStaThread(() => Burn(request, progress, cancellationToken)),
            cancellationToken);
    }

    private static IReadOnlyList<OpticalBurnDevice> EnumerateDevices(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic master = CreateComObject("IMAPI2.MsftDiscMaster2");
        try
        {
            if (!(bool)master.IsSupportedEnvironment)
            {
                return [];
            }

            int count = (int)master.Count;
            List<OpticalBurnDevice> devices = new(count);
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = (string)master.Item(index);
                dynamic recorder = CreateRecorder(id);
                try
                {
                    string vendor = Convert.ToString(recorder.VendorId)?.Trim() ?? string.Empty;
                    string product = Convert.ToString(recorder.ProductId)?.Trim() ?? string.Empty;
                    IReadOnlyList<string> volumes = ReadStrings(recorder.VolumePathNames);
                    string drive = volumes.Count == 0 ? string.Empty : $" ({string.Join(", ", volumes)})";
                    string model = string.Join(" ", new[] { vendor, product }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                    devices.Add(new OpticalBurnDevice(
                        id,
                        (model.Length == 0 ? $"光驱 {index + 1}" : model) + drive,
                        volumes));
                }
                finally
                {
                    ReleaseComObject(recorder);
                }
            }

            return devices;
        }
        finally
        {
            ReleaseComObject(master);
        }
    }

    private static IReadOnlyList<OpticalWriteSpeed> EnumerateWriteSpeeds(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic recorder = CreateRecorder(deviceId);
        dynamic? format = null;
        try
        {
            format = OpticalBurnMedia.UsesCdDao(mediaKind)
                ? CreateComObject("IMAPI2.MsftDiscFormat2RawCD")
                : CreateComObject("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            if (!(bool)format.IsRecorderSupported(recorder))
            {
                throw new InvalidOperationException("所选光驱不支持这种刻录方式。");
            }

            if (!(bool)format.IsCurrentMediaSupported(recorder))
            {
                throw new InvalidOperationException("请放入与所选 CD/DVD/蓝光模式兼容的可写盘片。");
            }

            ValidateRequestedMedia(mediaKind, recorder, format);
            cancellationToken.ThrowIfCancellationRequested();
            return ReadSupportedWriteSpeeds(format);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"读取当前盘片的刻录速度失败（0x{exception.HResult:X8}）：{exception.Message}",
                exception);
        }
        finally
        {
            if (format is not null)
            {
                ReleaseComObject(format);
            }

            ReleaseComObject(recorder);
        }
    }

    private static OpticalBurnResult Burn(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ImapiWriteProgressReporter writeProgress = new(request.MediaKind, request.ContentLength,
            value => progress?.Report(value));
        string currentOperation = "初始化 Windows 刻录接口";
        List<string> cleanupWarnings = [];
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new OpticalBurnProgress(
            "device",
            "正在检查刻录机和空白介质…",
            0,
            request.ContentLength));
        dynamic recorder = CreateRecorder(request.DeviceId);
        dynamic? format = null;
        OpticalBurnResult? result = null;
        Exception? burnFailure = null;
        try
        {
            currentOperation = "创建刻录格式";
            format = OpticalBurnMedia.UsesCdDao(request.MediaKind)
                ? CreateComObject("IMAPI2.MsftDiscFormat2RawCD")
                : CreateComObject("IMAPI2.MsftDiscFormat2Data");
            currentOperation = "连接刻录机";
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            currentOperation = "检查刻录机支持";
            if (!(bool)format.IsRecorderSupported(recorder))
            {
                throw new InvalidOperationException("所选光驱不支持这种刻录方式。");
            }

            currentOperation = "检查当前盘片兼容性";
            if (!(bool)format.IsCurrentMediaSupported(recorder))
            {
                throw new InvalidOperationException("当前盘片与所选 CD/DVD/蓝光模式不兼容。");
            }

            ValidateRequestedMedia(request.MediaKind, recorder, format);
            if (request.WriteSpeed is null)
            {
                currentOperation = "选择最低刻录速度";
                request = request with
                {
                    WriteSpeed = ResolveWriteSpeed(
                        null,
                        ReadSupportedWriteSpeeds(format)),
                };
                progress?.Report(new OpticalBurnProgress(
                    "speed",
                    request.WriteSpeed is null
                        ? "当前盘片未报告可选速度，将使用刻录接口的保守回退设置…"
                        : $"自动选择最低写入速度："
                            + $"{request.WriteSpeed.GetMultiplier(request.MediaKind):0.#}×"
                            + $"（{request.WriteSpeed.GetMegabytesPerSecond(request.MediaKind):0.0} MB/s）…",
                    0,
                    request.ContentLength));
            }

            using GeneratedContentComStream content = new(
                request.ContentLength,
                request.ProduceContent,
                (completed, total) => progress?.Report(new OpticalBurnProgress(
                    "transfer",
                    $"正在向刻录接口传输 {completed / (1024.0 * 1024.0):F1} / {total / (1024.0 * 1024.0):F1} MiB…",
                    completed,
                    total)),
                cancellationToken,
                request.EffectiveBufferOptions);
            progress?.Report(new OpticalBurnProgress(
                "buffer",
                "正在准备防断流缓冲区…",
                0,
                request.ContentLength));
            content.WaitUntilPrebuffered(cancellationToken);
            // Preparation may take time. Recheck the loaded medium before writing.
            ValidateRequestedMedia(request.MediaKind, recorder, format);

            OpticalWriteSpeed? actualWriteSpeed;
            if (OpticalBurnMedia.UsesCdDao(request.MediaKind))
            {
                using ICdRawImageSession imageSession = request.MediaKind == OpticalBurnMediaKind.CdData
                    ? new CdDataRawImageSession() : new DynamicCdRawImageSession();
                actualWriteSpeed = BurnCdDao(
                    new DynamicCdRawSession((object)format, writeProgress.ReportRawCdUpdate),
                    imageSession,
                    content,
                    request,
                    cancellationToken,
                    operation =>
                    {
                        currentOperation = operation;
                        if (operation == "关闭 CD 刻录会话")
                            writeProgress.ReportStage("finalize", "正在关闭 CD 刻录会话…");
                    },
                    speed => ReportWriteSpeed(progress, speed, request));
            }
            else if (request.MediaKind == OpticalBurnMediaKind.BluRayData)
            {
                // IDiscFormat2Data.Write formats virgin BD-R with spare areas.
                // The lower-level write engine only sends WRITE commands, so
                // the unformatted track keeps the LBA/physical-address mapping.
                using DynamicBluRaySequentialSession session = new((object)recorder, (object)format,
                    writeProgress.ReportAcceptedBytes,
                    reportCleanupWarning: message => cleanupWarnings.Add(message),
                    reportDiagnostics: message => progress?.Report(new OpticalBurnProgress(
                        "diagnostic", message, 0, request.ContentLength)));
                actualWriteSpeed = BurnBluRay(session, content, request,
                    cancellationToken, operation =>
                    {
                        currentOperation = operation;
                        bool finalizing = operation.Contains("封盘", StringComparison.Ordinal);
                        writeProgress.ReportStage(finalizing ? "finalize" : "media", operation);
                    },
                    speed => ReportWriteSpeed(progress, speed, request),
                    reportCleanupWarning: cleanupWarnings.Add);
            }
            else
            {
                currentOperation = "写入 DVD 数据";
                Action<OpticalWriteSpeed> reportDataWriteSpeed =
                    speed => ReportWriteSpeed(progress, speed, request);
                actualWriteSpeed = BurnData(
                    (object)format,
                    content,
                    request,
                    cancellationToken,
                    reportDataWriteSpeed,
                    writeProgress.ReportDataUpdate);
            }

            stopwatch.Stop();
            result = new OpticalBurnResult(
                request.DeviceId,
                request.MediaKind,
                request.ContentLength,
                stopwatch.Elapsed,
                actualWriteSpeed);
        }
        catch (COMException exception)
        {
            burnFailure = new InvalidOperationException(
                $"Windows 刻录接口在“{currentOperation}”时返回错误 "
                + $"0x{exception.HResult:X8}：{exception.Message}",
                exception);
        }
        catch (Exception exception)
        {
            burnFailure = exception;
        }
        finally
        {
            CompleteBurnCleanup(burnFailure, cleanupWarnings,
                () => { if (format is not null) ReleaseComObject((object)format); },
                () => ReleaseComObject((object)recorder));
        }

        progress?.Report(new OpticalBurnProgress(
            "complete",
            "刻录完成，盘片会话已关闭。",
            request.ContentLength,
            request.ContentLength));
        return result!;
    }

    // The burn exception is captured before entering this final cleanup boundary.
    // Every caller-owned COM object gets its release attempt before we propagate
    // that exception or report an otherwise successful burn's cleanup failure.
    internal static void CompleteBurnCleanup(Exception? burnFailure, ICollection<string> cleanupWarnings,
        Action releaseFormat, Action releaseRecorder)
    {
        Exception? firstCleanupFailure = null;
        ReleaseOne(releaseFormat, "释放刻录格式 COM 对象");
        ReleaseOne(releaseRecorder, "释放刻录机 COM 对象");

        string details = cleanupWarnings.Count == 0 ? string.Empty
            : "\n设备释放期间还出现以下问题（已尝试后续清理）：\n"
                + string.Join("\n", cleanupWarnings.Distinct());
        if (burnFailure is not null)
        {
            if (cleanupWarnings.Count > 0)
            {
                if (burnFailure is OperationCanceledException cancellation)
                    throw new OperationCanceledException(burnFailure.Message + details,
                        cancellation, cancellation.CancellationToken);
                throw new InvalidOperationException(burnFailure.Message + details, burnFailure);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(burnFailure).Throw();
        }
        if (cleanupWarnings.Count > 0)
            throw new InvalidOperationException("刻录流程已完成，但设备未能正常释放。" + details, firstCleanupFailure);

        void ReleaseOne(Action release, string operation)
        {
            try { release(); }
            catch (Exception exception)
            {
                firstCleanupFailure ??= exception;
                cleanupWarnings.Add($"{operation}失败（HRESULT 0x{exception.HResult:X8}）：{exception.Message}");
            }
        }
    }

    internal static OpticalWriteSpeed BurnBluRay(
        IBluRaySequentialSession session,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<string>? reportOperation = null,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null,
        Action<string>? reportCleanupWarning = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(content);
        request.Validate();
        if (request.MediaKind != OpticalBurnMediaKind.BluRayData)
            throw new ArgumentException("顺序 BD-R 通道只接受蓝光数据。", nameof(request));

        bool acquired = false;
        bool writeStarted = false;
        bool flushed = false;
        Exception? operationException = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportOperation?.Invoke("锁定 BD-R 盘片");
            session.Acquire();
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            reportOperation?.Invoke("检查未格式化 BD-R 的原始容量");
            BluRaySequentialMediaInfo media = session.ReadMedia();
            ValidateBluRaySequentialMedia(request, media);
            reportOperation?.Invoke("设置 BD-R 写入速度");
            OpticalWriteSpeed speed = session.ConfigureWrite(request.WriteSpeed);
            speed.Validate();
            reportWriteSpeed?.Invoke(speed);
            cancellationToken.ThrowIfCancellationRequested();
            reportOperation?.Invoke($"无备用区顺序写入 BD-R：{request.ContentLength / 2048:N0} 扇区");
            using (cancellationToken.Register(session.CancelWrite))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writeStarted = true;
                session.Write(content, checked((uint)(request.ContentLength / 2048)));
            }
            cancellationToken.ThrowIfCancellationRequested();

            // Once finalization starts, finish flushing/closing before releasing
            // the drive. Cancelling an in-flight CLOSE command is not safe.
            reportOperation?.Invoke("刷新 BD-R 缓存并封盘（请等待完成）");
            session.Flush();
            flushed = true;
            session.CloseTrack(media.TrackNumber);
            session.CloseDisc();
            return speed;
        }
        catch (Exception exception)
        {
            operationException = exception;
            if (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
                throw new OperationCanceledException("蓝光刻录已取消。", exception, cancellationToken);
            throw;
        }
        finally
        {
            if (acquired)
            {
                if (operationException is null)
                {
                    session.Release();
                }
                else
                {
                    // Preserve the original failure and drain any accepted
                    // blocks, but never finalize a failed or cancelled image.
                    if (writeStarted && !flushed)
                    {
                        long flushStarted = Stopwatch.GetTimestamp();
                        try { session.Flush(); }
                        catch (Exception exception)
                        {
                            TimeSpan elapsed = Stopwatch.GetElapsedTime(flushStarted);
                            try
                            {
                                reportCleanupWarning?.Invoke($"蓝光失败后刷新缓存失败（HRESULT 0x{exception.HResult:X8}，"
                                    + $"耗时 {elapsed.TotalSeconds:F3} 秒）：{exception.Message}");
                            }
                            catch { /* Diagnostics must not replace the write failure or prevent release. */ }
                        }
                    }
                    try { session.Release(); }
                    catch { }
                }
            }
        }
    }

    internal static void ValidateBluRaySequentialMedia(
        OpticalBurnRequest request, BluRaySequentialMediaInfo media)
    {
        request.Validate();
        if (request.MediaKind != OpticalBurnMediaKind.BluRayData)
            throw new ArgumentException("顺序 BD-R 通道只接受蓝光数据。", nameof(request));
        if (media.Profile != BluRaySequentialProfile)
            throw new InvalidOperationException("无备用区绘图需要顺序记录 BD-R（SRM）；不支持 BD-RE 或随机记录 BD-R。");
        if (!media.DiscIsBlank || !media.TrackIsBlank || !media.NextWritableAddressValid
            || media.NextWritableAddress != 0 || media.TrackStartAddress != 0 || media.TrackNumber != 1)
            throw new InvalidOperationException("无备用区绘图需要从 LBA 0 开始的全新空白 BD-R，已取消刻录。");
        if (media.FormatStatus != 1)
            throw new InvalidOperationException("BD-R 已格式化或格式状态未知；无法保证无备用区的物理位置，请使用未格式化的新盘。");

        long requiredSectors = request.ContentLength / 2048;
        if (requiredSectors % 32 != 0 || requiredSectors > BluRaySingleLayerSectors)
            throw new InvalidOperationException("蓝光绘图总扇区数必须是 32 的倍数，且不超过 12,219,392。");
        if (media.FreeBlocks < requiredSectors)
            throw new InvalidOperationException(
                $"蓝光盘容量不足：需要 {requiredSectors:N0} 扇区，原始轨道可写 {media.FreeBlocks:N0} 扇区。不会自动缩小绘图容量。");
        if (media.MaximumBlocks != BluRaySingleLayerSectors
            || media.TrackSize != media.MaximumBlocks || media.FreeBlocks != media.TrackSize)
            throw new InvalidOperationException(
                $"盘片原始容量或轨道布局不符合单层无备用区 BD-R：最大 {media.MaximumBlocks:N0}、"
                + $"轨道 {media.TrackSize:N0}、可写 {media.FreeBlocks:N0} 扇区；需要完整的 12,219,392 扇区布局。");
    }

    internal static OpticalWriteSpeed BurnCdAudio(
        ICdRawSession format,
        ICdRawImageSession imageSession,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<string>? reportOperation = null,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null)
        => BurnCdDao(format, imageSession, content, request, cancellationToken, reportOperation, reportWriteSpeed);

    internal static OpticalWriteSpeed BurnCdDao(
        ICdRawSession format,
        ICdRawImageSession imageSession,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<string>? reportOperation = null,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null)
    {
        if (!OpticalBurnMedia.UsesCdDao(request.MediaKind))
            throw new ArgumentException("CD DAO requires an audio or CD data request.", nameof(request));
        string trackDescription = request.MediaKind == OpticalBurnMediaKind.CdData ? "数据轨" : "音轨";
        bool prepared = false;
        Exception? operationException = null;
        try
        {
            reportOperation?.Invoke("准备并锁定 CD 介质");
            format.PrepareMedia();
            prepared = true;
            reportOperation?.Invoke("检查 CD 是否为空白盘");
            if (!format.MediaPhysicallyBlank)
            {
                throw new InvalidOperationException("CD 不是空白盘，已取消刻录。");
            }

            reportOperation?.Invoke("设置 CD DAO 扇区格式");
            if (!format.SupportedSectorTypes.Contains(CdRawSubcodeIsCooked))
            {
                throw new InvalidOperationException(
                    "所选光驱不支持 CD DAO 所需的 Cooked R-W 子通道格式。");
            }

            format.SetRequestedSectorType(CdRawSubcodeIsCooked);
            if (request.WriteSpeed is not null)
            {
                reportOperation?.Invoke("设置 CD 刻录速度");
                format.SetWriteSpeed(
                    request.WriteSpeed.SectorsPerSecond,
                    request.WriteSpeed.RotationTypeIsPureCav);
            }
            else
            {
                reportOperation?.Invoke("使用 CD DAO 保守回退速度");
                format.SetWriteSpeed(CdDaoFallbackSpeedSectorsPerSecond, false);
            }

            OpticalWriteSpeed actualWriteSpeed = new(
                format.CurrentWriteSpeed,
                format.CurrentRotationTypeIsPureCav);
            actualWriteSpeed.Validate();
            reportWriteSpeed?.Invoke(actualWriteSpeed);

            reportOperation?.Invoke($"构造 CD DAO {trackDescription}");
            IStream rawImage = imageSession.CreateImage(content);
            long startOfLeadout = imageSession.StartOfLeadout;
            long lastPossibleStartOfLeadout = format.LastPossibleStartOfLeadout;
            long excessSectors = startOfLeadout - lastPossibleStartOfLeadout;
            if (excessSectors > CdDaoOverburnAllowanceSectors)
            {
                throw new InvalidOperationException(
                    $"CD DAO lead-out 超出盘片边界 {excessSectors} 个扇区；"
                    + $"最多允许 {CdDaoOverburnAllowanceSectors} 个 DAO 扇区的受限 overburn。");
            }

            if (excessSectors > 0)
            {
                reportOperation?.Invoke(
                    $"使用 CD DAO 末端 overburn（超出 {excessSectors} 个 DAO 扇区）");
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                format.CancelWrite);
            reportOperation?.Invoke($"写入 CD DAO {trackDescription}");
            format.WriteMedia(rawImage);
            return actualWriteSpeed;
        }
        catch (Exception exception)
        {
            operationException = exception;
            throw;
        }
        finally
        {
            if (prepared)
            {
                if (operationException is null)
                {
                    reportOperation?.Invoke("关闭 CD 刻录会话");
                    format.ReleaseMedia();
                }
                else
                {
                    TryReleaseCdMedia(format);
                }
            }
        }
    }

    private static void TryReleaseCdMedia(ICdRawSession format)
    {
        try
        {
            format.ReleaseMedia();
        }
        catch
        {
            // Preserve the first failure. Some drives automatically unprepare the
            // media after a write error, making ReleaseMedia fail with 0xC0AA0502.
        }
    }

    private static OpticalWriteSpeed BurnData(
        dynamic format,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null,
        Action<object>? reportWriteProgress = null)
    {
        ValidateDataMedia(
            request,
            (int)format.CurrentMediaStatus,
            (long)format.FreeSectorsOnMedia);

        format.BufferUnderrunFreeDisabled = false;
        format.ForceMediaToBeClosed = true;
        format.ForceOverwrite = false;
        if (request.WriteSpeed is not null)
        {
            format.SetWriteSpeed(
                request.WriteSpeed.SectorsPerSecond,
                request.WriteSpeed.RotationTypeIsPureCav);
        }

        OpticalWriteSpeed actualWriteSpeed = new(
            Convert.ToInt32(format.CurrentWriteSpeed),
            Convert.ToBoolean(format.CurrentRotationTypeIsPureCAV));
        actualWriteSpeed.Validate();
        reportWriteSpeed?.Invoke(actualWriteSpeed);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            TryCancel(format, cdAudio: false));
        // DDiscFormat2DataEvents.Update supplies device-written LBAs, unlike
        // reads from the generated source stream. Preserve the cancellation
        // registration above and keep telemetry limited to the Write lifetime.
        Guid events = new("2735413C-7F64-5B0F-8F00-5D77AFBE261E");
        Action<object, object> update = (_, args) => reportWriteProgress?.Invoke(args);
        bool subscribed = false;
        try
        {
            ComEventsHelper.Combine((object)format, events, 0x200, update);
            subscribed = true;
        }
        catch (COMException) { /* Optional telemetry must not prevent writing. */ }
        try { format.Write(content); }
        finally
        {
            if (subscribed)
            {
                try { ComEventsHelper.Remove((object)format, events, 0x200, update); }
                catch (COMException) { /* Preserve any write error. */ }
            }
        }
        return actualWriteSpeed;
    }

    private static void ValidateRequestedMedia(
        OpticalBurnMediaKind mediaKind,
        dynamic recorder,
        dynamic format)
    {
        object currentProfiles = recorder.CurrentProfiles;
        int[] profiles = currentProfiles is Array values
            ? values.Cast<object>().Select(value => Convert.ToInt32(value)).ToArray()
            : [];
        if (!IsWritableMedia(mediaKind, Convert.ToInt32(format.CurrentPhysicalMediaType), profiles))
        {
            string medium = mediaKind switch
            {
                OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData => "CD-R 或 CD-RW",
                OpticalBurnMediaKind.DvdData => "DVD",
                OpticalBurnMediaKind.BluRayData => "未格式化的单层顺序 BD-R（无备用区）",
                _ => throw new ArgumentOutOfRangeException(nameof(mediaKind)),
            };
            throw new InvalidOperationException($"所选刻录模式需要可写的 {medium} 盘片；当前盘片不兼容。");
        }
    }

    internal static bool IsWritableMedia(OpticalBurnMediaKind mediaKind, int physicalMediaType,
        IReadOnlyCollection<int> currentProfiles)
    {
        ArgumentNullException.ThrowIfNull(currentProfiles);
        // Pair the physical disc type with the loaded medium's current profile.
        // IMAPI's data format also accepts CDs and BDs, so its generic support
        // check alone must not determine whether a DVD drawing can be written.
        return mediaKind switch
        {
            OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData => physicalMediaType switch
            {
                0x2 => currentProfiles.Contains(0x9), // CD-R
                0x3 => currentProfiles.Contains(0xA), // CD-RW
                _ => false,
            },
            OpticalBurnMediaKind.DvdData => physicalMediaType switch
            {
                0x5 => currentProfiles.Contains(0x12), // DVD-RAM
                0x6 => currentProfiles.Contains(0x1B), // DVD+R
                0x7 => currentProfiles.Contains(0x1A), // DVD+RW
                0x8 => currentProfiles.Contains(0x2B), // DVD+R DL
                0x9 => currentProfiles.Contains(0x11), // DVD-R
                0xA => currentProfiles.Contains(0x13) || currentProfiles.Contains(0x14), // DVD-RW
                0xB => currentProfiles.Contains(0x15) || currentProfiles.Contains(0x16), // DVD-R DL
                0xD => currentProfiles.Contains(0x2A), // DVD+RW DL
                _ => false,
            },
            OpticalBurnMediaKind.BluRayData => IsWritableBluRayMedia(physicalMediaType, currentProfiles),
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind)),
        };
    }

    internal static bool IsWritableBluRayMedia(int physicalMediaType, IReadOnlyCollection<int> currentProfiles)
    {
        ArgumentNullException.ThrowIfNull(currentProfiles);
        // Inspect the loaded medium's current profiles, never the drive's supported
        // profiles: a Blu-ray recorder can also contain a CD, DVD or read-only BD.
        return physicalMediaType == BluRayRecordableMedia
            && currentProfiles.Contains(BluRaySequentialProfile);
    }

    internal static void ValidateDataMedia(OpticalBurnRequest request, int mediaState, long freeSectors)
    {
        string mediaName = ValidateWritableDataCapacity(request.MediaKind, mediaState, freeSectors);
        long requiredSectors = request.ContentLength / 2048;
        if (freeSectors < requiredSectors)
        {
            long deficit = requiredSectors - freeSectors;
            throw new InvalidOperationException(
                $"{mediaName} 剩余容量不足：需要 {requiredSectors:N0} 扇区（{requiredSectors / 512.0:F3} MiB），"
                + $"实际可写 {freeSectors:N0} 扇区（{freeSectors / 512.0:F3} MiB），"
                + $"超出 {deficit:N0} 扇区（{deficit / 512.0:F3} MiB）。");
        }
    }

    internal static string ValidateWritableDataCapacity(OpticalBurnMediaKind mediaKind,
        int mediaState, long freeSectors)
    {
        string mediaName = mediaKind switch
        {
            OpticalBurnMediaKind.CdData => "CD",
            OpticalBurnMediaKind.DvdData => "DVD",
            OpticalBurnMediaKind.BluRayData => "蓝光盘",
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind), "Expected CD, DVD or Blu-ray data."),
        };
        if ((mediaState & BlankMediaState) == 0)
        {
            throw new InvalidOperationException($"{mediaName} 不是空白盘，已取消刻录。");
        }

        if (freeSectors <= 0)
        {
            throw new InvalidOperationException($"{mediaName} 未报告可用的数据容量（{freeSectors:N0} 扇区）。");
        }
        return mediaName;
    }

    private static void ReportWriteSpeed(
        IProgress<OpticalBurnProgress>? progress,
        OpticalWriteSpeed speed,
        OpticalBurnRequest request)
    {
        progress?.Report(new OpticalBurnProgress(
            "speed",
            $"设备设置速度={speed.GetMultiplier(request.MediaKind):0.#}×"
                + $" · {speed.GetMegabytesPerSecond(request.MediaKind):0.0} MB/s"
                + (speed.RotationTypeIsPureCav ? " · CAV" : "")
                + "（设备返回的设置值；实时吞吐另行测量）。",
            0,
            request.ContentLength));
    }

    private static IReadOnlyList<OpticalWriteSpeed> ReadSupportedWriteSpeeds(dynamic format)
    {
        List<OpticalWriteSpeed> result = [];
        try
        {
            if (format.SupportedWriteSpeedDescriptors is Array descriptors)
            {
                foreach (object? item in descriptors)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    try
                    {
                        dynamic descriptor = item;
                        int sectorsPerSecond = Convert.ToInt32(descriptor.WriteSpeed);
                        if (sectorsPerSecond > 0)
                        {
                            result.Add(new OpticalWriteSpeed(
                                sectorsPerSecond,
                                Convert.ToBoolean(descriptor.RotationTypeIsPureCAV)));
                        }
                    }
                    finally
                    {
                        ReleaseComObject(item);
                    }
                }
            }
        }
        catch (COMException)
        {
            // Older drives may expose only the simple speed list. Fall back below.
        }

        if (result.Count == 0 && format.SupportedWriteSpeeds is Array speeds)
        {
            foreach (object? item in speeds)
            {
                int sectorsPerSecond = Convert.ToInt32(item);
                if (sectorsPerSecond > 0)
                {
                    result.Add(new OpticalWriteSpeed(sectorsPerSecond, false));
                }
            }
        }

        return result
            .Distinct()
            .OrderBy(speed => speed.SectorsPerSecond)
            .ThenBy(speed => speed.RotationTypeIsPureCav)
            .ToArray();
    }

    internal static OpticalWriteSpeed? ResolveWriteSpeed(
        OpticalWriteSpeed? requested,
        IReadOnlyList<OpticalWriteSpeed> supported)
    {
        ArgumentNullException.ThrowIfNull(supported);
        return requested ?? supported
            .OrderBy(speed => speed.SectorsPerSecond)
            .ThenBy(speed => speed.RotationTypeIsPureCav)
            .FirstOrDefault();
    }

    private static void TryCancel(dynamic format, bool cdAudio)
    {
        try
        {
            if (cdAudio)
            {
                format.CancelAddTrack();
            }
            else
            {
                format.CancelWrite();
            }
        }
        catch
        {
            // The operation may already be finishing; the producer token is also cancelled.
        }
    }

    private static dynamic CreateRecorder(string id)
    {
        dynamic recorder = CreateComObject("IMAPI2.MsftDiscRecorder2");
        recorder.InitializeDiscRecorder(id);
        return recorder;
    }

    private static dynamic CreateComObject(string programmaticId)
    {
        Type type = Type.GetTypeFromProgID(programmaticId, throwOnError: false)
            ?? throw new PlatformNotSupportedException(
                "Windows Image Mastering API 2 (IMAPI2) 不可用。");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"无法创建 Windows 刻录组件 {programmaticId}。");
    }

    private static IReadOnlyList<string> ReadStrings(object? value)
    {
        if (value is not Array array)
        {
            return [];
        }

        List<string> result = [];
        foreach (object? item in array)
        {
            string? text = Convert.ToString(item)?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(text);
            }
        }

        return result;
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private static Task<T> RunOnStaThread<T>(Func<T> action)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "DiscImageStudio.IMAPI2",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
