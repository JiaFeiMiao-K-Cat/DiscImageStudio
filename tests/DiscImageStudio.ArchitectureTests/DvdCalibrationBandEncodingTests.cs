using System.IO;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;

internal static class DvdCalibrationBandEncodingTests
{
    internal static void Run()
    {
        const int sectors = 256, sectorBytes = 2048;
        var original = new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 58, sectors, 133.3);
        var options = new DvdStreamingOptions(sectors, 24, 58);

        // Obtain the established DVD black/white payloads at every PSN, including its scrambling mask.
        using var black = new MemoryStream();
        using var white = new MemoryStream();
        DvdStreamingGenerator.GeneratePattern((_, _) => 0, black, options);
        DvdStreamingGenerator.GeneratePattern((_, _) => 255, white, options);
        foreach (var parameters in new[] { original, original with { InnerRadiusMm = 20, OuterRadiusMm = 38 } })
        {
            var target = CalibrationTarget.Create(parameters, artwork: CalibrationArtwork.WideAddressBands);
            using var actual = new MemoryStream();
            var summary = DvdStreamingGenerator.GeneratePattern(target, actual,
                options with { InnerRadiusMm = parameters.InnerRadiusMm, OuterRadiusMm = parameters.OuterRadiusMm });
            if (summary.BytesWritten != sectors * sectorBytes || actual.Length != sectors * sectorBytes)
                throw new InvalidOperationException("Unexpected DVD calibration payload size.");
            for (int lba = 0; lba < sectors; lba++)
            {
                // ceil(256 * permille / 1000); the transitions fall inside ECC blocks.
                bool dark = lba is >= 9 and < 44 or >= 79 and < 131 or >= 181 and < 245;
                var expected = dark ? black : white;
                if (!actual.GetBuffer().AsSpan(lba * sectorBytes, sectorBytes)
                    .SequenceEqual(expected.GetBuffer().AsSpan(lba * sectorBytes, sectorBytes)))
                    throw new InvalidOperationException($"Wrong DVD calibration texture at sector {lba}.");
            }
        }
    }
}
