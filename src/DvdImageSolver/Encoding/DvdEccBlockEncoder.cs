namespace DvdImageSolver.Encoding;

public sealed class DvdEccBlockEncoder
{
    public const int SectorCount = 16;
    public const int PayloadBytesPerSector = 2048;
    public const int PayloadBytesPerBlock = SectorCount * PayloadBytesPerSector;
    public const int ChannelBitsPerSector = 38_688;
    public const int ChannelBitsPerBlock = SectorCount * ChannelBitsPerSector;
    internal const int SyncFramesPerSector = 26;
    internal const int SyncFrameCount = SectorCount * SyncFramesPerSector;

    private const int EccRows = 208;
    private const int DataRows = 192;
    private const int DataColumns = 172;
    private const int EccColumns = 182;
    private static readonly int[] FirstSyncCategories = [0, 1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4];
    private static readonly int[] SecondSyncCategories = [5, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7];

    public EncodedEccBlock Encode(
        ReadOnlySpan<byte> payloads,
        uint initialLba,
        uint psnOffset,
        ModulationBoundary initialBoundary)
    {
        if (payloads.Length != PayloadBytesPerBlock)
        {
            throw new ArgumentException($"An ECC Block needs exactly {PayloadBytesPerBlock} payload bytes.", nameof(payloads));
        }

        uint firstPsn = checked(initialLba + psnOffset);
        if ((firstPsn & 0xF) != 0)
        {
            throw new ArgumentException($"The first PSN 0x{firstPsn:X6} is not aligned to a 16-sector ECC Block.");
        }

        if (firstPsn > 0x00FF_FFF0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialLba), "The ECC Block exceeds the 24-bit PSN range.");
        }

        byte[] recordingFrames = BuildRecordingFrames(payloads, firstPsn);
        return EncodeRecordingFrames(recordingFrames, firstPsn, initialBoundary);
    }

    internal EncodedEccBlock EncodeRecordingFrames(
        ReadOnlySpan<byte> recordingFrames,
        uint firstPsn,
        ModulationBoundary initialBoundary)
    {
        if (recordingFrames.Length != EccRows * EccColumns)
        {
            throw new ArgumentException("Unexpected recording-frame byte count.", nameof(recordingFrames));
        }

        byte[] channelLevels = new byte[ChannelBitsPerBlock];
        EfmPlusEncoder modulator = new();
        ModulationBoundary boundary = ModulationBoundary.Validate(initialBoundary.State, initialBoundary.Dsv, initialBoundary.IsLand);
        int outputOffset = 0;
        for (int sector = 0; sector < SectorCount; sector++)
        {
            int frameOffset = sector * 13 * EccColumns;
            for (int row = 0; row < 13; row++)
            {
                ReadOnlySpan<byte> rowBytes = recordingFrames.Slice(frameOffset + row * EccColumns, EccColumns);
                boundary = modulator.EncodeSyncFrame(
                    rowBytes[..91],
                    FirstSyncCategories[row],
                    boundary,
                    channelLevels.AsSpan(outputOffset, EfmPlusEncoder.BitsPerSyncFrame));
                outputOffset += EfmPlusEncoder.BitsPerSyncFrame;
                boundary = modulator.EncodeSyncFrame(
                    rowBytes[91..],
                    SecondSyncCategories[row],
                    boundary,
                    channelLevels.AsSpan(outputOffset, EfmPlusEncoder.BitsPerSyncFrame));
                outputOffset += EfmPlusEncoder.BitsPerSyncFrame;
            }
        }

        return new EncodedEccBlock(channelLevels, boundary, firstPsn);
    }

    internal static ModulationBoundary ReplayRecordingFrameBoundary(
        ReadOnlySpan<byte> recordingFrames,
        ModulationBoundary initialBoundary)
    {
        if (recordingFrames.Length != EccRows * EccColumns)
        {
            throw new ArgumentException(
                "Unexpected recording-frame byte count.",
                nameof(recordingFrames));
        }

        sbyte[] unconstrained = new sbyte[EfmPlusEncoder.BytesPerSyncFrame];
        Array.Fill(unconstrained, (sbyte)-1);
        EfmPlusWordScorer scorer = new();
        ModulationBoundary boundary = ModulationBoundary.Validate(
            initialBoundary.State,
            initialBoundary.Dsv,
            initialBoundary.IsLand);
        for (int syncFrame = 0; syncFrame < SyncFrameCount; syncFrame++)
        {
            int offset = syncFrame * EfmPlusEncoder.BytesPerSyncFrame;
            boundary = scorer.ScoreSyncFrame(
                recordingFrames.Slice(offset, EfmPlusEncoder.BytesPerSyncFrame),
                SyncCategory(syncFrame),
                boundary,
                unconstrained).Boundary;
        }

        return boundary;
    }

    internal static ModulationBoundary ReplayPayloadBoundary(
        ReadOnlySpan<byte> payloads,
        uint firstPsn,
        ModulationBoundary initialBoundary)
    {
        if (payloads.Length != PayloadBytesPerBlock)
            throw new ArgumentException($"An ECC Block needs exactly {PayloadBytesPerBlock} payload bytes.", nameof(payloads));
        if ((firstPsn & 0xF) != 0)
            throw new ArgumentException("The first PSN is not aligned to a 16-sector ECC Block.", nameof(firstPsn));
        return ReplayRecordingFrameBoundary(BuildRecordingFrames(payloads, firstPsn), initialBoundary);
    }

    internal static byte[] BuildRecordingFrames(ReadOnlySpan<byte> payloads, uint firstPsn)
        => InterleaveRecordingFrames(BuildEccBytes(payloads, firstPsn));

    internal static byte[] UpdateRecordingFrames(
        ReadOnlySpan<byte> currentPayloads,
        ReadOnlySpan<byte> updatedPayloads,
        ReadOnlySpan<byte> currentRecordingFrames,
        uint firstPsn)
    {
        if (currentPayloads.Length != PayloadBytesPerBlock
            || updatedPayloads.Length != PayloadBytesPerBlock)
        {
            throw new ArgumentException($"An ECC Block needs exactly {PayloadBytesPerBlock} payload bytes.");
        }

        if (currentRecordingFrames.Length != EccRows * EccColumns)
        {
            throw new ArgumentException(
                "Unexpected recording-frame byte count.",
                nameof(currentRecordingFrames));
        }

        byte[] frames = currentRecordingFrames.ToArray();
        bool[] changedColumns = new bool[DataColumns];
        bool[] changedRows = new bool[EccRows];
        for (int sector = 0; sector < SectorCount; sector++)
        {
            int payloadOffset = sector * PayloadBytesPerSector;
            ReadOnlySpan<byte> updatedSector = updatedPayloads.Slice(
                payloadOffset,
                PayloadBytesPerSector);
            if (updatedSector.SequenceEqual(currentPayloads.Slice(
                    payloadOffset,
                    PayloadBytesPerSector)))
            {
                continue;
            }

            byte[] dataFrame = DvdDataFrameBuilder.BuildScrambledFrame(
                updatedSector,
                firstPsn + (uint)sector);
            for (int row = 0; row < 12; row++)
            {
                int eccRow = (sector * 12) + row;
                int recordingOffset = RecordingFrameIndex(eccRow, 0);
                ReadOnlySpan<byte> source = dataFrame.AsSpan(row * DataColumns, DataColumns);
                Span<byte> destination = frames.AsSpan(recordingOffset, DataColumns);
                bool rowChanged = false;
                for (int column = 0; column < DataColumns; column++)
                {
                    if (source[column] != destination[column])
                    {
                        changedColumns[column] = true;
                        rowChanged = true;
                    }
                }

                if (rowChanged)
                {
                    source.CopyTo(destination);
                    changedRows[eccRow] = true;
                }
            }
        }

        byte[] columnBytes = new byte[DataRows];
        byte[] poParity = new byte[16];
        for (int column = 0; column < DataColumns; column++)
        {
            if (!changedColumns[column])
            {
                continue;
            }

            for (int row = 0; row < DataRows; row++)
            {
                columnBytes[row] = frames[RecordingFrameIndex(row, column)];
            }

            ReedSolomon.AppendPo(columnBytes, poParity);
            for (int parityRow = 0; parityRow < poParity.Length; parityRow++)
            {
                int eccRow = DataRows + parityRow;
                int recordingIndex = RecordingFrameIndex(eccRow, column);
                if (frames[recordingIndex] != poParity[parityRow])
                {
                    frames[recordingIndex] = poParity[parityRow];
                    changedRows[eccRow] = true;
                }
            }
        }

        for (int row = 0; row < EccRows; row++)
        {
            if (!changedRows[row])
            {
                continue;
            }

            int recordingOffset = RecordingFrameIndex(row, 0);
            ReedSolomon.AppendPi(
                frames.AsSpan(recordingOffset, DataColumns),
                frames.AsSpan(recordingOffset + DataColumns, 10));
        }

        return frames;
    }

    internal static byte[] BuildEccBytes(ReadOnlySpan<byte> payloads, uint firstPsn)
    {
        byte[] ecc = new byte[EccRows * EccColumns];
        for (int sector = 0; sector < SectorCount; sector++)
        {
            byte[] frame = DvdDataFrameBuilder.BuildScrambledFrame(
                payloads.Slice(sector * PayloadBytesPerSector, PayloadBytesPerSector),
                firstPsn + (uint)sector);
            for (int row = 0; row < 12; row++)
            {
                frame.AsSpan(row * DataColumns, DataColumns)
                    .CopyTo(ecc.AsSpan((sector * 12 + row) * EccColumns, DataColumns));
            }
        }

        byte[] column = new byte[DataRows];
        byte[] parity = new byte[16];
        for (int columnIndex = 0; columnIndex < DataColumns; columnIndex++)
        {
            for (int row = 0; row < DataRows; row++)
            {
                column[row] = ecc[row * EccColumns + columnIndex];
            }

            ReedSolomon.AppendPo(column, parity);
            for (int row = 0; row < parity.Length; row++)
            {
                ecc[(DataRows + row) * EccColumns + columnIndex] = parity[row];
            }
        }

        for (int row = 0; row < EccRows; row++)
        {
            ReedSolomon.AppendPi(
                ecc.AsSpan(row * EccColumns, DataColumns),
                ecc.AsSpan(row * EccColumns + DataColumns, 10));
        }

        return ecc;
    }

    internal static byte[] InterleaveRecordingFrames(ReadOnlySpan<byte> ecc)
    {
        if (ecc.Length != EccRows * EccColumns)
        {
            throw new ArgumentException("Unexpected ECC Block size.", nameof(ecc));
        }

        byte[] frames = new byte[ecc.Length];
        for (int sourceRow = 0; sourceRow < DataRows; sourceRow++)
        {
            int destinationRow = sourceRow + sourceRow / 12;
            ecc.Slice(sourceRow * EccColumns, EccColumns)
                .CopyTo(frames.AsSpan(destinationRow * EccColumns, EccColumns));
        }

        for (int sourceRow = DataRows; sourceRow < EccRows; sourceRow++)
        {
            int destinationRow = 13 * (sourceRow - 191) - 1;
            ecc.Slice(sourceRow * EccColumns, EccColumns)
                .CopyTo(frames.AsSpan(destinationRow * EccColumns, EccColumns));
        }

        return frames;
    }

    internal static int RecordingFrameIndex(int eccRow, int eccColumn)
    {
        if ((uint)eccRow >= EccRows || (uint)eccColumn >= EccColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(eccRow));
        }

        int recordingRow = eccRow < DataRows
            ? eccRow + (eccRow / 12)
            : 13 * (eccRow - 191) - 1;
        return (recordingRow * EccColumns) + eccColumn;
    }

    internal static int PayloadDirectChannelOffset(int payloadIndex)
    {
        if ((uint)payloadIndex >= PayloadBytesPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadIndex));
        }

        int recordingIndex = PayloadRecordingFrameIndex(payloadIndex);
        int syncFrame = recordingIndex / EfmPlusEncoder.BytesPerSyncFrame;
        int byteInSyncFrame = recordingIndex % EfmPlusEncoder.BytesPerSyncFrame;
        return (syncFrame * EfmPlusEncoder.BitsPerSyncFrame) + 32 + (byteInSyncFrame * 16);
    }

    internal static int PayloadRecordingFrameIndex(int payloadIndex)
    {
        if ((uint)payloadIndex >= PayloadBytesPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadIndex));
        }

        int sector = payloadIndex / PayloadBytesPerSector;
        int frameIndex = 12 + (payloadIndex % PayloadBytesPerSector);
        int eccRow = (sector * 12) + (frameIndex / DataColumns);
        int eccColumn = frameIndex % DataColumns;
        return RecordingFrameIndex(eccRow, eccColumn);
    }

    internal static int SyncCategory(int syncFrameIndex)
    {
        if ((uint)syncFrameIndex >= SyncFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(syncFrameIndex));
        }

        int withinSector = syncFrameIndex % SyncFramesPerSector;
        int row = withinSector / 2;
        return (withinSector & 1) == 0
            ? FirstSyncCategories[row]
            : SecondSyncCategories[row];
    }
}

public sealed record EncodedEccBlock(
    byte[] ChannelLevels,
    ModulationBoundary FinalBoundary,
    uint FirstPhysicalSectorNumber);
