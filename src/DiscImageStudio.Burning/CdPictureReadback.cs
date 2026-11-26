using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DiscImageStudio.Burning;

internal sealed partial class CdMmcCommands
{
    internal byte[] ReadPicture(long lba, int blocks, bool waitForReady, CancellationToken token = default)
    {
        byte[] read = new byte[10];
        read[0] = 0x28;
        BinaryPrimitives.WriteUInt32BigEndian(read.AsSpan(2, 4), checked((uint)lba));
        BinaryPrimitives.WriteUInt16BigEndian(read.AsSpan(7, 2), checked((ushort)blocks));
        int bytes = checked(blocks * CdTaoWriter.DataBlockBytes);
        byte[] data = (waitForReady ? ExecuteWhenReady(read, bytes, 30, token) : Execute(read, bytes, 30)).Data;
        if (data.Length != bytes) throw new IOException($"LBA {lba} 读回 {data.Length} 字节，应为 {bytes}。");
        return data;
    }

    internal void VerifyPicture(Stream image, long start, long sectors, Action<long> report, CancellationToken token)
    {
        const int block = CdTaoWriter.DataBlockBytes;
        int batch = CdTaoWriter.BlocksPerWrite(block, transport.GetWriteTransferLimits());
        byte[] expected = new byte[batch * block];
        image.Position = 0;
        long nextReport = 0;
        for (long verified = 0; verified < sectors;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(batch, sectors - verified);
            int bytes = count * block;
            image.ReadExactly(expected.AsSpan(0, bytes));
            long lba = start + verified;
            byte[] data;
            try { data = ReadPicture(lba, count, waitForReady: true, token); }
            catch (Exception failure) when (failure is (IOException or COMException or TimeoutException)
                && !CdExtraReconnect.IsTransportFailure(failure))
            {
                // Re-read the failed range in single sectors to locate a short or unreadable sector.
                data = new byte[bytes];
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    try { ReadPicture(lba + index, 1, waitForReady: true, token).CopyTo(data, index * block); }
                    catch (Exception single) when (single is IOException or COMException or TimeoutException)
                    { throw new IOException($"图案校验无法读取 LBA {lba + index}：{single.Message}", single); }
                }
            }
            int common = expected.AsSpan(0, bytes).CommonPrefixLength(data);
            if (common != bytes)
                throw new InvalidDataException($"图案校验不一致：LBA {lba + common / block}，扇区内字节 {common % block}。");
            verified += count;
            if (verified >= nextReport || verified == sectors)
            {
                report(verified);
                nextReport = verified + Math.Max(1, sectors / 100);
            }
        }
        token.ThrowIfCancellationRequested();
    }

    // Hash samples read from the recorded audio session on this very drive. Using device
    // readback for both measurements avoids assuming a CD-DA sample offset for the drive.
    internal string AudioFingerprint(IReadOnlyList<long> starts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool hasSignal = false;
        for (int index = 0; index < starts.Count; index++)
        {
            CdTrackInformation track = ReadTrackInformation(index + 1);
            if (track.TrackStartAddress != starts[index] || track.SessionNumber != 1
                || track.TrackSize < CdExtraAudioTrack.MinimumSectors || (track.TrackMode & 4) != 0)
                throw new IOException("无法确定音乐轨指纹的采样位置。");
            for (int point = 1; point <= 3; point++)
            {
                byte[] read = [0xbe, 0x04, 0, 0, 0, 0, 0, 0, 1, 0x10, 0, 0]; // READ CD, CD-DA user data
                long lba = starts[index] + track.TrackSize * point / 4;
                BinaryPrimitives.WriteUInt32BigEndian(read.AsSpan(2, 4), checked((uint)lba));
                byte[] sample = ExecuteWhenReady(read, CdTaoWriter.AudioBlockBytes, 30).Data;
                if (sample.Length != CdTaoWriter.AudioBlockBytes) throw new IOException("音乐轨识别样本读取不完整。");
                hasSignal |= sample.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
                hash.AppendData(sample);
            }
        }
        if (!hasSignal) throw new IOException("音轨样本全为静音，无法识别这张盘片。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
