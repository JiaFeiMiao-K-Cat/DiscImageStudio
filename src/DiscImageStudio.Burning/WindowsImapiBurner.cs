using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

public sealed class WindowsImapiBurner : IOpticalDiscBurner
{
    private readonly OpticalBurnOperationGate _operationGate = new();
    private const int CdDaoFallbackSpeedSectorsPerSecond = 1200;
    private const int RawCdMediaNotBlank = unchecked((int)0xC0AA0606);
    private const int RawCdMediaNotSupported = unchecked((int)0xC0AA0607);
    private const int BlankMediaState = 0x2;
    // IMAPI_FORMAT2_DATA_MEDIA_STATE_OVERWRITE_ONLY / _RANDOMLY_WRITABLE (imapi2.h).
    // A DVD+RW never returns to BLANK after its first format: erasing only replaces
    // the contents, so the disc can only be written through ForceOverwrite.
    private const int OverwriteOnlyMediaState = 0x1;
    private const int EraseRequiredMediaState = 0x800;
    private const int WriteProtectedMediaState = 0x2000;
    private const int UnsupportedMediaStates = 0xFC00;
    private const int CdRewritableMediaType = 0x3;
    private const int DvdPlusRw = 0x7;
    private const int DvdMinusRw = 0xA;
    private const int DvdPlusRwDualLayer = 0xD;
    // IMAPI_MEDIA_PHYSICAL_TYPE and IMAPI_PROFILE_TYPE (imapi2.h).
    // https://learn.microsoft.com/windows/win32/api/imapi2/ne-imapi2-imapi_media_physical_type
    // https://learn.microsoft.com/windows/win32/api/imapi2/ne-imapi2-imapi_profile_type
    private const int BluRayRecordableMedia = 0x12;
    private const int BluRayRewritableMediaType = 0x13;
    private const int BluRaySequentialProfile = 0x41;
    private const int BluRayRewritableProfile = 0x43;
    private const uint BluRaySingleLayerSectors = 12_219_392;

    public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
        => _operationGate.RunAsync(() => ImapiCom.RunOnStaThread(() => EnumerateDevices(cancellationToken)),
            cancellationToken);

