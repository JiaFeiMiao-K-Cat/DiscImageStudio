using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

internal sealed record MmcCommandResult(int HResult, byte[] Sense, byte[] Data);

internal sealed record MmcWriteTransferLimits(uint ByteAlignmentMask, uint MaximumPageAlignedBytes);

/// <summary>The narrow command boundary also lets tests inspect every CDB without a recorder.</summary>
internal interface IMmcCommandTransport
{
    MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds);

    MmcWriteTransferLimits GetWriteTransferLimits()
        => throw new NotSupportedException("This MMC transport does not support data writes.");

    MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int bufferLength, uint timeoutSeconds)
        => throw new NotSupportedException("This MMC transport does not support data writes.");
}

internal sealed class MmcCommandException : IOException
{
    internal MmcCommandException(byte opcode, MmcCommandResult result)
        : base(Describe(opcode, result))
    {
        HResult = result.HResult;
        (SenseKey, AdditionalSenseCode, AdditionalSenseQualifier) = ReadSense(result.Sense);
    }

    public int SenseKey { get; }
    public int AdditionalSenseCode { get; }
    public int AdditionalSenseQualifier { get; }
    public bool HasValidSense => SenseKey >= 0;
    public bool IsOperationInProgress => SenseKey == 2
        && AdditionalSenseCode == 4 && AdditionalSenseQualifier is 1 or 7 or 8;

    private static string Describe(byte opcode, MmcCommandResult result)
    {
        var (key, asc, ascq) = ReadSense(result.Sense);
        string sense = key >= 0 ? $"Sense {key:X2}/{asc:X2}/{ascq:X2}" : "未返回有效的设备 Sense 数据";
        // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED). This describes the
        // current I/O connection; it does not establish a cable/power cause.
        string connection = result.HResult == unchecked((int)0x8007048f)
            ? "，Windows 报告设备未连接" : string.Empty;
        return $"光驱命令 0x{opcode:X2} 失败（HRESULT 0x{result.HResult:X8}，"
            + sense + connection + "）。";
    }

    private static (int Key, int Asc, int Ascq) ReadSense(byte[] sense)
    {
        if (sense.Length >= 14 && (sense[0] & 0x7f) is 0x70 or 0x71)
            return (sense[2] & 0x0f, sense[12], sense[13]);
        if (sense.Length >= 4 && (sense[0] & 0x7f) is 0x72 or 0x73)
            return (sense[1] & 0x0f, sense[2], sense[3]);
        return (-1, -1, -1);
    }
}

/// <summary>
/// Only reads, removal protection, SYNCHRONIZE CACHE and CLOSE TRACK/SESSION are
/// issued here. In particular, this path never issues FORMAT UNIT or allocates spare areas.
/// </summary>
internal sealed class BluRayMmcCommands(IMmcCommandTransport transport, Action<int, int>? reportRecordedLba = null)
{
    internal const int CommandHasSenseData = 0x00AA0200;

    internal BluRaySequentialMediaInfo ReadMedia()
    {
        byte[] configuration = Read(0x46, 8);
        RequireLength(configuration, 8, "GET CONFIGURATION");
        if (BinaryPrimitives.ReadUInt32BigEndian(configuration.AsSpan(0, 4)) < 4)
            throw new IOException("GET CONFIGURATION 返回了无效的长度字段。");
        int profile = BinaryPrimitives.ReadUInt16BigEndian(configuration.AsSpan(6, 2));

        byte[] disc = Read(0x51, 64);
        RequireResponseLength(disc, 12, "READ DISC INFORMATION");
        bool discBlank = (disc[2] & 3) == 0;
        int lastTrack = disc[6] | disc[11] << 8;
        if (lastTrack == 0)
            throw new IOException("蓝光盘未报告有效的可写轨道编号。");

        var (maximumBlocks, formatStatus, spareClusters) = ReadFormatCapacity();
        byte[] track = ReadTrack(lastTrack);
        int returnedTrack = track[2] | track[32] << 8;

        return new BluRaySequentialMediaInfo(profile, discBlank, formatStatus,
            (track[6] & 0x40) != 0, (track[7] & 1) != 0,
            BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(12, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(24, 4)),
            maximumBlocks, returnedTrack, spareClusters);
    }

