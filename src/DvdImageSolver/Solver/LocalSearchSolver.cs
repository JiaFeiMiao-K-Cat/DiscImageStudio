using System.Diagnostics;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed class LocalSearchSolver
{
    private readonly DvdEccBlockEncoder _encoder = new();

    public SolverResult Solve(
        ImageConstraint constraint,
        ReadOnlySpan<byte> initialPayloads,
        uint initialLba,
        uint psnOffset,
        ModulationBoundary initialBoundary,
        SolverOptions options,
        Action<int, int, int>? progress = null)
    {
        if (initialPayloads.Length != DvdEccBlockEncoder.PayloadBytesPerBlock)
        {
            throw new ArgumentException("Initial payload must contain one complete 16-sector ECC Block.", nameof(initialPayloads));
        }

        if (options.Iterations < 0
            || options.MutationBytes < 1
            || !double.IsFinite(options.InitialTemperature)
            || options.InitialTemperature <= 0
            || !double.IsFinite(options.FinalTemperature)
            || options.FinalTemperature <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Iterations must be non-negative; mutation bytes and temperatures must be positive.");
        }

        Random random = new(options.RandomSeed);
        byte[] currentPayload = initialPayloads.ToArray();
        EncodedEccBlock currentEncoded = _encoder.Encode(currentPayload, initialLba, psnOffset, initialBoundary);
        int currentScore = constraint.Score(currentEncoded.ChannelLevels);
        byte[] bestPayload = (byte[])currentPayload.Clone();
        EncodedEccBlock bestEncoded = currentEncoded;
        int bestScore = currentScore;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int completed = 0;

        if (!constraint.HasMutablePayload)
        {
            stopwatch.Stop();
            return new SolverResult(
                bestPayload, bestEncoded, bestScore, constraint.ConstrainedBits,
                "bit-mismatch", 0, stopwatch.Elapsed);
        }

        int mutationCount = Math.Min(options.MutationBytes, constraint.MutablePayloadCount);
        int[] indexes = new int[mutationCount];
        byte[] previous = new byte[mutationCount];
        HashSet<int> usedIndexes = [];
        for (int iteration = 0; iteration < options.Iterations && bestScore != 0; iteration++)
        {
            usedIndexes.Clear();
            for (int mutation = 0; mutation < mutationCount; mutation++)
            {
                int index;
                do
                {
                    index = constraint.SamplePayloadIndex(random);
                }
                while (!usedIndexes.Add(index));

                indexes[mutation] = index;
                previous[mutation] = currentPayload[index];
                byte replacement;
                do
                {
                    replacement = (byte)random.Next(256);
                }
                while (replacement == previous[mutation]);

                currentPayload[index] = replacement;
            }

            EncodedEccBlock candidateEncoded = _encoder.Encode(currentPayload, initialLba, psnOffset, initialBoundary);
            int candidateScore = constraint.Score(candidateEncoded.ChannelLevels);
            double fraction = options.Iterations <= 1 ? 1.0 : (double)iteration / (options.Iterations - 1);
            double temperature = options.InitialTemperature
                * Math.Pow(options.FinalTemperature / options.InitialTemperature, fraction);
            bool accept = candidateScore <= currentScore
                || random.NextDouble() < Math.Exp((currentScore - candidateScore) / Math.Max(temperature, 1e-9));
            if (accept)
            {
                currentScore = candidateScore;
                currentEncoded = candidateEncoded;
                if (candidateScore < bestScore)
                {
                    bestScore = candidateScore;
                    bestPayload = (byte[])currentPayload.Clone();
                    bestEncoded = candidateEncoded;
                }
            }
            else
            {
                for (int mutation = 0; mutation < mutationCount; mutation++)
                {
                    currentPayload[indexes[mutation]] = previous[mutation];
                }
            }

            completed = iteration + 1;
            progress?.Invoke(completed, currentScore, bestScore);
        }

        stopwatch.Stop();
        return new SolverResult(
            bestPayload,
            bestEncoded,
            bestScore,
            constraint.ConstrainedBits,
            "bit-mismatch",
            completed,
            stopwatch.Elapsed);
    }
}
