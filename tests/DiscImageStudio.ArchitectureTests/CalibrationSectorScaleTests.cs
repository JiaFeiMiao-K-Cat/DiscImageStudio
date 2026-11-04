using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;

internal static class CalibrationSectorScaleTests
{
    internal static void Run()
    {
        // Generate optical cuts of the actual artwork through the encoder's inverse mapping.
        // Only the generator sees the planted physical scale and camera angle.
        var generated = new CalibrationParameters(CalibrationDiscKind.Cd, 24.68744, 57.64439, 359848, 1200);
        var target = CalibrationTarget.Create(generated);
        var truth = generated with { InnerRadiusMm = 24.837775, OuterRadiusMm = 58.020875 };
        const double scale = .9936025, mmPerPixel = .1;
        var measured = generated with { InnerRadiusMm = truth.InnerRadiusMm * scale - .0022,
            OuterRadiusMm = truth.OuterRadiusMm * scale - .0003 };
        var inverse = target.CreateInverseMapping(truth, .68);
        var directions = Enumerable.Range(0, 144).Select(i => Complex.FromPolarCoordinates(1, i * Math.Tau / 144)).ToArray();
        var cuts = new Dictionary<double, Complex>();
        Complex ReadPhoto(double r)
        {
            if (cuts.TryGetValue(r, out var cached)) return cached;
            Complex sum = Complex.Zero;
            double weights = 0;
            for (int j = -14; j <= 14; j++)
            {
                double dr = .018 * j, weight = Math.Exp(-.5 * Math.Pow(dr / .09, 2));
                var src = inverse(new((r + dr) / scale, 0))
                    ?? throw new InvalidOperationException("Synthetic cut is outside the generated band.");
                var point = new Complex(src.X, src.Y);
                foreach (var unit in directions)
                {
                    var rotated = point * unit;
                    sum += weight * unit * (target.Sample(rotated.Real, rotated.Imaginary) / 255.0);
                }
                weights += weight;
            }
            // Smooth angular illumination may exceed the attenuated first harmonic.
            var sample = sum / (weights * directions.Length) + new Complex(.12 + .001 * r, .08 + .0007 * r);
            cuts.Add(r, sample);
            return sample;
        }
        var fit = CalibrationSectorScaleFitter.Verify(target, measured, mmPerPixel, ReadPhoto);
        Check(fit.Corrected is { } p && Math.Abs(fit.ApparentOverPhysical - scale) < .0002
            && Math.Abs(p.InnerRadiusMm - truth.InnerRadiusMm) < .01 && Math.Abs(p.OuterRadiusMm - truth.OuterRadiusMm) < .01,
            $"two swept gaps recover the planted CD scale: {fit}");
        var partial = CalibrationSectorScaleFitter.Verify(target, measured, mmPerPixel,
            r => r < 40 ? ReadPhoto(r) : new Complex(.1 + r * .002, -.04));
        Check(!partial.Succeeded, "a visible inner gap alone cannot verify scale");
        var mirror = CalibrationSectorScaleFitter.Verify(target, measured, mmPerPixel, r => Complex.Conjugate(ReadPhoto(r)));
        Check(!mirror.Succeeded, "a mirrored photograph cannot verify the chirp direction");
        var unrelated = CalibrationSectorScaleFitter.Verify(target, measured, mmPerPixel,
            r => new Complex(.1 * Math.Cos(r * .5), .1 * Math.Sin(r * .8)));
        Check(!unrelated.Succeeded, "smooth reflection alone cannot verify two chirped anchors");
        var legacy = new CalibrationTarget(6, target.Id, generated, target.Options, []);
        Check(!CalibrationSectorScaleFitter.Verify(legacy, measured, mmPerPixel,
            _ => throw new Exception("Should not read any sector on the legacy target.")).Succeeded,
            "the legacy fixed cross-and-rings artwork provides no sector reference");
        var fineOnly = CalibrationTarget.Create(generated, artwork: CalibrationArtwork.FineCross);
        Check(!CalibrationSectorScaleFitter.Verify(fineOnly, measured, mmPerPixel,
            _ => throw new Exception("Should not read sectors on a fine-only target.")).Succeeded,
            "the fine-only version 7 artwork also has no angular reference");
        CheckColorPhoto(target, truth, measured, scale);
        Console.WriteLine("calibration-sector-scale: second-burn CD optical cuts, both independent anchors, mirrored/unrelated/partial and legacy checks passed");
    }

    private static void CheckColorPhoto(CalibrationTarget target, CalibrationParameters truth,
        CalibrationParameters apparent, double scale)
    {
        // A synthetic iridescent photograph: red and green responses have opposite
        // signs, polarity changes with azimuth, and a section has no visible ink.
        // Read it through the actual rectified-photo / local RGB reference sampler.
        const int size = 1100;
        const double canvas = 63, step = 2 * canvas / size;
        var pixels = new byte[size * size * 4];
        var inverse = target.CreateInverseMapping(truth, .68);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double px = (x + .5) * step - canvas, py = (y + .5) * step - canvas;
            double r = Math.Sqrt(px * px + py * py);
            double black = 0;
            if (r >= truth.InnerRadiusMm * scale && r <= truth.OuterRadiusMm * scale)
            {
                double cx = px / r, cy = py / r;
                for (int j = -2; j <= 2; j++)
                {
                    double rr = (r + .055 * j) / scale;
                    var src = inverse(new(rr * cx, rr * cy));
                    if (src is not null && target.Sample(src.X, src.Y) < 128) black += .2;
                }
            }
            double theta = Math.Atan2(py, px);
            double response = Math.Cos(theta) < -.4 ? 0 : black * Math.Sin(2 * theta + .45);
            double background = 130 + 18 * Math.Sin(theta) + .23 * px;
            int at = 4 * (y * size + x);
            pixels[at] = (byte)(background - 12 * response);
            pixels[at + 1] = (byte)(background - 20 * response);
            pixels[at + 2] = (byte)(background + 45 * response);
            pixels[at + 3] = 255;
        }
        var photo = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        photo.Freeze();
        var result = CalibrationSectorPhotoReader.Verify(photo, canvas, target, apparent, CancellationToken.None);
        Check(result.Succeeded && Math.Abs(result.ApparentOverPhysical - scale) < .00025,
            $"local ring/gutter RGB contrast recovers a partial-azimuth color photograph: {result}");
    }

    private static void Check(bool pass, string message)
    {
        if (!pass) throw new InvalidOperationException("Sector scale: " + message);
    }
}