    internal (uint Blocks, int Status, int SpareClusters) ReadFormatCapacity()
    {
        byte[] capacities = Read(0x23, 256);
        RequireLength(capacities, 12, "READ FORMAT CAPACITIES");
        if (capacities[3] < 8 || capacities[3] % 8 != 0 || capacities.Length < 4 + capacities[3])
            throw new IOException("蓝光格式容量描述符长度无效。");
        uint maximumBlocks = BinaryPrimitives.ReadUInt32BigEndian(capacities.AsSpan(4, 4));
        int formatStatus = capacities[8] & 3;
        // For BD-R, bytes 9..11 describe spare-area clusters, not the logical
        // block size used by generic MMC format-capacity descriptors.

        int spareClusters = (capacities[9] << 16) | (capacities[10] << 8) | capacities[11];
        return (maximumBlocks, formatStatus, spareClusters);
    }

    internal void PreventRemoval(bool prevent)
        => Execute([0x1e, 0, 0, 0, prevent ? (byte)1 : (byte)0, 0], 0, 30);

    internal void SynchronizeCache()
    {
        Execute([0x35, 0x02, 0, 0, 0, 0, 0, 0, 0, 0], 0, 120);
        WaitUntilReady();
    }

    internal void CloseTrack(int trackNumber)
    {
        if (trackNumber is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(trackNumber));
        // A full track can close automatically; filling the final track may
        // finalize the entire disc before we send either CLOSE command.
        if (ReadDiscStatus() == 2 || IsTrackClosed(ReadTrack(trackNumber))) return;
        try { Execute([0x5b, 1, 1, 0, (byte)(trackNumber >> 8), (byte)trackNumber, 0, 0, 0, 0], 0, 120); }
        catch (MmcCommandException)
        {
            if (IsDiscFinalizedAfterFailure() || IsTrackClosedAfterFailure(trackNumber)) return;
            throw;
        }
        WaitUntilReady();
        WaitUntilTrackClosed(trackNumber);
    }

    internal void CloseDisc()
    {
        // MMC-5 6.3.3.9.4: BD-R SRM function 110b finalizes the disc;
        // function 010b only closes a session and does not promise finalization.
        if (ReadDiscStatus() == 2) return;
        try { Execute([0x5b, 1, 6, 0, 0, 0, 0, 0, 0, 0], 0, 120); }
        catch (MmcCommandException)
        {
            if (IsDiscFinalizedAfterFailure()) return;
            throw;
        }
        WaitUntilReady();
        WaitUntilDiscFinalized();
    }

    private int ReadDiscStatus()
    {
        byte[] disc = Read(0x51, 64);
        RequireResponseLength(disc, 12, "READ DISC INFORMATION");
        return disc[2] & 3;
    }

