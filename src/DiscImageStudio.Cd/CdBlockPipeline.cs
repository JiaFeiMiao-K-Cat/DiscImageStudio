namespace DiscImageStudio.Cd;

/// <summary>Bounded immutable-sample workers followed by ordered, caller-thread consumption.</summary>
internal static class CdBlockPipeline
{
    internal const int SectorsPerBlock = 32;

    internal static void Run(long sectorCount, int bytesPerSector,
        Action<long, byte[], int> fillBlock, Action<long, byte[], int> consumeBlock,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sectorCount == 0) return;
        int workers = Math.Min(16, Math.Max(1, Environment.ProcessorCount - 1));
        long blockCount = 1 + (sectorCount - 1) / SectorsPerBlock;
        int capacity = (int)Math.Min(blockCount, workers * 2L);
        int bufferLength = checked((int)Math.Min(sectorCount, SectorsPerBlock) * bytesPerSector);
        byte[][] buffers = new byte[capacity][];
        for (int i = 0; i < buffers.Length; i++) buffers[i] = new byte[bufferLength];
        ParallelOptions options = new() { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken };
        for (long firstSector = 0; firstSector < sectorCount;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long batchStart = firstSector;
            int count = (int)Math.Min(capacity, 1 + (sectorCount - firstSector - 1) / SectorsPerBlock);
            void Fill(int index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long start = batchStart + (long)index * SectorsPerBlock;
                fillBlock(start, buffers[index], (int)Math.Min(SectorsPerBlock, sectorCount - start));
            }
            if (workers == 1 || count == 1)
                for (int index = 0; index < count; index++) Fill(index);
            else Parallel.For(0, count, options, Fill);

            // Workers have joined before output can block or fail. A slow sink
            // cannot queue more blocks, and each slot is reused only after consumption.
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long start = batchStart + (long)index * SectorsPerBlock;
                consumeBlock(start, buffers[index], (int)Math.Min(SectorsPerBlock, sectorCount - start));
            }
            firstSector += (long)count * SectorsPerBlock;
        }
    }
}
