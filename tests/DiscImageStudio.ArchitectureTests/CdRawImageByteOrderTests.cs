using DiscImageStudio.Burning;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

internal static class CdRawImageByteOrderTests
{
    private const int AudioSectorBytes = 2352;
    private const int RawSectorBytes = AudioSectorBytes + 96;
    private const int AudioSectorCount = 300;

    internal static void Run()
    {
        byte[] audio = CreateAsymmetricAudio();
        byte[] swappedAudio = SwapSampleBytes(audio);
        using GeneratedContentComStream content = new(
            audio.Length,
            (output, _) => output.Write(audio),
            progress: null,
            CancellationToken.None);
        content.WaitUntilPrebuffered(CancellationToken.None);

        // This creates an in-memory image only; no recorder or write interface is used.
        using DynamicCdRawImageSession session = new();
        IStream image = session.CreateImage(content);
        image.Stat(out STATSTG stat, 1);
        Require(session.StartOfLeadout == AudioSectorCount, "audio length matches lead-out");
        Require(stat.cbSize > audio.Length, "image includes RAW framing and lead-in/out");
        Require(stat.cbSize % RawSectorBytes == 0, "RAW image contains complete 2448-byte sectors");

        byte[] rawSector = new byte[RawSectorBytes];
        long rawSectorCount = stat.cbSize / RawSectorBytes;
        long audioStartSector = -1;
        int matchedAudioSectors = 0;
        int firstSectorOccurrences = 0;
        IntPtr bytesRead = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            for (long rawSectorIndex = 0; rawSectorIndex < rawSectorCount; rawSectorIndex++)
            {
                Marshal.WriteInt32(bytesRead, -1);
                image.Read(rawSector, rawSector.Length, bytesRead);
                Require(
                    Marshal.ReadInt32(bytesRead) == RawSectorBytes,
                    $"complete RAW sector read at {rawSectorIndex}");
                ReadOnlySpan<byte> payload = rawSector.AsSpan(0, AudioSectorBytes);
                bool isFirstOriginalSector = payload.SequenceEqual(audio.AsSpan(0, AudioSectorBytes));
                if (isFirstOriginalSector)
                {
                    firstSectorOccurrences++;
                    if (audioStartSector < 0)
                    {
                        audioStartSector = rawSectorIndex;
                    }
                }

                Require(
                    !payload.SequenceEqual(swappedAudio.AsSpan(0, AudioSectorBytes)),
                    "RAW creator preserves the first audio sector instead of byte-swapping it");
                if (audioStartSector < 0 || matchedAudioSectors == AudioSectorCount)
                {
                    continue;
                }

                int audioOffset = matchedAudioSectors * AudioSectorBytes;
                Require(
                    rawSectorIndex == audioStartSector + matchedAudioSectors,
                    "audio sectors are contiguous at the 2448-byte RAW stride");
                Require(
                    payload.SequenceEqual(audio.AsSpan(audioOffset, AudioSectorBytes)),
                    $"audio sector {matchedAudioSectors} retains all 2352 bytes, including both boundaries");
                Require(
                    !payload.SequenceEqual(swappedAudio.AsSpan(audioOffset, AudioSectorBytes)),
                    $"audio sector {matchedAudioSectors} differs from its 16-bit byte-swapped form");
                matchedAudioSectors++;
            }

            image.Read(rawSector, rawSector.Length, bytesRead);
            Require(Marshal.ReadInt32(bytesRead) == 0, "RAW image ends at its declared length");
        }
        finally
        {
            Marshal.FreeCoTaskMem(bytesRead);
        }

        Require(firstSectorOccurrences == 1, "first asymmetric audio sector occurs exactly once");
        Require(matchedAudioSectors == AudioSectorCount, "all 300 audio sectors preserve the supplied byte order");
        Require(audioStartSector > 0, "audio follows the RAW lead-in and pregap");
        Require(audioStartSector + AudioSectorCount < rawSectorCount, "RAW lead-out follows the final audio sector");
        Console.WriteLine("cd-raw-image-byte-order: passed (300 asymmetric sectors preserved)");
    }

    private static byte[] CreateAsymmetricAudio()
    {
        byte[] audio = new byte[AudioSectorCount * AudioSectorBytes];
        new Random(123456789).NextBytes(audio);
        for (int offset = 0; offset < audio.Length; offset += 2)
        {
            // Make every sample asymmetric, including samples beside sector boundaries.
            if (audio[offset] == audio[offset + 1])
            {
                audio[offset + 1] ^= 0xFF;
            }
        }

        return audio;
    }

    private static byte[] SwapSampleBytes(byte[] audio)
    {
        byte[] swapped = new byte[audio.Length];
        for (int offset = 0; offset < audio.Length; offset += 2)
        {
            swapped[offset] = audio[offset + 1];
            swapped[offset + 1] = audio[offset];
        }

        return swapped;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"CD RAW image byte order: {name}.");
        }
    }
}