    public async Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default)
        => (await GetMediaCapacityAsync(deviceId, mediaKind, cancellationToken)).TotalSectors;

    public Task<OpticalMediaCapacity> GetMediaCapacityAsync(string deviceId, OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _ = OpticalBurnMedia.GetSectorBytes(mediaKind);
        return _operationGate.RunAsync(() => ImapiCom.RunOnStaThread(() =>
            ReadMediaCapacity(deviceId, mediaKind, cancellationToken)), cancellationToken);
    }

    private static OpticalMediaCapacity ReadMediaCapacity(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        dynamic? recorder = null;
        dynamic? format = null;
        try
        {
            recorder = ImapiCom.CreateRecorder(deviceId);
            format = ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            int physicalMediaType = ValidateRequestedMedia(mediaKind, (object)recorder, (object)format);
            // These are media queries. PrepareMedia would enter a recording session.
            OpticalMediaCapacity capacity;
            if (mediaKind == OpticalBurnMediaKind.DvdData)
            {
                int mediaType = Convert.ToInt32(format.CurrentPhysicalMediaType);
                int mediaState = Convert.ToInt32(format.CurrentMediaStatus);
                long totalSectors = Convert.ToInt64(format.TotalSectorsOnMedia);
                long freeSectors = Convert.ToInt64(format.FreeSectorsOnMedia);
                // Keep the geometry extent. IMAPI's Free property already removes
                // recording overhead; never apply the legacy 608-sector estimate again.
                // A used rewritable disc is rewritten from LBA 0 once the user approves,
                // so its occupied tail is not the writable limit.
                capacity = ReadDvdMediaCapacity(mediaType, mediaState, totalSectors, freeSectors)
                    with { RequiresErase = DvdNeedsErase((object)format, mediaType) };
            }
            else
            {
                var transport = new ImapiMmcCommandTransport((object)recorder);
                capacity = OpticalBurnMedia.UsesCdDao(mediaKind)
                    ? ReadCdMediaCapacity(ReadCdAtipSectorCount(transport),
                        Convert.ToBoolean(format.MediaPhysicallyBlank), physicalMediaType)
                    : physicalMediaType == BluRayRewritableMediaType
                        ? BluRayRewritableBurn.ReadCapacity(transport)
                        : ReadBluRayMediaCapacity(transport);
            }
            token.ThrowIfCancellationRequested();
            return capacity;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0xC0AA0202))
        {
            throw new InvalidOperationException("所选光驱中没有盘片。", ex);
        }
        finally
        {
            if (format is not null) ImapiCom.ReleaseComObject(format);
            if (recorder is not null) ImapiCom.ReleaseComObject(recorder);
        }
    }

    internal static long ReadCdAtipSectorCount(IMmcCommandTransport transport)
    {
        // READ TOC/PMA/ATIP format 4. Unlike a session TOC, the pre-groove lead-out
        // describes the recordable disc even if only part of it has been written.
        const byte allocation = 64;
        byte[] cdb = [0x43, 0x02, 0x04, 0, 0, 0, 0, 0, allocation, 0];
        MmcCommandResult response = transport.Execute(cdb, allocation, 30);
        if (response.HResult < 0 || response.HResult == BluRayMmcCommands.CommandHasSenseData)
            throw new MmcCommandException(cdb[0], response);
        byte[] data = response.Data;
        if (data.Length < 15)
            throw new IOException("光驱返回的 CD 预刻槽引出区信息不完整。");
        int declared = BinaryPrimitives.ReadUInt16BigEndian(data) + 2;
        if (declared < 15 || data.Length < Math.Min(allocation, declared))
            throw new IOException("光驱返回的 CD 预刻槽引出区信息不完整。");
        int minutes = data[12], seconds = data[13], frames = data[14];
        // Binary MSF. LBA 0 starts at 00:02:00; the lead-out address is exclusive.
        int count = ((minutes * 60 + seconds) * 75 + frames) - 150;
        if (minutes >= 100 || seconds >= 60 || frames >= 75 || count <= 0)
            throw new IOException("光驱返回的 CD 引出区地址无效。");
        return count;
    }

    internal static long ReadBluRaySectorCount(IMmcCommandTransport transport)
    {
        var (blocks, status, spareClusters) = new BluRayMmcCommands(transport).ReadFormatCapacity();
        return ReadBluRaySectorCount(blocks, status, spareClusters);
    }

    internal static long ReadBluRaySectorCount(uint blocks, int status, int spareClusters)
    {
        if (!TryReadBluRaySectorCount(blocks, status, spareClusters, out long total, out string reason))
            throw new InvalidOperationException(reason);
        return total;
    }

    private static bool TryReadBluRaySectorCount(uint blocks, int status, int spareClusters,
        out long total, out string reason)
    {
        // Type 1 counts data-zone blocks; spareClusters is the possible maximum.
        // Type 2 counts user blocks; spareClusters is actually allocated space.
        // Smaller drawing ranges can be authored, but the recorder requires the
        // complete physical layout of the supported no-spare 25 GB single layer.
        total = 0;
        if (status is not (1 or 2))
        {
            reason = "光驱未报告当前 BD-R 的有效容量。";
            return false;
        }
        if (status == 2 && spareClusters != 0)
        {
            reason = "BD-R 已分配备用区，逻辑容量不能用于无备用区标定。";
            return false;
        }
        if (blocks != BluRaySingleLayerSectors)
        {
            reason = $"BD-R 报告 {blocks:N0} 扇区，当前标定仅支持 25 GB 单层无备用区的 12,219,392 扇区布局。";
            return false;
        }
        total = blocks;
        reason = string.Empty;
        return true;
    }

    internal static OpticalMediaCapacity ReadCdMediaCapacity(long atipSectors, bool physicallyBlank, int physicalMediaType)
    {
        if (atipSectors <= 2)
            throw new IOException("光驱未报告有效的 CD 容量。");
        // A conservative single-track payload limit shared with TAO. The actual
        // recorder remains DAO and independently checks its lead-out before writing.
        bool requiresErase = !physicallyBlank && physicalMediaType == CdRewritableMediaType;
        return new(atipSectors, physicallyBlank || requiresErase ? atipSectors - 2 : 0)
        {
            RequiresErase = requiresErase,
        };
    }

    internal static OpticalMediaCapacity ReadDvdMediaCapacity(int physicalMediaType, int mediaState,
        long totalSectorsOnMedia, long freeSectorsOnMedia)
    {
        long total = ReadDvdSectorCount(physicalMediaType, totalSectorsOnMedia);
        if (freeSectorsOnMedia < 0 || freeSectorsOnMedia > total)
            throw new IOException("光驱报告的 DVD 可写容量无效。");
        // A formatted erasable disc is written again from LBA 0 once ForceOverwrite
        // is set, so the free extent of its old track is not the writable payload.
        return new(total, (mediaState & OverwriteOnlyMediaState) != 0 ? total : freeSectorsOnMedia);
    }

    internal static OpticalMediaCapacity ReadBluRayMediaCapacity(IMmcCommandTransport transport)
    {
        BluRaySequentialMediaInfo media = new BluRayMmcCommands(transport).ReadMedia();
        long total = ReadBluRaySectorCount(media.MaximumBlocks, media.FormatStatus, media.SpareClusters);
        // A recorded no-spare disc can still supply its geometry, but is not an
        // available destination for our blank-disc sequential recording workflow.
        if (media.FormatStatus != 1 || !media.DiscIsBlank)
            return new(total, 0);
        ValidateBluRaySequentialLayout(media);
        return new(total, media.FreeBlocks);
    }

    internal static long ReadDvdSectorCount(int physicalMediaType, long totalSectorsOnMedia)
    {
        if (physicalMediaType is 0x8 or 0xB or 0xD)
            throw new InvalidOperationException("双层 DVD 不适用于单层盘片标定。");
        if (totalSectorsOnMedia <= 0)
            throw new InvalidOperationException("光驱未报告有效的 DVD 总扇区数。");
        return totalSectorsOnMedia;
    }

    public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _ = OpticalBurnMedia.GetSectorBytes(mediaKind);
        return _operationGate.RunAsync(() => ImapiCom.RunOnStaThread(() => EnumerateWriteSpeeds(
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
        return _operationGate.RunAsync(() => ImapiCom.RunOnStaThread(() => Burn(request, progress, cancellationToken)),
            cancellationToken);
    }

    private static IReadOnlyList<OpticalBurnDevice> EnumerateDevices(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic master = ImapiCom.CreateComObject("IMAPI2.MsftDiscMaster2");
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
                dynamic recorder = ImapiCom.CreateRecorder(id);
                try
                {
                    string vendor = Convert.ToString(recorder.VendorId)?.Trim() ?? string.Empty;
                    string product = Convert.ToString(recorder.ProductId)?.Trim() ?? string.Empty;
                    IReadOnlyList<string> volumes = ImapiCom.ReadStrings(recorder.VolumePathNames);
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
                    ImapiCom.ReleaseComObject(recorder);
                }
            }

            return devices;
        }
        finally
        {
            ImapiCom.ReleaseComObject(master);
        }
    }

    private static IReadOnlyList<OpticalWriteSpeed> EnumerateWriteSpeeds(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic recorder = ImapiCom.CreateRecorder(deviceId);
        dynamic? format = null;
        try
        {
            format = OpticalBurnMedia.UsesCdDao(mediaKind)
                ? ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2RawCD")
                : ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            int physicalMediaType = ValidateRequestedMedia(mediaKind, (object)recorder, (object)format);
            return ReadCurrentMediaWriteSpeeds(
                mediaKind, physicalMediaType, (object)recorder, (object)format, cancellationToken);
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
                ImapiCom.ReleaseComObject(format);
            }

            ImapiCom.ReleaseComObject(recorder);
        }
    }

    internal static IReadOnlyList<OpticalWriteSpeed> ReadCurrentMediaWriteSpeeds(
        OpticalBurnMediaKind mediaKind, int physicalMediaType, dynamic recorder, dynamic format,
        CancellationToken cancellationToken)
    {
        // Rewritable media may report speeds while they still require erasing or formatting.
        bool rewritable = mediaKind switch
        {
            OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData => physicalMediaType == CdRewritableMediaType,
            OpticalBurnMediaKind.DvdData => physicalMediaType is DvdPlusRw or DvdMinusRw or DvdPlusRwDualLayer,
            OpticalBurnMediaKind.BluRayData => physicalMediaType == BluRayRewritableMediaType,
            _ => false,
        };
        if (!rewritable)
        {
            if (!(bool)format.IsRecorderSupported(recorder))
                throw new InvalidOperationException("所选光驱不支持这种刻录方式。");
            if (!(bool)format.IsCurrentMediaSupported(recorder))
                throw new InvalidOperationException("请放入与所选 CD/DVD/蓝光模式兼容的可写盘片。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ReadSupportedWriteSpeeds(format);
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
            "正在检查刻录机和当前盘片…",
            0,
            request.ContentLength));
        dynamic recorder = ImapiCom.CreateRecorder(request.DeviceId);
        dynamic? format = null;
        BluRayMmcCommands? removalProtection = null;
        OpticalBurnResult? result = null;
        Exception? burnFailure = null;
        string? environmentDiagnostic = null;
        string? recorderDiagnostic = null;
        string? physicalMediaDiagnostic = null;
        bool bluRayRewritable = false;
        try
        {
            BurnDeviceDiagnostics.Report(ReportDiagnostic, () => environmentDiagnostic = BurnDeviceDiagnostics.DescribeEnvironment(request));
            BurnDeviceDiagnostics.Report(ReportDiagnostic, () => recorderDiagnostic = BurnDeviceDiagnostics.DescribeRecorder((object)recorder));
            currentOperation = "创建刻录格式";
            bool cdDao = OpticalBurnMedia.UsesCdDao(request.MediaKind);
            format = cdDao
                ? ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2RawCD")
                : ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Data");
            currentOperation = "连接刻录机";
            format.Recorder = recorder;
            format.ClientName = "Disc Image Studio";
            BurnDeviceDiagnostics.Report(ReportDiagnostic, () => physicalMediaDiagnostic = BurnDeviceDiagnostics.DescribePhysicalMedia((object)format));
            currentOperation = "检查当前盘片兼容性";
            int physicalMediaType = ValidateRequestedMedia(request.MediaKind, (object)recorder, (object)format);
            bluRayRewritable = request.MediaKind == OpticalBurnMediaKind.BluRayData
                && physicalMediaType == BluRayRewritableMediaType;
            if (bluRayRewritable != request.BluRayRewritablePreparation.HasValue)
                throw new InvalidOperationException("蓝光盘片已更换或尚未确认 BD-RE 格式化／覆写，请重新开始刻录。");

            bool cdRwNeedsErase = !bluRayRewritable
                && ValidateWriterSupportBeforeBurn(request, physicalMediaType, (object)recorder, (object)format);

            using DynamicBluRaySequentialSession? rewritableSession = bluRayRewritable
                ? CreateBluRaySession()
                : null;
            BluRayRewritableLayout? rewritableLayout = null;
            Action<Stream, CancellationToken> produceContent = request.ProduceContent;
            if (rewritableSession is not null)
            {
                ReportOperation("锁定并检查单层 BD-RE 盘片");
                rewritableSession.Acquire();
                BluRayRewritableLayout layout = BluRayRewritableBurn.Prepare(
                    rewritableSession, request, cancellationToken, ReportOperation);
                rewritableLayout = layout;
                ReportDiagnostic($"BD-RE 无备用区：LBA 0 → PSN 0x{layout.FirstPhysicalSector:X8}，"
                    + $"连续用户扇区={layout.SectorCount:N0}。");
                var mappedProducer = request.ProduceMappedBluRayContent!;
                produceContent = (output, token) => mappedProducer(output, layout.FirstPhysicalSector, layout.SectorCount, token);
            }

            if (request.MediaKind != OpticalBurnMediaKind.DvdData && !cdRwNeedsErase)
                SelectAutomaticWriteSpeed();

            using GeneratedContentComStream content = new(
                request.ContentLength,
                produceContent,
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
            currentOperation = "预填充生成缓冲区";
            try { content.WaitUntilPrebuffered(cancellationToken); }
            catch (Exception exception)
            {
                // Dispose cancels the producer, so preserve its original state first.
                try { exception.Data["Burn.ContentAtFailure"] = content.GetDiagnosticSummary(); } catch { }
                throw;
            }
            BurnDeviceDiagnostics.Report(ReportDiagnostic, content.GetDiagnosticSummary);
            // Preparation may take time. Recheck the loaded medium before writing.
            currentOperation = "再次检查当前盘片兼容性";
            try
            {
                if (rewritableSession is null
                    && ValidateRequestedMedia(request.MediaKind, (object)recorder, (object)format) != physicalMediaType)
                    throw new InvalidOperationException("盘片已更换，请重新开始刻录。");
                if (rewritableSession is not null && rewritableSession.ReadRewritableLayout() != rewritableLayout)
                    throw new InvalidOperationException("BD-RE 物理布局已变化，请重新开始刻录。");
            }
            catch (Exception exception)
            {
                try { exception.Data["Burn.ContentAtFailure"] = content.GetDiagnosticSummary(); } catch { }
                throw;
            }

            OpticalWriteSpeed? actualWriteSpeed;
            if (cdDao)
            {
                if (cdRwNeedsErase)
                {
                    ReportOperation("锁定并检查 CD-RW 盘片");
                    cancellationToken.ThrowIfCancellationRequested();
                    var transport = new ImapiMmcCommandTransport((object)recorder);
                    var commands = new BluRayMmcCommands(transport);
                    commands.PreventRemoval(true);
                    removalProtection = commands;

                    PrepareCdRwForBurn(request, () =>
                    {
                        if (ValidateRequestedMedia(request.MediaKind, (object)recorder, (object)format!) != CdRewritableMediaType)
                            throw new InvalidOperationException("盘片已更换，请重新开始刻录。");
                        return ReadCdMediaCapacity(ReadCdAtipSectorCount(transport),
                            Convert.ToBoolean(format!.MediaPhysicallyBlank), CdRewritableMediaType);
                    }, () =>
                    {
                        ReportOperation("正在快速擦除 CD-RW，擦除完成后将继续刻录…");
                        cancellationToken.ThrowIfCancellationRequested();
                        EraseRewritableMedia((object)recorder, "CD-RW");
                        cancellationToken.ThrowIfCancellationRequested();
                        ReportOperation("重新检查已擦除的 CD-RW 盘片");
                        ImapiCom.ReleaseComObject((object)format!);
                        format = null;
                        format = ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2RawCD");
                        format.Recorder = recorder;
                        format.ClientName = "Disc Image Studio";
                    }, cancellationToken);

                    if (!(bool)format.IsRecorderSupported(recorder))
                        throw new InvalidOperationException("所选光驱不支持这种刻录方式。");
                    if (!(bool)format.IsCurrentMediaSupported(recorder))
                        throw new InvalidOperationException("当前 CD-RW 盘片无法进行 RAW DAO 刻录。");
                    SelectAutomaticWriteSpeed();
                }

                using ICdRawImageSession imageSession = request.MediaKind == OpticalBurnMediaKind.CdData
                    ? new CdDataRawImageSession() : new DynamicCdRawImageSession();
                actualWriteSpeed = BurnCdDao(
                    new DynamicCdRawSession((object)format, writeProgress.ReportRawCdUpdate),
                    imageSession,
                    content,
                    request,
                    cancellationToken,
                    ReportOperation,
                    speed => ReportWriteSpeed(progress, speed, request),
                    CaptureFailure,
                    message => cleanupWarnings.Add(message),
                    ReportDiagnostic);
            }
            else if (rewritableSession is not null)
            {
                ReportOperation("设置 BD-RE 写入速度");
                actualWriteSpeed = rewritableSession.ConfigureRewritableWrite(request.WriteSpeed);
                if (actualWriteSpeed is not null)
                    ReportWriteSpeed(progress, actualWriteSpeed, request);
                else
                    ReportDiagnostic("BD-RE：刻录接口未报告实际写入速度。");
                using CancellationTokenRegistration registration = cancellationToken.Register(rewritableSession.CancelWrite);
                cancellationToken.ThrowIfCancellationRequested();
                ReportOperation("正在写入单层 BD-RE");
                try
                {
                    rewritableSession.Write(content, checked((uint)(request.ContentLength / BluRaySequentialWriter.SectorBytes)));
                }
                catch (Exception exception)
                {
                    try { exception.Data["Burn.ContentAtFailure"] = content.GetDiagnosticSummary(); } catch { }
                    CaptureFailure(exception);
                    try { rewritableSession.Flush(); }
                    catch (Exception flushError) { cleanupWarnings.Add($"BD-RE 缓存刷新失败：{flushError.Message}"); }
                    throw;
                }
                ReportOperation("刷新 BD-RE 缓存（请等待完成）");
                rewritableSession.Flush();
                cancellationToken.ThrowIfCancellationRequested();
                ReportOperation("释放 BD-RE 刻录机访问权");
                rewritableSession.Release();
            }
            else if (request.MediaKind == OpticalBurnMediaKind.BluRayData)
            {
                // IDiscFormat2Data.Write formats virgin BD-R with spare areas.
                // The lower-level write engine only sends WRITE commands, so
                // the unformatted track keeps the LBA/physical-address mapping.
                using DynamicBluRaySequentialSession session = CreateBluRaySession();
                actualWriteSpeed = BurnBluRay(session, content, request,
                    cancellationToken, ReportOperation,
                    speed => ReportWriteSpeed(progress, speed, request),
                    reportCleanupWarning: cleanupWarnings.Add,
                    reportFailure: CaptureFailure,
                    reportDiagnostic: ReportDiagnostic);
            }
            else
            {
                currentOperation = "准备 DVD 盘片";
                int mediaType = Convert.ToInt32(format.CurrentPhysicalMediaType);
                if (IsRewritableDvd(mediaType))
                {
                    var commands = new BluRayMmcCommands(new ImapiMmcCommandTransport((object)recorder));
                    commands.PreventRemoval(true);
                    removalProtection = commands;
                    ValidateRequestedMedia(request.MediaKind, recorder, format);
                    if (Convert.ToInt32(format.CurrentPhysicalMediaType) != mediaType)
                        throw new InvalidOperationException("盘片已更换，请重新开始刻录。");

                    long capacity = ReadDvdSectorCount(mediaType, Convert.ToInt64(format.TotalSectorsOnMedia));
                    ValidateDataMedia(request, BlankMediaState, capacity, capacity);
                    int mediaState = Convert.ToInt32(format.CurrentMediaStatus);
                    ReportDiagnostic($"DVD 擦除前状态：MediaState=0x{mediaState:X8}。");
                    if ((mediaState & WriteProtectedMediaState) != 0)
                        throw new InvalidOperationException("当前 DVD±RW 盘片或刻录机已启用写保护。");

                    if (DvdNeedsErase((object)format, mediaType, mediaState)
                        && request.ConfirmDvdDataLoss is Func<string, bool> confirmDataLoss)
                    {
                        string medium = mediaType == DvdPlusRw ? "DVD+RW" : "DVD-RW";
                        cancellationToken.ThrowIfCancellationRequested();
                        if (confirmDataLoss(medium) != true)
                            throw new OperationCanceledException("已取消清除盘片数据。");
                        cancellationToken.ThrowIfCancellationRequested();
                        if (mediaType == DvdMinusRw)
                        {
                            // A quick erase returns DVD-RW to the blank state, so the
                            // burn continues like any blank-disc session.
                            ReportOperation("正在擦除 DVD-RW，擦除完成后将继续刻录…");
                            EraseRewritableMedia((object)recorder, "DVD±RW");
                            cancellationToken.ThrowIfCancellationRequested();
                            ImapiCom.ReleaseComObject((object)format);
                            format = null;
                            format = ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Data");
                            format.Recorder = recorder;
                            format.ClientName = "Disc Image Studio";
                            ValidateRequestedMedia(request.MediaKind, recorder, format);
                            if (Convert.ToInt32(format.CurrentPhysicalMediaType) != mediaType)
                                throw new InvalidOperationException("盘片已更换，请重新开始刻录。");
                        }
                        else
                        {
                            // Erasing cannot reset a formatted DVD+RW: the confirmed
                            // rewrite reuses the existing format through ForceOverwrite.
                            request = request with { OverwriteRewritableMedia = true };
                        }
                    }
                }

                if (!(bool)format.IsCurrentMediaSupported(recorder))
                    throw new InvalidOperationException("当前 DVD 盘片不可写入。");
                SelectAutomaticWriteSpeed();
                currentOperation = "写入 DVD 数据";
                Action<OpticalWriteSpeed> reportDataWriteSpeed =
                    speed => ReportWriteSpeed(progress, speed, request);
                actualWriteSpeed = BurnData(
                    new DynamicDvdDataSession((object)format, writeProgress.ReportDataUpdate),
                    content,
                    request,
                    cancellationToken,
                    reportDataWriteSpeed,
                    ReportOperation,
                    CaptureFailure,
                    ReportDiagnostic);
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
            CaptureFailure(exception);
            burnFailure = new InvalidOperationException(
                $"Windows 刻录接口在“{currentOperation}”时返回错误 "
                + $"0x{exception.HResult:X8}：{exception.Message}",
                exception);
        }
        catch (Exception exception)
        {
            CaptureFailure(exception);
            burnFailure = exception;
        }
        finally
        {
            try
            {
                CompleteBurnCleanup(burnFailure, cleanupWarnings,
                    () => { if (format is not null) ImapiCom.ReleaseComObject((object)format); },
                    () => ImapiCom.ReleaseComObject((object)recorder),
                    allowRemoval: removalProtection is null ? null : () => removalProtection.PreventRemoval(false),
                    ejectMedia: request.EjectOnSuccess && result is not null ? () => recorder.EjectMedia() : null,
                    reportEjectDiagnostic: ReportDiagnostic,
                    cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                CaptureFailure(exception);
                throw;
            }
        }

        progress?.Report(new OpticalBurnProgress(
            "complete",
            bluRayRewritable ? "BD-RE 刻录完成，设备缓存已刷新。" : "刻录完成，盘片会话已关闭。",
            request.ContentLength,
            request.ContentLength));
        return result!;

        DynamicBluRaySequentialSession CreateBluRaySession()
            => new((object)recorder, (object)format!, writeProgress.ReportAcceptedBytes,
                reportCleanupWarning: cleanupWarnings.Add,
                reportDiagnostics: ReportDiagnostic,
                reportRecordedLba: writeProgress.ReportRecordedSectors);

        void SelectAutomaticWriteSpeed()
        {
            if (request.WriteSpeed is not null)
                return;
            currentOperation = "选择最低刻录速度";
            IReadOnlyList<OpticalWriteSpeed> supportedSpeeds;
            try { supportedSpeeds = ReadSupportedWriteSpeeds(format!); }
            catch (COMException) when (bluRayRewritable) { supportedSpeeds = []; }
            BurnDeviceDiagnostics.Report(ReportDiagnostic, () => "可选写入速度："
                + (supportedSpeeds.Count == 0 ? "none" : string.Join(" | ", supportedSpeeds.Select(BurnDeviceDiagnostics.DescribeSpeed))) + "。");
            request = request with { WriteSpeed = ResolveWriteSpeed(null, supportedSpeeds) };
            progress?.Report(new OpticalBurnProgress("speed",
                request.WriteSpeed is null
                    ? bluRayRewritable
                        ? "当前 BD-RE 未报告可选速度，将使用光驱当前设置…"
                        : "当前盘片未报告可选速度，将使用刻录接口的保守回退设置…"
                    : $"自动选择最低写入速度：{request.WriteSpeed.GetMultiplier(request.MediaKind):0.#}×"
                        + $"（{request.WriteSpeed.GetMegabytesPerSecond(request.MediaKind):0.0} MB/s）…",
                0, request.ContentLength));
        }

        void ReportDiagnostic(string message)
            => progress?.Report(new OpticalBurnProgress("diagnostic", message, 0, request.ContentLength));

        void ReportOperation(string operation)
        {
            currentOperation = operation;
            writeProgress.ReportStage(GetBurnOperationStage(operation), operation);
        }

        void CaptureFailure(Exception exception)
        {
            bool firstSnapshot = !exception.Data.Contains("Burn.ProgressAtFailure");
            OpticalBurnDiagnostics.CaptureFailure(exception, currentOperation, writeProgress.GetDiagnosticSummary());
            try
            {
                if (!exception.Data.Contains("Burn.DeviceContext"))
                    exception.Data["Burn.DeviceContext"] = string.Join("\n", new[]
                        { environmentDiagnostic, recorderDiagnostic, physicalMediaDiagnostic }.Where(value => value is not null));
            }
            catch { }
            if (firstSnapshot)
            {
                try
                {
                    progress?.Report(new("diagnostic",
                        $"已捕获刻录异常：Operation={exception.Data["Burn.Operation"]}；HRESULT=0x{exception.HResult:X8}；"
                            + $"{exception.Data["Burn.ProgressAtFailure"]}", 0, request.ContentLength));
                }
                catch { }
            }
        }
    }

    internal static string GetBurnOperationStage(string operation) => operation switch
    {
        "CD 原始写入调用已返回" or "DVD 写入调用已返回" or "BD-R 数据传输调用已返回" => "write-complete",
        "刷新 BD-R 缓存（请等待完成）" or "刷新 BD-RE 缓存（请等待完成）" => "flush",
        "关闭 BD-R 轨道（请等待完成）" => "close-track",
        "封闭 BD-R 盘片（请等待完成）" => "close-disc",
        "关闭 CD 刻录会话" => "finalize",
        "释放 BD-R 刻录机访问权" or "释放 BD-RE 刻录机访问权" => "release",
        _ when operation.Contains("校验", StringComparison.Ordinal) => "diagnostic",
        _ => "media",
    };

    // The burn exception is captured before entering this final cleanup boundary.
    // Every caller-owned COM object gets its release attempt before we propagate
    // that exception or report an otherwise successful burn's cleanup failure.
    internal static void CompleteBurnCleanup(Exception? burnFailure, ICollection<string> cleanupWarnings,
        Action releaseFormat, Action releaseRecorder, Action? allowRemoval = null, Action? ejectMedia = null,
        Action<string>? reportEjectDiagnostic = null, CancellationToken cancellationToken = default)
    {
        Exception? firstCleanupFailure = null;
        if (allowRemoval is not null)
            ReleaseOne(allowRemoval, "解除盘片弹出保护");
        ReleaseOne(releaseFormat, "释放刻录格式 COM 对象");

        // Eject through the same recorder while it is still available, after the writer and media lock are released.
        if (ejectMedia is not null && burnFailure is null && cleanupWarnings.Count == 0
            && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                ejectMedia();
                BurnDeviceDiagnostics.Report(reportEjectDiagnostic, () => "刻录完成后已自动弹出盘片。");
            }
            catch (Exception exception)
            {
                BurnDeviceDiagnostics.Report(reportEjectDiagnostic,
                    () => $"刻录已完成，自动弹出盘片失败（HRESULT 0x{exception.HResult:X8}）：{exception.Message}");
            }
        }
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
                OpticalBurnDiagnostics.CaptureFailure(exception, operation);
                firstCleanupFailure ??= exception;
                Exception owner = burnFailure ?? firstCleanupFailure;
                if (!ReferenceEquals(owner, exception))
                    owner.Data[$"Burn.Cleanup.{operation}"] = OpticalBurnDiagnostics.FormatException(exception);
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
        Action<string>? reportCleanupWarning = null,
        Action<Exception>? reportFailure = null,
        Action<string>? reportDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(content);
        request.Validate();
        if (request.MediaKind != OpticalBurnMediaKind.BluRayData)
            throw new ArgumentException("顺序 BD-R 通道只接受蓝光数据。", nameof(request));

        bool acquired = false;
        bool writeStarted = false;
        bool writeReturned = false;
        bool flushed = false;
        Exception? operationException = null;
        string operation = "准备 BD-R 刻录";
        Stopwatch burnTimer = Stopwatch.StartNew();
        TimeSpan operationStarted = burnTimer.Elapsed;
        string? requestDiagnostic = null;
        string? mediaDiagnostic = null;
        string? speedDiagnostic = null;
        try
        {
            requestDiagnostic = "BD-R 请求：RequestedSpeed=" + BurnDeviceDiagnostics.DescribeSpeed(request.WriteSpeed)
                + $"；ContentBytes={request.ContentLength}；SectorBytes=2048；TrackSectors={request.ContentLength / 2048}。";
            Diagnose(() => requestDiagnostic);
            Diagnose(content.GetDiagnosticSummary);
            cancellationToken.ThrowIfCancellationRequested();
            SetOperation("锁定 BD-R 盘片");
            session.Acquire();
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            SetOperation("检查未格式化 BD-R 的原始容量");
            BluRaySequentialMediaInfo media = session.ReadMedia();
            string profile = media.Profile switch
            {
                0x40 => "BD-ROM",
                0x41 => "BD-R sequential",
                0x42 => "BD-R random recording",
                0x43 => "BD-RE",
                _ => "Unknown",
            };
            // Use the required pre-write read; never probe the recorder again on failure.
            mediaDiagnostic = $"BD-R 介质：Profile=0x{media.Profile:X4} ({profile})；DiscIsBlank={media.DiscIsBlank}；"
                + $"FormatStatus={media.FormatStatus}；TrackIsBlank={media.TrackIsBlank}；"
                + $"NextWritableAddressValid={media.NextWritableAddressValid}；TrackStartAddress={media.TrackStartAddress}；"
                + $"NextWritableAddress={media.NextWritableAddress}；FreeBlocks={media.FreeBlocks}；"
                + $"TrackSize={media.TrackSize}；MaximumBlocks={media.MaximumBlocks}；TrackNumber={media.TrackNumber}。";
            Diagnose(() => mediaDiagnostic);
            ValidateBluRaySequentialMedia(request, media);
            SetOperation("设置 BD-R 写入速度");
            OpticalWriteSpeed speed = session.ConfigureWrite(request.WriteSpeed);
            speedDiagnostic = "BD-R 写入速度：Requested=" + BurnDeviceDiagnostics.DescribeSpeed(request.WriteSpeed)
                + "；Actual=" + BurnDeviceDiagnostics.DescribeSpeed(speed) + "。";
            Diagnose(() => speedDiagnostic);
            speed.Validate();
            reportWriteSpeed?.Invoke(speed);
            cancellationToken.ThrowIfCancellationRequested();
            SetOperation("BD-R 刻后读回校验：未启用");
            Diagnose(content.GetDiagnosticSummary);
            SetOperation($"无备用区顺序写入 BD-R：{request.ContentLength / 2048:N0} 扇区");
            using (cancellationToken.Register(session.CancelWrite))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writeStarted = true;
                session.Write(content, checked((uint)(request.ContentLength / 2048)));
                writeReturned = true;
            }
            SetOperation("BD-R 数据传输调用已返回");
            cancellationToken.ThrowIfCancellationRequested();

            // Once finalization starts, finish flushing/closing before releasing
            // the drive. Cancelling an in-flight CLOSE command is not safe.
            SetOperation("刷新 BD-R 缓存（请等待完成）");
            session.Flush();
            flushed = true;
            SetOperation("关闭 BD-R 轨道（请等待完成）");
            session.CloseTrack(media.TrackNumber);
            SetOperation("封闭 BD-R 盘片（请等待完成）");
            session.CloseDisc();
            return speed;
        }
        catch (Exception exception)
        {
            operationException = exception;
            RecordFailure(exception);
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
                    SetOperation("释放 BD-R 刻录机访问权");
                    try { session.Release(); ReportOperationTiming("completed"); }
                    catch (Exception exception) { RecordFailure(exception); throw; }
                }
                else
                {
                    // Preserve the original failure and drain any accepted
                    // blocks, but never finalize a failed or cancelled image.
                    if (writeStarted && !flushed)
                    {
                        long flushStarted = Stopwatch.GetTimestamp();
                        try
                        {
                            session.Flush();
                            Diagnose(() => $"BD-R 失败后清理：Flush returned；ElapsedMs={Stopwatch.GetElapsedTime(flushStarted).TotalMilliseconds:F0}。");
                        }
                        catch (Exception exception)
                        {
                            TimeSpan elapsed = Stopwatch.GetElapsedTime(flushStarted);
                            Diagnose(() => $"BD-R 失败后清理：Flush failed；HRESULT=0x{exception.HResult:X8}；ElapsedMs={elapsed.TotalMilliseconds:F0}。");
                            try
                            {
                                OpticalBurnDiagnostics.CaptureFailure(exception, "蓝光失败后刷新缓存（清理）");
                                operationException.Data["Burn.CleanupFlushException"] = OpticalBurnDiagnostics.FormatException(exception);
                                reportCleanupWarning?.Invoke($"蓝光失败后刷新缓存失败（HRESULT 0x{exception.HResult:X8}，"
                                    + $"耗时 {elapsed.TotalSeconds:F3} 秒）：{exception.Message}");
                            }
                            catch { /* Diagnostics must not replace the write failure or prevent release. */ }
                        }
                    }
                    long releaseStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        session.Release();
                        Diagnose(() => $"BD-R 失败后清理：Release returned；ElapsedMs={Stopwatch.GetElapsedTime(releaseStarted).TotalMilliseconds:F0}。");
                    }
                    catch (Exception exception)
                    {
                        Diagnose(() => $"BD-R 失败后清理：Release failed；HRESULT=0x{exception.HResult:X8}；ElapsedMs={Stopwatch.GetElapsedTime(releaseStarted).TotalMilliseconds:F0}。");
                        try
                        {
                            operationException.Data["Burn.CleanupReleaseException"] = OpticalBurnDiagnostics.FormatException(exception);
                            reportCleanupWarning?.Invoke($"蓝光失败后释放设备失败（HRESULT 0x{exception.HResult:X8}）：{exception.Message}");
                        }
                        catch { }
                    }
                }
            }
        }

        void SetOperation(string value)
        {
            ReportOperationTiming("returned");
            operation = value;
            operationStarted = burnTimer.Elapsed;
            try { reportOperation?.Invoke(value); } catch { }
        }

        void RecordFailure(Exception exception)
        {
            // Capture before cleanup can drain the source, cancel generation or release the device.
            try
            {
                if (!exception.Data.Contains("Burn.BluRayContext"))
                {
                    string timing = $"BD-R 失败：Operation={operation}；ElapsedMs={burnTimer.Elapsed.TotalMilliseconds:F0}；"
                        + $"OperationElapsedMs={(burnTimer.Elapsed - operationStarted).TotalMilliseconds:F0}；"
                        + $"WriteStarted={writeStarted}；WriteReturned={writeReturned}；FlushReturned={flushed}；"
                        + $"CancellationRequested={cancellationToken.IsCancellationRequested}。";
                    exception.Data["Burn.BluRayContext"] = string.Join("\n", new[]
                        { timing, requestDiagnostic, mediaDiagnostic, speedDiagnostic }.Where(value => value is not null));
                    exception.Data["Burn.ContentAtFailure"] = content.GetDiagnosticSummary();
                    Diagnose(() => timing);
                    Diagnose(() => (string)exception.Data["Burn.ContentAtFailure"]!);
                }
            }
            catch { }
            // MMC exceptions already carry real sense data; keep it unchanged.
            ReportOperationTiming("failed");
            OpticalBurnDiagnostics.CaptureFailure(exception, operation);
            try { reportFailure?.Invoke(exception); } catch { }
        }

        void Diagnose(Func<string?> describe) => BurnDeviceDiagnostics.Report(reportDiagnostic, describe);

        void ReportOperationTiming(string result)
            => Diagnose(() => $"BD-R 阶段：Operation={operation}；Result={result}；"
                + $"ElapsedMs={(burnTimer.Elapsed - operationStarted).TotalMilliseconds:F0}。");
    }

    internal static void ValidateBluRaySequentialMedia(
        OpticalBurnRequest request, BluRaySequentialMediaInfo media)
    {
        request.Validate();
        if (request.MediaKind != OpticalBurnMediaKind.BluRayData)
            throw new ArgumentException("顺序 BD-R 通道只接受蓝光数据。", nameof(request));
        ValidateBluRaySequentialLayout(media);

        long requiredSectors = request.ContentLength / 2048;
        if (requiredSectors % 32 != 0 || requiredSectors > BluRaySingleLayerSectors)
            throw new InvalidOperationException("蓝光绘图总扇区数必须是 32 的倍数，且不超过 12,219,392。");
        if (media.FreeBlocks < requiredSectors)
            throw new InvalidOperationException(
                $"蓝光盘容量不足：需要 {requiredSectors:N0} 扇区，原始轨道可写 {media.FreeBlocks:N0} 扇区。不会自动缩小绘图容量。");
    }

    private static void ValidateBluRaySequentialLayout(BluRaySequentialMediaInfo media)
    {
        if (!IsBluRaySequentialWritable(media, out string reason))
            throw new InvalidOperationException(reason);
    }

    /// <summary>Shared writability rule: the probe reports zero usable sectors for every layout
    /// that the sequential BD-R burn path would reject, so card and burner cannot disagree.</summary>
    internal static bool IsBluRaySequentialWritable(BluRaySequentialMediaInfo media, out string reason)
    {
        if (!TryReadBluRaySectorCount(media.MaximumBlocks, media.FormatStatus,
            media.SpareClusters, out _, out reason))
            return false;
        if (media.Profile != BluRaySequentialProfile)
        {
            reason = "无备用区绘图需要顺序记录 BD-R（SRM）；不支持 BD-RE 或随机记录 BD-R。";
            return false;
        }
        if (!media.DiscIsBlank || !media.TrackIsBlank || !media.NextWritableAddressValid
            || media.NextWritableAddress != 0 || media.TrackStartAddress != 0 || media.TrackNumber != 1)
        {
            reason = "无备用区绘图需要从 LBA 0 开始的全新空白 BD-R，已取消刻录。";
            return false;
        }
        if (media.FormatStatus != 1)
        {
            reason = "BD-R 已格式化或格式状态未知；无法保证无备用区的物理位置，请使用未格式化的新盘。";
            return false;
        }

        if (media.MaximumBlocks != BluRaySingleLayerSectors
            || media.TrackSize != media.MaximumBlocks || media.FreeBlocks != media.TrackSize)
        {
            reason = $"盘片原始容量或轨道布局不符合单层无备用区 BD-R：最大 {media.MaximumBlocks:N0}、"
                + $"轨道 {media.TrackSize:N0}、可写 {media.FreeBlocks:N0} 扇区；需要完整的 12,219,392 扇区布局。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static OpticalWriteSpeed BurnCdAudio(
        ICdRawSession format,
        ICdRawImageSession imageSession,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<string>? reportOperation = null,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null,
        Action<string>? reportDiagnostic = null)
        => BurnCdDao(format, imageSession, content, request, cancellationToken, reportOperation, reportWriteSpeed,
            reportDiagnostic: reportDiagnostic);

    internal static OpticalWriteSpeed BurnCdDao(
        ICdRawSession format,
        ICdRawImageSession imageSession,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<string>? reportOperation = null,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null,
        Action<Exception>? reportFailure = null,
        Action<string>? reportCleanupWarning = null,
        Action<string>? reportDiagnostic = null)
    {
        if (!OpticalBurnMedia.UsesCdDao(request.MediaKind))
            throw new ArgumentException("CD DAO requires an audio or CD data request.", nameof(request));
        string trackDescription = request.MediaKind == OpticalBurnMediaKind.CdData ? "数据轨" : "音轨";
        bool prepared = false;
        Exception? operationException = null;
        string operation = "准备 CD 刻录";
        Stopwatch daoTimer = Stopwatch.StartNew();
        TimeSpan operationStarted = daoTimer.Elapsed;
        string? layoutDiagnostic = null;
        string? mediaDiagnostic = null;
        string? blankDiagnostic = null;
        string? sectorDiagnostic = null;
        string? speedDiagnostic = null;
        bool writeStarted = false;
        bool writeReturned = false;
        try
        {
            Diagnose(() => "CD DAO 请求：RequestedSpeed=" + BurnDeviceDiagnostics.DescribeSpeed(request.WriteSpeed)
                + $"；ContentBytes={request.ContentLength}；SectorBytes={OpticalBurnMedia.GetSectorBytes(request.MediaKind)}。");
            Diagnose(content.GetDiagnosticSummary);
            SetOperation("准备并锁定 CD 介质");
            format.PrepareMedia();
            prepared = true;
            SetOperation("检查 CD 是否为空白盘");
            bool physicallyBlank = format.MediaPhysicallyBlank;
            blankDiagnostic = $"CD DAO 空白状态：MediaPhysicallyBlank={physicallyBlank}（写入前检查）。";
            Diagnose(() => blankDiagnostic);
            if (!physicallyBlank)
            {
                throw new InvalidOperationException("CD 不是空白盘，已取消刻录。");
            }

            SetOperation("设置 CD DAO 扇区格式");
            IReadOnlyList<int> supportedSectorTypes = format.SupportedSectorTypes;
            int sectorType = ResolveCdDaoSectorType(supportedSectorTypes);
            sectorDiagnostic = DescribeCdDaoSectorType(sectorType, supportedSectorTypes);
            Diagnose(() => sectorDiagnostic);
            format.SetRequestedSectorType(sectorType);
            if (request.WriteSpeed is not null)
            {
                SetOperation("设置 CD 刻录速度");
                format.SetWriteSpeed(
                    request.WriteSpeed.SectorsPerSecond,
                    request.WriteSpeed.RotationTypeIsPureCav);
            }
            else
            {
                SetOperation("使用 CD DAO 保守回退速度");
                format.SetWriteSpeed(CdDaoFallbackSpeedSectorsPerSecond, false);
            }

            OpticalWriteSpeed actualWriteSpeed = new(
                format.CurrentWriteSpeed,
                format.CurrentRotationTypeIsPureCav);
            actualWriteSpeed.Validate();
            reportWriteSpeed?.Invoke(actualWriteSpeed);
            speedDiagnostic = "CD DAO 写入速度：Requested=" + BurnDeviceDiagnostics.DescribeSpeed(request.WriteSpeed)
                + "；Actual=" + BurnDeviceDiagnostics.DescribeSpeed(actualWriteSpeed) + "。";
            Diagnose(() => speedDiagnostic);
            // Optional settings are cached now; do not query a failing recorder later.
            try { mediaDiagnostic = format.GetDiagnosticSummary(); }
            catch (Exception exception) { mediaDiagnostic = $"CD DAO 介质设置：unavailable (0x{exception.HResult:X8})。"; }
            Diagnose(() => mediaDiagnostic);

            SetOperation($"构造 CD DAO {trackDescription}");
            IStream rawImage = imageSession.CreateImage(content, sectorType);
            long startOfLeadout = imageSession.StartOfLeadout;
            long lastPossibleStartOfLeadout = format.LastPossibleStartOfLeadout;
            long excessSectors = startOfLeadout - lastPossibleStartOfLeadout;
            layoutDiagnostic = $"CD DAO 容量：TrackSectors={request.ContentLength / OpticalBurnMedia.GetSectorBytes(request.MediaKind)}；"
                + $"StartOfLeadout={startOfLeadout}；LastPossibleStartOfLeadout={lastPossibleStartOfLeadout}；"
                + $"LeadoutMarginSectors={-excessSectors}。";
            Diagnose(() => layoutDiagnostic);
            // The current recorder's prepared-media boundary is authoritative,
            // including if the medium changed after the read-only capacity query.
            // The conservative CD payload reservation was applied before generation;
            // do not subtract it from this DAO boundary a second time.
            if (excessSectors > 0)
            {
                throw new InvalidOperationException(
                    $"CD DAO lead-out 超出盘片边界 {excessSectors} 个扇区；"
                    + "已取消刻录，不允许超刻。");
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                format.CancelWrite);
            SetOperation("CD 刻后读回校验：未启用");
            Diagnose(content.GetDiagnosticSummary);
            SetOperation($"写入 CD DAO {trackDescription}");
            writeStarted = true;
            format.WriteMedia(rawImage);
            writeReturned = true;
            SetOperation("CD 原始写入调用已返回");
            return actualWriteSpeed;
        }
        catch (Exception exception)
        {
            operationException = exception;
            RecordFailure(exception);
            throw;
        }
        finally
        {
            if (prepared)
            {
                if (operationException is null)
                {
                    SetOperation("关闭 CD 刻录会话");
                    try { format.ReleaseMedia(); ReportOperationTiming("completed"); }
                    catch (Exception exception) { RecordFailure(exception); throw; }
                }
                else
                {
                    long releaseStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        format.ReleaseMedia();
                        Diagnose(() => $"CD DAO 失败后清理：ReleaseMedia returned；ElapsedMs={Stopwatch.GetElapsedTime(releaseStarted).TotalMilliseconds:F0}。");
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            operationException.Data["Burn.CleanupReleaseException"] = OpticalBurnDiagnostics.FormatException(exception);
                            reportCleanupWarning?.Invoke($"CD 失败后关闭会话失败（HRESULT 0x{exception.HResult:X8}）：{exception.Message}");
                        }
                        catch { /* Preserve the original write failure. */ }
                    }
                }
            }
        }

        void SetOperation(string value)
        {
            ReportOperationTiming("returned");
            operation = value;
            operationStarted = daoTimer.Elapsed;
            try { reportOperation?.Invoke(value); } catch { }
        }

        void RecordFailure(Exception exception)
        {
            // Cache before ReleaseMedia/Dispose, which can cancel the producer and change telemetry.
            try
            {
                if (!exception.Data.Contains("Burn.CdDaoContext"))
                {
                    string timing = $"CD DAO 失败：Operation={operation}；ElapsedMs={daoTimer.Elapsed.TotalMilliseconds:F0}；"
                        + $"OperationElapsedMs={(daoTimer.Elapsed - operationStarted).TotalMilliseconds:F0}；"
                        + $"WriteStarted={writeStarted}；WriteReturned={writeReturned}；CancellationRequested={cancellationToken.IsCancellationRequested}。";
                    exception.Data["Burn.CdDaoContext"] = string.Join("\n", new[]
                        { timing, sectorDiagnostic, speedDiagnostic, mediaDiagnostic, blankDiagnostic, layoutDiagnostic }.Where(value => value is not null));
                    exception.Data["Burn.ContentAtFailure"] = content.GetDiagnosticSummary();
                    Diagnose(() => timing);
                    Diagnose(() => (string)exception.Data["Burn.ContentAtFailure"]!);
                    if (writeStarted && !writeReturned)
                    {
                        const string sense = "unavailable: IMAPI RawCD.WriteMedia does not expose the failing command's SCSI sense; no post-failure device query was made.";
                        exception.Data["Burn.SenseStatus"] = sense;
                        Diagnose(() => "CD DAO Sense：" + sense);
                    }
                }
            }
            catch { }
            OpticalBurnDiagnostics.CaptureFailure(exception, operation);
            try { reportFailure?.Invoke(exception); } catch { }
        }

        void Diagnose(Func<string?> describe) => BurnDeviceDiagnostics.Report(reportDiagnostic, describe);

        void ReportOperationTiming(string result)
            => Diagnose(() => $"CD DAO 阶段：Operation={operation}；Result={result}；"
                + $"ElapsedMs={(daoTimer.Elapsed - operationStarted).TotalMilliseconds:F0}。");
    }

    internal static OpticalWriteSpeed BurnData(
        IDvdDataSession session,
        GeneratedContentComStream content,
        OpticalBurnRequest request,
        CancellationToken cancellationToken,
        Action<OpticalWriteSpeed>? reportWriteSpeed = null,
        Action<string>? reportOperation = null,
        Action<Exception>? reportFailure = null,
        Action<string>? reportDiagnostic = null)
    {
        string operation = "准备 DVD 刻录";
        Stopwatch burnTimer = Stopwatch.StartNew();
        TimeSpan operationStarted = burnTimer.Elapsed;
        int? mediaState = null;
        long? freeSectors = null;
        long? totalSectors = null;
        bool overwrite = false;
        long requiredSectors = request.ContentLength / 2048;
        string requestDiagnostic = "DVD 请求：RequestedSpeed=" + BurnDeviceDiagnostics.DescribeSpeed(request.WriteSpeed)
            + $"；ContentBytes={request.ContentLength}；SectorBytes=2048；RequiredSectors={requiredSectors}。";
        string? settingsDiagnostic = null;
        string? speedDiagnostic = null;
        bool writeStarted = false;
        bool writeReturned = false;
        try
        {
            Diagnose(() => requestDiagnostic);
            Diagnose(content.GetDiagnosticSummary);
            SetOperation("检查 DVD 介质状态和容量");
            mediaState = session.CurrentMediaStatus;
            freeSectors = session.FreeSectorsOnMedia;
            totalSectors = session.TotalSectorsOnMedia;
            overwrite = CanOverwriteMedia(request, mediaState.Value);
            Diagnose(DescribeCapacity);
            ValidateDataMedia(request, mediaState.Value, freeSectors.Value, totalSectors.Value);

            SetOperation("设置 DVD 缓冲保护和封盘选项");
            session.ConfigureMedia(overwrite);
            SetOperation("设置 DVD 刻后校验为 NONE");
            session.DisableVerification();
            SetOperation("DVD 刻后校验：已关闭（NONE）");
            if (request.WriteSpeed is not null)
            {
                SetOperation("设置 DVD 刻录速度");
                session.SetWriteSpeed(request.WriteSpeed);
            }

            SetOperation("读取 DVD 实际刻录速度");
            OpticalWriteSpeed actualWriteSpeed = session.CurrentWriteSpeed;
            actualWriteSpeed.Validate();
            speedDiagnostic = "DVD 写入速度：Requested=" + BurnDeviceDiagnostics.DescribeSpeed(request.WriteSpeed)
                + "；Actual=" + BurnDeviceDiagnostics.DescribeSpeed(actualWriteSpeed) + "。";
            Diagnose(() => speedDiagnostic);
            reportWriteSpeed?.Invoke(actualWriteSpeed);
            // Optional settings are read once before writing, never after an error.
            try { settingsDiagnostic = session.GetDiagnosticSummary(); }
            catch (Exception exception) { settingsDiagnostic = $"DVD 介质设置：unavailable (0x{exception.HResult:X8})。"; }
            Diagnose(() => settingsDiagnostic);
            using CancellationTokenRegistration registration = cancellationToken.Register(session.CancelWrite);
            SetOperation("订阅 DVD 刻录进度事件");
            Diagnose(content.GetDiagnosticSummary);
            SetOperation("写入 DVD 数据（包含 Windows 内部收尾）");
            writeStarted = true;
            session.Write(content, RecordFailure);
            writeReturned = true;
            SetOperation("DVD 写入调用已返回");
            Diagnose(() => $"DVD 写入结果：WriteStarted={writeStarted}；WriteReturned={writeReturned}；ElapsedMs={burnTimer.Elapsed.TotalMilliseconds:F0}。");
            ReportOperationTiming("completed");
            return actualWriteSpeed;
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            throw;
        }

        void RecordFailure(Exception exception)
        {
            // Called inside the adapter's Write catch, before unsubscribing events
            // or waiting for cancellation callbacks. The outer catch handles setup.
            if (exception.Data.Contains("Burn.DvdContext")) return;
            OpticalBurnDiagnostics.CaptureFailure(exception, operation);
            try
            {
                if (!exception.Data.Contains("Burn.DvdContext"))
                {
                    string timing = $"DVD 失败：Operation={operation}；ElapsedMs={burnTimer.Elapsed.TotalMilliseconds:F0}；"
                        + $"OperationElapsedMs={(burnTimer.Elapsed - operationStarted).TotalMilliseconds:F0}；"
                        + $"WriteStarted={writeStarted}；WriteReturned={writeReturned}；CancellationRequested={cancellationToken.IsCancellationRequested}。";
                    exception.Data["Burn.DvdContext"] = string.Join("\n", new[]
                        { timing, requestDiagnostic, DescribeCapacity(), settingsDiagnostic, speedDiagnostic }.Where(value => value is not null));
                    if (!exception.Data.Contains("Burn.ContentAtFailure"))
                        exception.Data["Burn.ContentAtFailure"] = content.GetDiagnosticSummary();
                    Diagnose(() => timing);
                    Diagnose(() => (string)exception.Data["Burn.ContentAtFailure"]!);
                    if (writeStarted && !writeReturned)
                    {
                        const string sense = "unavailable: IMAPI IDiscFormat2Data.Write does not expose the failing command's SCSI sense; no post-failure device query was made.";
                        exception.Data["Burn.SenseStatus"] = sense;
                        Diagnose(() => "DVD Sense：" + sense);
                    }
                }
            }
            catch { }
            try { reportFailure?.Invoke(exception); } catch { }
        }

        string DescribeCapacity()
            => $"DVD 容量：MediaState={(mediaState is int state ? $"0x{state:X8}" : "unavailable")}；"
                + $"FreeSectors={freeSectors?.ToString() ?? "unavailable"}；"
                + $"TotalSectors={totalSectors?.ToString() ?? "unavailable"}；"
                + $"Overwrite={overwrite}；RequiredSectors={requiredSectors}；"
                + $"MarginSectors={(freeSectors - requiredSectors)?.ToString() ?? "unavailable"}。";

        void SetOperation(string value)
        {
            ReportOperationTiming("returned");
            operation = value;
            operationStarted = burnTimer.Elapsed;
            try { reportOperation?.Invoke(value); } catch { }
        }

        void Diagnose(Func<string?> describe) => BurnDeviceDiagnostics.Report(reportDiagnostic, describe);

        void ReportOperationTiming(string result)
            => Diagnose(() => $"DVD 阶段：Operation={operation}；Result={result}；"
                + $"ElapsedMs={(burnTimer.Elapsed - operationStarted).TotalMilliseconds:F0}。");
    }

    private static int ValidateRequestedMedia(
        OpticalBurnMediaKind mediaKind,
        dynamic recorder,
        dynamic format)
    {
        int? currentProfile = null;
        if (mediaKind == OpticalBurnMediaKind.BluRayData)
        {
            // An unformatted BD-RE may not be usable through IMAPI's data writer.
            // Its actual MMC profile selects the direct command path.
            var commands = new BluRayMmcCommands(new ImapiMmcCommandTransport((object)recorder));
            currentProfile = commands.ReadCurrentProfile();
            if (currentProfile == BluRayRewritableProfile) return BluRayRewritableMediaType;
        }

        object currentProfiles = recorder.CurrentProfiles;
        int[] profiles = currentProfiles is Array values
            ? values.Cast<object>().Select(value => Convert.ToInt32(value)).ToArray()
            : [];
        int physicalMediaType = Convert.ToInt32(format.CurrentPhysicalMediaType);
        if (mediaKind == OpticalBurnMediaKind.BluRayData
            ? currentProfile == BluRaySequentialProfile && IsWritableBluRayMedia(physicalMediaType, profiles)
            : IsWritableMedia(mediaKind, physicalMediaType, profiles))
            return physicalMediaType;

        if (mediaKind == OpticalBurnMediaKind.DvdData && IsRewritableDvd(physicalMediaType))
        {
            // Some drives omit the current profile from the IMAPI profile list.
            var commands = new BluRayMmcCommands(new ImapiMmcCommandTransport((object)recorder));
            currentProfile = commands.ReadCurrentProfile();
            if (IsWritableMedia(mediaKind, physicalMediaType, [currentProfile.Value]))
                return physicalMediaType;
        }

        string medium = mediaKind switch
        {
            OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData => "CD-R 或 CD-RW",
            OpticalBurnMediaKind.DvdData => "DVD",
            OpticalBurnMediaKind.BluRayData => "单层顺序 BD-R 或单层 BD-RE（无备用区绘图）",
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind)),
        };
        string reportedProfiles = string.Join(", ", profiles.Select(profile => $"0x{profile:X4}"));
        string directProfile = currentProfile is int direct ? $"；MMC 当前配置档=0x{direct:X4}" : "";
        throw new InvalidOperationException($"所选刻录模式需要可写的 {medium} 盘片；当前盘片不兼容。"
            + $"介质类型=0x{physicalMediaType:X2}；IMAPI 配置档=[{reportedProfiles}]{directProfile}。");
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
                CdRewritableMediaType => currentProfiles.Contains(0xA),
                _ => false,
            },
            OpticalBurnMediaKind.DvdData => physicalMediaType switch
            {
                0x6 => currentProfiles.Contains(0x1B), // DVD+R
                0x7 => currentProfiles.Contains(0x1A), // DVD+RW
                0x8 => currentProfiles.Contains(0x2B), // DVD+R DL
                0x9 => currentProfiles.Contains(0x11), // DVD-R
                0xA => currentProfiles.Contains(0x13) || currentProfiles.Contains(0x14), // DVD-RW
                0xB => currentProfiles.Contains(0x15) || currentProfiles.Contains(0x16), // DVD-R DL
                DvdPlusRwDualLayer => currentProfiles.Contains(0x2A),
                _ => false,
            },
            OpticalBurnMediaKind.BluRayData => physicalMediaType == BluRayRewritableMediaType
                ? currentProfiles.Contains(BluRayRewritableProfile)
                : IsWritableBluRayMedia(physicalMediaType, currentProfiles),
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

    private static bool IsRewritableDvd(int mediaType) => mediaType is DvdPlusRw or DvdMinusRw;

    private static bool IsDvdBlank(dynamic format, int? status = null)
    {
        int mediaState = status ?? Convert.ToInt32(format.CurrentMediaStatus);
        return (mediaState & EraseRequiredMediaState) == 0
            && ((mediaState & BlankMediaState) != 0
                || ((mediaState & OverwriteOnlyMediaState) != 0 && (bool)format.MediaHeuristicallyBlank));
    }

    internal static bool DvdNeedsErase(dynamic format, int? mediaType = null, int? mediaState = null)
        => IsRewritableDvd(mediaType ?? Convert.ToInt32(format.CurrentPhysicalMediaType))
            && !IsDvdBlank((object)format, mediaState);

    internal static bool ValidateWriterSupportBeforeBurn(OpticalBurnRequest request, int physicalMediaType,
        dynamic recorder, dynamic format)
    {
        bool cdDao = OpticalBurnMedia.UsesCdDao(request.MediaKind);
        if (cdDao && request.ConfirmCdRwErase is not null && physicalMediaType != CdRewritableMediaType)
            throw new InvalidOperationException("盘片已更换，请重新开始刻录。");
        bool cdRwNeedsErase = cdDao && physicalMediaType == CdRewritableMediaType && !(bool)format.MediaPhysicallyBlank;
        if (cdRwNeedsErase && request.ConfirmCdRwErase is null)
            throw new InvalidOperationException("CD-RW 不是空白盘，需要确认擦除后才能刻录。");

        try
        {
            if (!(bool)format.IsRecorderSupported(recorder))
                throw new InvalidOperationException("所选光驱不支持这种刻录方式。");
        }
        catch (COMException exception) when (cdRwNeedsErase
            && exception.HResult is RawCdMediaNotBlank or RawCdMediaNotSupported)
        {
            // RAW CD may reject the loaded disc even during the recorder support probe.
            // Repeat the probe after the confirmed erase with the blank disc.
            return true;
        }
        if (cdRwNeedsErase)
            return true;

        // The DVD writer can reject used media that the eraser will prepare later.
        if (!(request.MediaKind == OpticalBurnMediaKind.DvdData && DvdNeedsErase((object)format))
            && !(bool)format.IsCurrentMediaSupported(recorder))
        {
            throw new InvalidOperationException("当前盘片与所选 CD/DVD/蓝光模式不兼容。");
        }

        return false;
    }

    internal static void PrepareCdRwForBurn(OpticalBurnRequest request, Func<OpticalMediaCapacity> readCapacity,
        Action eraseAndRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long requiredSectors = request.ContentLength / OpticalBurnMedia.GetSectorBytes(request.MediaKind);
        OpticalMediaCapacity capacity = readCapacity();
        ValidateCapacity(capacity);
        if (!capacity.RequiresErase)
            return;

        Func<bool> confirmErase = request.ConfirmCdRwErase
            ?? throw new InvalidOperationException("CD-RW 不是空白盘，需要确认擦除后才能刻录。");
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmErase())
            throw new OperationCanceledException("已取消擦除 CD-RW 盘片。");
        cancellationToken.ThrowIfCancellationRequested();
        eraseAndRefresh();
        cancellationToken.ThrowIfCancellationRequested();

        capacity = readCapacity();
        if (capacity.RequiresErase)
            throw new InvalidOperationException("快速擦除后 CD-RW 仍不是空白盘，已取消刻录。");
        ValidateCapacity(capacity);

        void ValidateCapacity(OpticalMediaCapacity media)
        {
            if (media.WritableSectors < requiredSectors)
                throw new InvalidOperationException(
                    $"CD-RW 容量不足：需要 {requiredSectors:N0} 扇区，可写 {media.WritableSectors:N0} 扇区。");
        }
    }

    private static void EraseRewritableMedia(dynamic recorder, string mediaName)
    {
        dynamic eraser = ImapiCom.CreateComObject("IMAPI2.MsftDiscFormat2Erase");
        try
        {
            if (!(bool)eraser.IsRecorderSupported(recorder) || !(bool)eraser.IsCurrentMediaSupported(recorder))
                throw new InvalidOperationException($"Windows 刻录接口无法擦除当前 {mediaName}。");
            eraser.Recorder = recorder;
            eraser.ClientName = "Disc Image Studio";
            eraser.FullErase = false;
            eraser.EraseMedia();
        }
        finally
        {
            ImapiCom.ReleaseComObject((object)eraser);
        }
    }

    private static bool CanOverwriteMedia(OpticalBurnRequest request, int mediaState)
        => request.MediaKind == OpticalBurnMediaKind.DvdData
            && request.OverwriteRewritableMedia
            && (mediaState & BlankMediaState) == 0;

    internal static void ValidateDataMedia(OpticalBurnRequest request, int mediaState, long freeSectors)
        => ValidateDataMedia(request, mediaState, freeSectors, freeSectors);

    internal static void ValidateDataMedia(OpticalBurnRequest request, int mediaState, long freeSectors,
        long totalSectors)
    {
        bool overwrite = CanOverwriteMedia(request, mediaState);
        // Overwriting a formatted disc starts again at LBA 0, so the whole data area
        // is available instead of the free tail behind the previous recording.
        long writableSectors = overwrite ? totalSectors : freeSectors;
        string mediaName = ValidateWritableDataCapacity(request.MediaKind, mediaState, writableSectors, overwrite);
        long requiredSectors = request.ContentLength / 2048;
        if (writableSectors < requiredSectors)
        {
            long deficit = requiredSectors - writableSectors;
            throw new InvalidOperationException(
                $"{mediaName} 剩余容量不足：需要 {requiredSectors:N0} 扇区（{requiredSectors / 512.0:F3} MiB），"
                + $"实际可写 {writableSectors:N0} 扇区（{writableSectors / 512.0:F3} MiB），"
                + $"超出 {deficit:N0} 扇区（{deficit / 512.0:F3} MiB）。");
        }
    }

    internal static string ValidateWritableDataCapacity(OpticalBurnMediaKind mediaKind,
        int mediaState, long freeSectors)
        => ValidateWritableDataCapacity(mediaKind, mediaState, freeSectors, overwrite: false);

    internal static string ValidateWritableDataCapacity(OpticalBurnMediaKind mediaKind,
        int mediaState, long freeSectors, bool overwrite)
    {
        string mediaName = mediaKind switch
        {
            OpticalBurnMediaKind.CdData => "CD",
            OpticalBurnMediaKind.DvdData => "DVD",
            OpticalBurnMediaKind.BluRayData => "蓝光盘",
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind), "Expected CD, DVD or Blu-ray data."),
        };
        if ((mediaState & UnsupportedMediaStates) != 0)
            throw new InvalidOperationException($"{mediaName} 当前状态不可写入，已取消刻录。");
        if ((mediaState & BlankMediaState) == 0)
        {
            if (!overwrite)
            {
                throw new InvalidOperationException($"{mediaName} 不是空白盘，已取消刻录。");
            }

            // ForceOverwrite only helps on erasable media that still carries a format.
            if ((mediaState & OverwriteOnlyMediaState) == 0)
            {
                throw new InvalidOperationException(
                    $"{mediaName} 不是可覆写的已格式化盘片（只有 DVD±RW 支持覆写），已取消刻录。");
            }
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
                        ImapiCom.ReleaseComObject(item);
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

    // Prefer raw P-W. IMAPI's image creator writes no R-W data, so cooked and raw images
    // are byte-identical; only the subchannel layout announced to the recorder differs.
    // Some recorders list cooked R-W yet reject its first lead-in WRITE: an HP BD-RE BU20N
    // (firmware 0403) returned 5/64/00 for data block type 2, which IMAPI reports as
    // 0xC0AA0301, while a direct SCSI Test Write with data block type 3 (raw P-W) accepted
    // the first 3,000 lead-in sectors at 4x on the same disc. cdrecord also recommends
    // raw96r over raw96p.
    internal static int ResolveCdDaoSectorType(IReadOnlyCollection<int> supportedSectorTypes)
    {
        ArgumentNullException.ThrowIfNull(supportedSectorTypes);
        if (supportedSectorTypes.Contains(CdRawSectorType.SubcodeIsRaw))
            return CdRawSectorType.SubcodeIsRaw;
        if (supportedSectorTypes.Contains(CdRawSectorType.SubcodeIsCooked))
            return CdRawSectorType.SubcodeIsCooked;
        throw new InvalidOperationException(
            "所选光驱不支持 CD DAO 所需的 Raw P-W 或 Cooked R-W 子通道格式"
            + $"（光驱报告：{FormatSectorTypes(supportedSectorTypes)}）。");
    }

    internal static string DescribeCdDaoSectorType(int sectorType, IReadOnlyCollection<int> supportedSectorTypes)
        => (sectorType == CdRawSectorType.SubcodeIsRaw
                ? "CD DAO 子通道格式：Raw P-W（IMAPI 类型 3）"
                : "CD DAO 子通道格式：Cooked R-W（IMAPI 类型 2）")
            + $"；光驱报告支持：{FormatSectorTypes(supportedSectorTypes)}。";

    private static string FormatSectorTypes(IReadOnlyCollection<int> sectorTypes)
        => sectorTypes.Count == 0 ? "无" : string.Join(", ", sectorTypes);
}
