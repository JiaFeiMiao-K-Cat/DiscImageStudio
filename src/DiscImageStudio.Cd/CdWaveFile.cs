using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DiscImageStudio.Cd;

internal static class CdWaveFile
{
    internal const int HeaderBytes = 44;

    internal static void ValidateLength(long audioBytes)
    {
        if (audioBytes <= 0 || audioBytes > uint.MaxValue - 36L)
        {
            throw new ArgumentOutOfRangeException(nameof(audioBytes), "Audio exceeds the RIFF/WAV size limit.");
        }
    }

    internal static void WriteHeader(Stream output, long audioBytes)
    {
        ValidateLength(audioBytes);
        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(audioBytes + 36)));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 176400);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)audioBytes));
        output.Write(header);
    }

    internal static string WriteCue(string wavePath)
    {
        string cuePath = Path.ChangeExtension(wavePath, ".cue");
        File.WriteAllText(cuePath,
            $"FILE \"{Path.GetFileName(wavePath)}\" WAVE\r\n"
            + "  TRACK 01 AUDIO\r\n"
            + "    INDEX 01 00:00:00\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return cuePath;
    }

    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatExtensible = 0xFFFE;

    // KSDATAFORMAT_SUBTYPE_PCM {00000001-0000-0010-8000-00AA00389B71} in file byte order.
    private static ReadOnlySpan<byte> PcmSubFormat =>
        [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    /// <summary>WAVE_FORMAT_EXTENSIBLE tail after the 16 common bytes: 16 valid bits, a
    /// front-left/front-right (or unspecified) channel mask and the PCM sub-format.</summary>
    private static bool IsExtensiblePcm(BinaryReader reader, uint length)
    {
        if (length < 40 || reader.ReadUInt16() < 22) return false;
        ushort validBits = reader.ReadUInt16();
        uint channelMask = reader.ReadUInt32();
        Span<byte> subFormat = stackalloc byte[16];
        reader.BaseStream.ReadExactly(subFormat);
        return validBits == 16 && channelMask is 0 or 3 && subFormat.SequenceEqual(PcmSubFormat);
    }

    internal static (long Offset, long Length) ReadAudioRange(Stream input)
    {
        using BinaryReader reader = new(input, Encoding.ASCII, leaveOpen: true);
        if (input.Length < 12 || reader.ReadUInt32() != 0x46464952)
        {
            throw new InvalidDataException("Expected a RIFF/WAV audio file.");
        }

        long riffEnd = reader.ReadUInt32() + 8L;
        if (reader.ReadUInt32() != 0x45564157 || riffEnd < 12 || riffEnd > input.Length)
        {
            throw new InvalidDataException("The WAV header or declared length is invalid.");
        }

        bool hasFormat = false;
        (long Offset, long Length)? audio = null;
        while (input.Position < riffEnd)
        {
            if (riffEnd - input.Position < 8)
            {
                throw new InvalidDataException("Truncated WAV chunk header.");
            }

            uint chunk = reader.ReadUInt32();
            uint length = reader.ReadUInt32();
            long start = input.Position;
            long next = start + length + (length & 1);
            if (next > riffEnd)
            {
                throw new InvalidDataException("Truncated WAV chunk data.");
            }

            if (chunk == 0x20746D66) // fmt
            {
                if (hasFormat || length < 16)
                {
                    throw new InvalidDataException("CD WAV audio must be PCM, 44100 Hz, 16-bit stereo.");
                }

                ushort tag = reader.ReadUInt16();
                bool cdLayout = reader.ReadUInt16() == 2
                    && reader.ReadUInt32() == 44100
                    && reader.ReadUInt32() == 176400
                    && reader.ReadUInt16() == 4
                    && reader.ReadUInt16() == 16;
                bool pcm = tag == WaveFormatPcm || tag == WaveFormatExtensible && IsExtensiblePcm(reader, length);
                if (!cdLayout || !pcm)
                {
                    throw new InvalidDataException("CD WAV audio must be PCM, 44100 Hz, 16-bit stereo.");
                }

                hasFormat = true;
            }
            else if (chunk == 0x61746164) // data
            {
                if (audio is not null || length == 0 || length % 4 != 0)
                {
                    throw new InvalidDataException("WAV must contain one data chunk with complete stereo samples.");
                }

                audio = (start, length);
            }

            input.Position = next;
        }

        if (!hasFormat || audio is null)
        {
            throw new InvalidDataException("WAV is missing its PCM format or audio data chunk.");
        }

        return audio.Value;
    }
}