    private byte[] ReadTrack(int trackNumber)
    {
        byte[] cdb = CreateReadCdb(0x52, 48);
        cdb[1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), checked((uint)trackNumber));
        byte[] track = Execute(cdb, 48, 30).Data;
        RequireResponseLength(track, 36, "READ TRACK INFORMATION");
        if ((track[2] | track[32] << 8) != trackNumber)
            throw new IOException("蓝光盘返回了不同的轨道编号，已停止刻录操作。");
        if ((track[5] & 0x20) != 0)
            throw new IOException("蓝光轨道已标记为损坏，已停止刻录操作。");
        // Reuse an existing READ TRACK INFORMATION response. LRA_V is bit 1;
        // never infer physical recording from NWA or acknowledged WRITE data.
        // No additional device command is issued for diagnostics, even on failure.
        if ((track[7] & 2) != 0)
        {
            uint start = BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(8, 4));
            uint lastRecorded = BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(28, 4));
            if (start <= lastRecorded && lastRecorded <= int.MaxValue)
            {
                try { reportRecordedLba?.Invoke((int)start, (int)lastRecorded); }
                catch { /* Telemetry must not change the result of a device command. */ }
            }
        }
        return track;
    }

    private static bool IsTrackClosed(byte[] track)
        => (track[6] & 0x40) == 0 && (track[7] & 1) == 0
            && BinaryPrimitives.ReadUInt32BigEndian(track.AsSpan(16, 4)) == 0;

    private bool IsDiscFinalizedAfterFailure()
    {
        try { return ReadDiscStatus() == 2; }
        catch { return false; } // Preserve the original CLOSE failure.
    }

    private bool IsTrackClosedAfterFailure(int trackNumber)
    {
        try { return IsTrackClosed(ReadTrack(trackNumber)); }
        catch { return false; }
    }

    private void WaitUntilDiscFinalized()
    {
        WaitUntilCompleted(() =>
        {
            int status = ReadDiscStatus();
            if (status == 2) return true;
            if (status != 1)
                throw new IOException("蓝光封盘后未返回有效的已完成盘片状态。");
            return false;
        }, "等待蓝光盘确认封盘完成超时。");
    }

    private void WaitUntilTrackClosed(int trackNumber)
    {
        WaitUntilCompleted(() =>
        {
            int status = ReadDiscStatus();
            if (status == 2) return true;
            if (status != 1)
                throw new IOException("蓝光闭轨后未返回有效的已写入盘片状态。");
            return IsTrackClosed(ReadTrack(trackNumber));
        }, "等待蓝光盘确认闭轨完成超时。");
    }

    private void WaitUntilCompleted(Func<bool> isComplete, string timeoutMessage)
    {
        // A successful TEST UNIT READY is not sufficient: some drives still
        // return an open state or temporary NOT READY to the following query.
        // Both cases share one deadline; neither causes another CLOSE command.
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (isComplete()) return;
            }
            catch (MmcCommandException exception) when (exception.IsOperationInProgress)
            {
                // READ DISC/TRACK INFORMATION may report 02/04/01,07,08 even
                // immediately after TEST UNIT READY reported GOOD.
            }
            if (elapsed.Elapsed >= TimeSpan.FromMinutes(10))
                throw new TimeoutException(timeoutMessage);
            Thread.Sleep(200);
            TimeSpan remaining = TimeSpan.FromMinutes(10) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException(timeoutMessage);
            WaitUntilReady(remaining);
        }
    }

    internal void WaitUntilReady(TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromMinutes(10);
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Execute([0, 0, 0, 0, 0, 0], 0, 30);
                return;
            }
            catch (MmcCommandException exception) when (exception.IsOperationInProgress)
            {
                if (elapsed.Elapsed >= limit)
                    throw new TimeoutException("等待蓝光盘完成写入收尾超时。", exception);
                Thread.Sleep((int)Math.Min(200, Math.Max(1, (limit - elapsed.Elapsed).TotalMilliseconds)));
            }
        }
    }

    private byte[] Read(byte opcode, ushort allocationLength)
        => Execute(CreateReadCdb(opcode, allocationLength), allocationLength, 30).Data;

    private static byte[] CreateReadCdb(byte opcode, ushort allocationLength)
    {
        byte[] cdb = new byte[10];
        cdb[0] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), allocationLength);
        return cdb;
    }

    private MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
    {
        MmcCommandResult result = transport.Execute(cdb, allocationLength, timeoutSeconds);
        // S_IMAPI_COMMAND_HAS_SENSE_DATA is a positive HRESULT but means that
        // the device failed the command. Never treat it as ordinary success.
        if (result.HResult < 0 || result.HResult == CommandHasSenseData)
            throw new MmcCommandException(cdb[0], result);
        return result;
    }

    private static void RequireLength(byte[] bytes, int minimum, string command)
    {
        if (bytes.Length < minimum)
            throw new IOException($"{command} 返回的数据被截断（{bytes.Length} 字节）。");
    }

    private static void RequireResponseLength(byte[] bytes, int minimum, string command)
    {
        RequireLength(bytes, minimum, command);
        int declared = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2)) + 2;
        if (declared < minimum || bytes.Length < Math.Min(declared, command == "READ TRACK INFORMATION" ? 48 : 64))
            throw new IOException($"{command} 返回了无效的长度字段。");
    }
}

internal sealed class ImapiMmcCommandTransport(object recorder) : IMmcCommandTransport
{
    private readonly IDiscRecorder2ExCommands _recorder = (IDiscRecorder2ExCommands)recorder;
    private MmcWriteTransferLimits? _transferLimits;

    public MmcWriteTransferLimits GetWriteTransferLimits()
    {
        if (_transferLimits is not null) return _transferLimits;
        Marshal.ThrowExceptionForHR(_recorder.GetByteAlignmentMask(out uint alignment));
        Marshal.ThrowExceptionForHR(_recorder.GetMaximumPageAlignedTransferSize(out uint maximum));
        return _transferLimits = new(alignment, maximum);
    }

