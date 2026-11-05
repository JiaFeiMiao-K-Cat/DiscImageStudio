using System.IO;
using DiscImageStudio.BluRay;
using DiscImageStudio.BluRay.Encoding;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.ViewModels;
using DvdImageSolver.Solver;

internal static class CalibrationAngleSweepEncodingTests
{
    internal static void Run()
    {
        CheckCd();
        CheckDvd();
        CheckBluRay();
        Console.WriteLine("calibration-angle-sweep-encoding: CD byte, DVD sector and BD interleaved payload positions passed");
    }

    private static byte Mark(CalibrationAngleSweepStation station, double progress) =>
        ((long)Math.Floor(station.PhaseAt(progress) / Math.PI) & 1) == 0 ? (byte)0 : (byte)255;

    private static void CheckCd()
    {
        var p = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200);
        var target = CalibrationTarget.Create(p, artwork: CalibrationArtwork.AngleSweepBands);
        long totalBytes = p.Sectors * CdDiscParameters.BytesPerSector;
        var station = target.AngleSweepStations[0];
        int first = checked((int)Math.Ceiling(station.StartProgress * totalBytes));
        int end = first + 120 * CdDiscParameters.BytesPerSector;
        using var output = new MemoryStream(end + (int)(totalBytes / 100));
        using var stop = new CancellationTokenSource();
        try
        {
            CdTrackGenerator.GeneratePatternToStream(target, output, false, value =>
            {
                if (value.CompletedBytes >= end) stop.Cancel();
            }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (output.Length < end) throw new InvalidOperationException("Missing CD angle sample");
        int changes = 0;
        byte previous = 0x0d;
        for (int b = first - 2048; b < end; b++)
        {
            double q = b / (double)totalBytes;
            byte value = q < station.StartProgress || Mark(station, q) == 0 ? (byte)0x0d : (byte)0xaa;
            if (value != output.GetBuffer()[b]) throw new InvalidOperationException($"Wrong CD angle-sweep byte at {b}");
            if (value != previous) changes++;
            previous = value;
        }
        if (changes < 5) throw new InvalidOperationException("CD sample must span multiple sweeps");
    }

    private static void CheckDvd()
    {
        const uint sectors = 32768;
        var p = new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 58, sectors, 133.3);
        var sweep = CalibrationTarget.Create(p, artwork: CalibrationArtwork.AngleSweepBands);
        var bands = CalibrationTarget.Create(p, artwork: CalibrationArtwork.WideAddressBands);
        var options = new DvdStreamingOptions(sectors, 24, 58);
        using var oldOutput = new MemoryStream((int)sectors * 2048);
        using var newOutput = new MemoryStream((int)sectors * 2048);
        DvdStreamingGenerator.GeneratePattern(bands, oldOutput, options);
        DvdStreamingGenerator.GeneratePattern(sweep, newOutput, options);
        byte transition = (byte)(DvdGrayscalePalette.GetScrambledByte(0) ^ DvdGrayscalePalette.GetScrambledByte(255));
        for (int sector = 0; sector < sectors; sector++)
        {
            double q = (sector + .5) / sectors;
            var station = sweep.AngleSweepStations.FirstOrDefault(s => q >= s.StartProgress && q < s.EndProgress);
            // All three windows lie inside the original dark bands. The PSN mask cancels in this XOR.
            byte expected = station is not null && Mark(station, q) == 255 ? transition : (byte)0;
            int at = sector * 2048;
            for (int i = at; i < at + 2048; i++)
                if ((newOutput.GetBuffer()[i] ^ oldOutput.GetBuffer()[i]) != expected)
                    throw new InvalidOperationException($"Wrong DVD angle sweep at sector {sector}");
        }
    }

    private static void CheckBluRay()
    {
        const uint sectors = BluRayDiscParameters.SectorsPerLayer;
        var p = new CalibrationParameters(CalibrationDiscKind.BluRay, 24, 58, sectors, 74.5,
            BluRayInvertMapping: true, DrawingStartSector: 16384, DrawingEndSectorExclusive: sectors - 32768);
        var target = CalibrationTarget.Create(p, artwork: CalibrationArtwork.AngleSweepBands);
        var disc = CalibrationViewModel.BluRayParameters(p);
        var mapping = CalibrationViewModel.BluRayPatternMapping(target);
        var layout = new BluRayImagePayloadMapping(disc, static (_, _) => 0, new BluRayRunLengthMapping());
        var switches = new CalibrationAngleSweepSampler(target).Switches;
        double totalChannels = (long)(sectors / 32) * BluRayDiscParameters.ChannelBitsPerRecordingUnit;
        foreach (var station in target.AngleSweepStations)
        {
            // Choose a transition in the payload portion of a cluster, away from either guard.
            var edge = switches.First(s => s.Progress > (station.StartProgress + station.EndProgress) / 2
                && s.Progress < station.EndProgress && (s.Progress * (sectors / 32) % 1) is > .2 and < .65);
            uint first = (uint)(edge.Progress * (sectors / 32)) * 32;
            using var output = new MemoryStream();
            BluRayStreamingGenerator.GenerateToStream(output, new(disc, first, 32), mapping);
            byte[] mask = BluRayScrambler.CreateMask(disc.GetPhysicalSectorNumber(first), 2048);
            var scalar = new byte[2048];
            bool sawDark = false, sawLight = false;
            for (int s = 0; s < 32; s++)
            {
                uint sector = first + (uint)s;
                mapping.FillScrambledPayload(new(sector, disc.GetPhysicalSectorNumber(sector), disc), scalar);
                for (int b = 0; b < 2048; b++)
                {
                    double q = layout.GetPosition(sector, b).NominalChannelBitPosition / totalChannels;
                    bool dark = Mark(station, q) == 0;
                    byte expected = dark ? BluRayRunLengthMapping.SixTByte : BluRayRunLengthMapping.ThreeTByte;
                    if (scalar[b] != expected || (output.GetBuffer()[s * 2048 + b] ^ mask[b]) != expected)
                        throw new InvalidOperationException($"Wrong BD angle sweep at LBA {sector}, byte {b}");
                    sawDark |= dark;
                    sawLight |= !dark;
                }
            }
            if (!sawDark || !sawLight) throw new InvalidOperationException("BD transition did not divide the cluster");
        }
    }
}
