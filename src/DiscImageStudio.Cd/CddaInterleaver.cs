// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2008-2022 arduinocelentano
// Copyright (c) 2026 Disc Image Studio contributors
// Adapted from CDImage's converter.cpp / converter.h:
// https://github.com/arduinocelentano/cdimage/tree/de5ca19f403b86934cfedb6793ca75f6716080c0/src
// C# port and subsequent stream, byte-order and shared-table changes: 2026.
// Attribution restored 2026-10-25; see Git history and THIRD-PARTY-NOTICES.txt.

using System.IO;

namespace DiscImageStudio.Cd;

internal sealed class CddaInterleaver(CdAudioByteOrder audioByteOrder)
{
    private readonly byte[] _interleaveSequence =
        new byte[CddaInterleaveTable.FramesPerCycle * CddaInterleaveTable.BytesPerFrame];
    private readonly byte[] _sector = new byte[CdDiscParameters.BytesPerSector];
    private int _head = CddaInterleaveTable.FramesPerCycle - 1;
    private int _inputPosition;
    private int _sectorPosition;

    internal void Add(byte value, Stream output)
    {
        _interleaveSequence[MapIndex(CddaInterleaveTable.DelayFor(_inputPosition++))] = value;
        if (_inputPosition < CddaInterleaveTable.BytesPerFrame)
        {
            return;
        }

        CompleteFrame(output);
    }

    internal void Add(ReadOnlySpan<byte> values, Stream output)
    {
        while (!values.IsEmpty)
        {
            int count = Math.Min(values.Length, CddaInterleaveTable.BytesPerFrame - _inputPosition);
            for (int index = 0; index < count; index++)
                _interleaveSequence[MapIndex(CddaInterleaveTable.DelayFor(_inputPosition + index))] = values[index];
            _inputPosition += count;
            values = values[count..];
            if (_inputPosition == CddaInterleaveTable.BytesPerFrame) CompleteFrame(output);
        }
    }

    private void CompleteFrame(Stream output)
    {
        _inputPosition = 0;
        _head++;
        if (_head >= CddaInterleaveTable.FramesPerCycle)
        {
            _head = 0;
        }

        // Both the ring and an audio sector contain whole 24-byte frames. The
        // completed frame is contiguous even when the head wrapped to zero.
        _interleaveSequence.AsSpan(_head * CddaInterleaveTable.BytesPerFrame,
            CddaInterleaveTable.BytesPerFrame).CopyTo(_sector.AsSpan(_sectorPosition));
        _sectorPosition += CddaInterleaveTable.BytesPerFrame;
        if (_sectorPosition == _sector.Length)
        {
            CdAudioSamples.WriteSector(output, _sector, audioByteOrder);
            _sectorPosition = 0;
        }
    }

    internal void Flush(Stream output)
    {
        if (_sectorPosition == 0)
        {
            return;
        }

        Array.Clear(_sector, _sectorPosition, _sector.Length - _sectorPosition);
        CdAudioSamples.WriteSector(output, _sector, audioByteOrder);
        _sectorPosition = 0;
    }

    private int MapIndex(int position)
    {
        int value = (_head * CddaInterleaveTable.BytesPerFrame) + position;
        int modulus = _interleaveSequence.Length;
        if (value >= modulus)
        {
            return value - modulus;
        }

        return value < 0 ? value + modulus : value;
    }
}
