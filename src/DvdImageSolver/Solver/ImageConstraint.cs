using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed class ImageConstraint
{
    internal const sbyte Unconstrained = -1;
    private readonly sbyte[] _targets;
    private readonly sbyte[] _channelWordTargets;
    private readonly sbyte[] _channelWordGrayscaleTargets;
    private readonly int[] _constrainedSectors;
    private readonly int[] _mutablePayloadIndices;
    private readonly bool _hasLockedPayload;

    private ImageConstraint(
        sbyte[] targets,
        int constrainedBits,
        ImageMappingSummary? imageMapping,
        sbyte[]? channelWordTargets = null,
        sbyte[]? channelWordGrayscaleTargets = null,
        bool[]? lockedPayload = null)
    {
        _targets = targets;
        _channelWordTargets = channelWordTargets ?? BuildChannelWordTargets(
            targets,
            binaryTargets: imageMapping is not null);
        if (_channelWordTargets.Length != DvdEccBlockEncoder.ChannelBitsPerBlock / 16)
        {
            throw new ArgumentException(
                "Unexpected channel-word target count.",
                nameof(channelWordTargets));
        }

        // Dispersion uses sampled black/white levels; the full/state-control solvers
        // retain their original binary channel-level and land-count targets.
        _channelWordGrayscaleTargets = channelWordGrayscaleTargets ?? [];
        if (_channelWordGrayscaleTargets.Length != 0
            && (_channelWordGrayscaleTargets.Length != _channelWordTargets.Length
                || _channelWordGrayscaleTargets.Any(value => value is < -1 or > 1)))
        {
            throw new ArgumentException(
                "Grayscale targets must contain one level (-1 or 0..1) per channel word.",
                nameof(channelWordGrayscaleTargets));
        }

        ConstrainedBits = constrainedBits;
        ImageMapping = imageMapping;
        _constrainedSectors = Enumerable.Range(0, DvdEccBlockEncoder.SectorCount)
            .Where(sector => targets.AsSpan(
                    sector * DvdEccBlockEncoder.ChannelBitsPerSector,
                    DvdEccBlockEncoder.ChannelBitsPerSector)
                .IndexOfAnyExcept(Unconstrained) >= 0)
            .ToArray();
        _hasLockedPayload = lockedPayload is not null;
        _mutablePayloadIndices = Enumerable.Range(0, DvdEccBlockEncoder.PayloadBytesPerBlock)
            .Where(index => lockedPayload is null || !lockedPayload[index])
            .ToArray();
    }

    public int ConstrainedBits { get; }

    internal bool HasMutablePayload => _mutablePayloadIndices.Length > 0;

    internal int MutablePayloadCount => _mutablePayloadIndices.Length;

    public ImageMappingSummary? ImageMapping { get; }

    public static ImageConstraint Load(string path, int targetSector)
    {
        string text = File.ReadAllText(path);
        List<sbyte> symbols = [];
        foreach (char character in text)
        {
            switch (character)
            {
                case '0':
                case 'p':
                case 'P':
                    symbols.Add(0); // Pit, black.
                    break;
                case '1':
                case 'l':
                case 'L':
                    symbols.Add(1); // Land, white.
                    break;
                case '?':
                    symbols.Add(Unconstrained);
                    break;
                default:
                    if (!char.IsWhiteSpace(character) && character != '_')
                    {
                        throw new FormatException($"Unsupported target character '{character}'. Use 0/P, 1/L, or ?.");
                    }

                    break;
            }
        }

        sbyte[] targets = new sbyte[DvdEccBlockEncoder.ChannelBitsPerBlock];
        Array.Fill(targets, Unconstrained);
        if (symbols.Count == DvdEccBlockEncoder.ChannelBitsPerBlock)
        {
            symbols.CopyTo(targets);
        }
        else if (symbols.Count == DvdEccBlockEncoder.ChannelBitsPerSector)
        {
            if (targetSector is < 0 or >= DvdEccBlockEncoder.SectorCount)
            {
                throw new ArgumentOutOfRangeException(nameof(targetSector), "Target sector must be 0..15.");
            }

            symbols.CopyTo(targets, targetSector * DvdEccBlockEncoder.ChannelBitsPerSector);
        }
        else
        {
            throw new FormatException(
                $"Target contains {symbols.Count} symbols; expected either "
                + $"{DvdEccBlockEncoder.ChannelBitsPerSector} (one sector) or "
                + $"{DvdEccBlockEncoder.ChannelBitsPerBlock} (one ECC Block).");
        }

        int constrained = targets.Count(value => value != Unconstrained);
        if (constrained == 0)
        {
            throw new FormatException("Target does not constrain any channel level.");
        }

        return new ImageConstraint(targets, constrained, imageMapping: null);
    }

    public static ImageConstraint LoadImage(
        string path,
        uint initialLba,
        ImageMappingOptions options)
        => ImageTargetMapper.Map(path, initialLba, options);

    public void SaveText(string path)
    {
        char[] symbols = new char[_targets.Length];
        for (int index = 0; index < _targets.Length; index++)
        {
            symbols[index] = _targets[index] switch
            {
                0 => 'P',
                1 => 'L',
                _ => '?',
            };
        }

        File.WriteAllText(path, symbols);
    }

    public int Score(ReadOnlySpan<byte> channelLevels)
    {
        if (channelLevels.Length != _targets.Length)
        {
            throw new ArgumentException("Unexpected channel-level count.", nameof(channelLevels));
        }

        int mismatches = 0;
        for (int index = 0; index < _targets.Length; index++)
        {
            sbyte target = _targets[index];
            if (target != Unconstrained && channelLevels[index] != target)
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    internal int SamplePayloadIndex(Random random)
    {
        // Most proposals should touch a sector that is visibly constrained. A smaller
        // global share remains necessary because PO parity couples all 16 sectors.
        if (_constrainedSectors.Length > 0
            && _constrainedSectors.Length != DvdEccBlockEncoder.SectorCount
            && random.NextDouble() < 0.8)
        {
            int sector = _constrainedSectors[random.Next(_constrainedSectors.Length)];
            return sector * DvdEccBlockEncoder.PayloadBytesPerSector
                + random.Next(DvdEccBlockEncoder.PayloadBytesPerSector);
        }

        return _hasLockedPayload
            ? _mutablePayloadIndices[random.Next(_mutablePayloadIndices.Length)]
            : random.Next(DvdEccBlockEncoder.PayloadBytesPerBlock);
    }

    internal IReadOnlyList<int> ConstrainedSectors => _constrainedSectors;

    internal ImageConstraint MaskPayloadSectors(ReadOnlySpan<bool> lockedSectors)
    {
        if (lockedSectors.Length != DvdEccBlockEncoder.SectorCount)
            throw new ArgumentException("Locked sector mask must contain 16 entries.", nameof(lockedSectors));
        sbyte[] targets = (sbyte[])_targets.Clone();
        sbyte[] words = (sbyte[])_channelWordTargets.Clone();
        sbyte[] grayscale = (sbyte[])_channelWordGrayscaleTargets.Clone();
        bool[] lockedPayload = new bool[DvdEccBlockEncoder.PayloadBytesPerBlock];
        for (int sector = 0; sector < lockedSectors.Length; sector++)
        {
            if (!lockedSectors[sector]) continue;
            Array.Fill(targets, Unconstrained,
                sector * DvdEccBlockEncoder.ChannelBitsPerSector,
                DvdEccBlockEncoder.ChannelBitsPerSector);
            Array.Fill(words, Unconstrained,
                sector * (DvdEccBlockEncoder.ChannelBitsPerSector / 16),
                DvdEccBlockEncoder.ChannelBitsPerSector / 16);
            if (grayscale.Length > 0)
            {
                Array.Fill(grayscale, Unconstrained,
                    sector * (DvdEccBlockEncoder.ChannelBitsPerSector / 16),
                    DvdEccBlockEncoder.ChannelBitsPerSector / 16);
            }
            Array.Fill(lockedPayload, true,
                sector * DvdEccBlockEncoder.PayloadBytesPerSector,
                DvdEccBlockEncoder.PayloadBytesPerSector);
        }
        int constrained = targets.Count(value => value != Unconstrained);
        return new ImageConstraint(targets, constrained, ImageMapping, words,
            grayscale.Length == 0 ? null : grayscale, lockedPayload);
    }

    internal static ImageConstraint Create(
        sbyte[] targets,
        ImageMappingSummary imageMapping,
        sbyte[]? channelWordTargets = null,
        sbyte[]? channelWordGrayscaleTargets = null)
    {
        if (targets.Length != DvdEccBlockEncoder.ChannelBitsPerBlock)
        {
            throw new ArgumentException("Unexpected image target size.", nameof(targets));
        }

        int constrained = targets.Count(value => value != Unconstrained);
        return new ImageConstraint(
            targets, constrained, imageMapping, channelWordTargets, channelWordGrayscaleTargets);
    }

    internal sbyte TargetAt(int index) => _targets[index];

    internal ReadOnlySpan<sbyte> ChannelWordTargets => _channelWordTargets;

    internal ReadOnlySpan<sbyte> ChannelWordGrayscaleTargets => _channelWordGrayscaleTargets;

    private static sbyte[] BuildChannelWordTargets(
        ReadOnlySpan<sbyte> channelTargets,
        bool binaryTargets)
    {
        sbyte[] result = new sbyte[DvdEccBlockEncoder.ChannelBitsPerBlock / 16];
        Array.Fill(result, Unconstrained);
        for (int wordIndex = 0; wordIndex < result.Length; wordIndex++)
        {
            int channelOffset = wordIndex * 16;
            int samples = 0;
            int landSamples = 0;
            sbyte tieBreaker = Unconstrained;
            int tieBreakerDistance = int.MaxValue;
            for (int bit = 0; bit < 16; bit++)
            {
                sbyte target = channelTargets[channelOffset + bit];
                if (target < 0)
                {
                    continue;
                }

                samples++;
                landSamples += target;
                int centerDistance = Math.Abs(bit - 8);
                if (centerDistance < tieBreakerDistance)
                {
                    tieBreaker = target;
                    tieBreakerDistance = centerDistance;
                }
            }

            if (samples == 0)
            {
                continue;
            }

            result[wordIndex] = binaryTargets
                ? checked((sbyte)(landSamples * 2 == samples
                    ? tieBreaker * 16
                    : landSamples * 2 > samples ? 16 : 0))
                : checked((sbyte)(((landSamples * 16) + (samples / 2)) / samples));
        }

        return result;
    }
}