    public MmcCommandResult SendData(byte[] cdb, IntPtr buffer, int bufferLength, uint timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(cdb);
        if (cdb.Length is < 6 or > 16 || buffer == IntPtr.Zero || bufferLength <= 0 || timeoutSeconds == 0)
            throw new ArgumentOutOfRangeException(nameof(cdb));
        byte[] sense = new byte[18];
        // The caller owns a stable page/adapter-aligned unmanaged buffer. LPArray
        // marshalling would not guarantee the recorder's data-address alignment.
        int result = _recorder.SendCommandSendDataToDevice(cdb, (uint)cdb.Length, sense,
            timeoutSeconds, buffer, checked((uint)bufferLength));
        return new(result, sense, []);
    }

    public MmcCommandResult Execute(byte[] cdb, int allocationLength, uint timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(cdb);
        if (cdb.Length is < 6 or > 16 || allocationLength < 0)
            throw new ArgumentOutOfRangeException(nameof(cdb));
        byte[] sense = new byte[18];
        byte[] data = new byte[allocationLength];
        int result;
        if (allocationLength == 0)
            result = _recorder.SendCommandNoData(cdb, (uint)cdb.Length, sense, timeoutSeconds);
        else
        {
            // Read-command payloads have the same adapter alignment contract as
            // writes; ordinary managed arrays do not guarantee that alignment.
            using AlignedMmcWriteBuffer buffer = new(allocationLength, GetWriteTransferLimits());
            result = _recorder.SendCommandGetDataFromDevice(cdb, (uint)cdb.Length, sense,
                timeoutSeconds, buffer.Pointer, (uint)data.Length, out uint fetched);
            if (fetched > data.Length) throw new IOException("IMAPI 返回了超过接收缓冲区的长度。");
            Array.Resize(ref data, (int)fetched);
            if (fetched > 0) Marshal.Copy(buffer.Pointer, data, 0, (int)fetched);
        }
        return new(result, sense, data);
    }
}

// Full IDiscRecorder2Ex vtable, in Windows SDK imapi2.idl order. All entries before
// the transfer-limit queries are required even when this application does not
// invoke them. Buffers returned by the unused allocating methods are IntPtr.
[ComImport, Guid("27354132-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDiscRecorder2ExCommands
{
    [PreserveSig]
    int SendCommandNoData([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] cdb,
        uint cdbSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 18)] byte[] sense, uint timeout);

    [PreserveSig]
    int SendCommandSendDataToDevice([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] cdb,
        uint cdbSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 18)] byte[] sense, uint timeout,
        IntPtr buffer, uint bufferSize);

    [PreserveSig]
    int SendCommandGetDataFromDevice([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] cdb,
        uint cdbSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 18)] byte[] sense, uint timeout,
        IntPtr buffer, uint bufferSize, out uint fetched);

    [PreserveSig]
    int ReadDvdStructure(uint format, uint address, uint layer, uint agid, out IntPtr data, out uint count);

    [PreserveSig]
    int SendDvdStructure(uint format, IntPtr data, uint count);

    [PreserveSig]
    int GetAdapterDescriptor(out IntPtr data, out uint byteSize);

    [PreserveSig]
    int GetDeviceDescriptor(out IntPtr data, out uint byteSize);

    [PreserveSig]
    int GetDiscInformation(out IntPtr data, out uint byteSize);

    [PreserveSig]
    int GetTrackInformation(uint address, int addressType, out IntPtr data, out uint byteSize);

    [PreserveSig]
    int GetFeaturePage(int requestedFeature, [MarshalAs(UnmanagedType.U1)] bool currentFeatureOnly,
        out IntPtr data, out uint byteSize);

    [PreserveSig]
    int GetModePage(int requestedModePage, int requestType, out IntPtr data, out uint byteSize);

    [PreserveSig]
    int SetModePage(int requestType, IntPtr data, uint byteSize);

    [PreserveSig]
    int GetSupportedFeaturePages([MarshalAs(UnmanagedType.U1)] bool currentFeatureOnly,
        out IntPtr data, out uint byteSize);

    [PreserveSig]
    int GetSupportedProfiles([MarshalAs(UnmanagedType.U1)] bool currentOnly,
        out IntPtr data, out uint validProfiles);

    [PreserveSig]
    int GetSupportedModePages(int requestType, out IntPtr data, out uint validPages);

    [PreserveSig]
    int GetByteAlignmentMask(out uint value);

    [PreserveSig]
    int GetMaximumNonPageAlignedTransferSize(out uint value);

    [PreserveSig]
    int GetMaximumPageAlignedTransferSize(out uint value);
}
