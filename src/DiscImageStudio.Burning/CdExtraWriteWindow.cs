using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

// Inclusive bounds for the drive's next writable address after an ambiguous command.
// A cache flush or final close can only be retried once the entire picture is accounted for.
internal sealed record CdExtraWriteWindow(byte Opcode, long FirstLba, long LimitLba)
{
    internal const int UnexpectedResponse = unchecked((int)0xc0aa0301);

    internal static CdExtraWriteWindow? FromCommand(Exception error, CdExtraCheckpoint job)
    {
        // COM cleanup can wrap the original operation. Never attribute a failed recovery
        // probe inside a CdTaoWriteException to the WRITE that preceded that probe.
        while (error is InvalidOperationException { InnerException: { } inner }) error = inner;
        if (error is CdTaoWriteException { BlockBytes: CdTaoWriter.DataBlockBytes, InnerException: { } cause } write
            && cause.InnerException is null && cause is IOException or COMException
            && cause is not MmcCommandException { Opcode: not 0x2a } && CanInspect(cause))
            return new(0x2a, write.Lba, checked(write.Lba + write.Blocks));
        if (error is MmcCommandException { Opcode: 0x35 or 0x5b } control && CanInspect(control))
            return new(control.Opcode, job.StartLba + job.Sectors, job.StartLba + job.Sectors);
        return null;
    }

    private static bool CanInspect(Exception error)
    {
        if (error.HResult != UnexpectedResponse) return false;
        if (error is not MmcCommandException sense) return true;
        return !sense.IsDeferredSense && (!sense.HasValidSense || sense is { SenseKey: 0, AdditionalSenseCode: 0, AdditionalSenseQualifier: 0 }
            || sense.SenseKey is 1 or 0x0b || sense.IsOperationInProgress || sense is { SenseKey: 6, AdditionalSenseCode: 0x29 });
    }

    internal bool Fits(CdExtraCheckpoint job) => Opcode switch
    {
        0x2a => FirstLba >= job.StartLba && LimitLba > FirstLba && LimitLba <= job.StartLba + job.Sectors
            && LimitLba - FirstLba <= CdTaoWriter.DataBlocksPerWrite,
        0x35 or 0x5b => FirstLba == job.StartLba + job.Sectors && LimitLba == FirstLba,
        _ => false
    };

    internal void CheckPosition(long nextLba)
    {
        if (nextLba < FirstLba || nextLba > LimitLba)
            throw new IOException($"光驱报告的下一位置 LBA {nextLba} 超出 0xC0AA0301 命令 0x{Opcode:X2} 允许的范围 {FirstLba}–{LimitLba}，已停止续写。");
    }
}
