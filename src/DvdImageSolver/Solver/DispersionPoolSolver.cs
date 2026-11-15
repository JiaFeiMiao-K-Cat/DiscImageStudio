using System.Diagnostics;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

internal sealed class DispersionPoolSolver
{
    internal const byte BlackScrambledByte = DvdGrayscalePalette.BlackScrambledByte;
    internal const byte WhiteScrambledByte = DvdGrayscalePalette.WhiteScrambledByte;

    internal SolverResult Solve(
        ImageConstraint constraint,
        ReadOnlySpan<byte> initialPayloads,
        uint initialLba,
        uint psnOffset,
        ModulationBoundary initialBoundary,
        SolverOptions options,
        Action<int, int, int>? progress)
    {
        if (initialPayloads.Length != DvdEccBlockEncoder.PayloadBytesPerBlock)
        {
            throw new ArgumentException(
                $"Initial payload must contain {DvdEccBlockEncoder.PayloadBytesPerBlock} bytes.",
                nameof(initialPayloads));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        uint firstPsn = checked(initialLba + psnOffset);
        if ((firstPsn & 0xF) != 0)
        {
            throw new ArgumentException(
                $"The first PSN 0x{firstPsn:X6} is not aligned to a 16-sector ECC Block.");
        }

        byte[] payloads = initialPayloads.ToArray();
        ReadOnlySpan<sbyte> wordTargets = constraint.ChannelWordTargets;
        ReadOnlySpan<sbyte> grayscaleTargets = constraint.ChannelWordGrayscaleTargets;
        int controlledWords = 0;
        for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
        {
            int payloadOffset = sector * DvdEccBlockEncoder.PayloadBytesPerSector;
            Span<byte> sectorPayload = payloads.AsSpan(
                payloadOffset,
                DvdEccBlockEncoder.PayloadBytesPerSector);

            // Convert the initial raw ISO payload P into the scrambled bytes Q seen by EFMPlus.
            DvdDataFrameBuilder.TransformPayloadScrambling(
                sectorPayload,
                firstPsn + checked((uint)sector));
            for (int byteIndex = 0; byteIndex < sectorPayload.Length; byteIndex++)
            {
                int payloadIndex = payloadOffset + byteIndex;
                int wordIndex = DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex) / 16;
                sbyte target = grayscaleTargets.IsEmpty
                    ? wordTargets[wordIndex]
                    : grayscaleTargets[wordIndex];
                if (target < 0)
                {
                    continue;
                }

                sectorPayload[byteIndex] = grayscaleTargets.IsEmpty
                    ? target < 8 ? BlackScrambledByte : WhiteScrambledByte
                    : DvdGrayscalePalette.GetScrambledByteForLevel(target);
                controlledWords++;
            }

            // Scrambling is XOR-based, so applying it again converts Q back into ISO payload P.
            DvdDataFrameBuilder.TransformPayloadScrambling(
                sectorPayload,
                firstPsn + checked((uint)sector));
        }

        byte[] recordingFrames = DvdEccBlockEncoder.BuildRecordingFrames(payloads, firstPsn);
        ModulationBoundary finalBoundary = DvdEccBlockEncoder.ReplayRecordingFrameBoundary(
            recordingFrames,
            initialBoundary);
        stopwatch.Stop();
        progress?.Invoke(1, 0, 0);
        return new SolverResult(
            payloads,
            new EncodedEccBlock([], finalBoundary, firstPsn),
            Score: 0,
            ScoreMaximum: controlledWords,
            ScoreMetric: "payload-transition-class-error",
            CompletedIterations: 1,
            Elapsed: stopwatch.Elapsed)
        {
            GrayScore = 0,
            GrayScoreMaximum = checked(controlledWords * 16),
            GrayScoreMetric = "payload-transition-count-error",
        };
    }
}
