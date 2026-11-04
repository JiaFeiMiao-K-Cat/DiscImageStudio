using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Services;

internal static class CalibrationSectorPhotoReader
{
    private const int AngleCount = 720;
    private sealed record Strip(double RadiusMm, Vector3[] White, Vector3[] Ink);

    public static CalibrationSectorScaleResult Verify(BitmapSource rectified, double canvasRadiusMm,
        CalibrationTarget target, CalibrationParameters apparent, CancellationToken token)
    {
        var image = rectified.Format == PixelFormats.Bgra32 ? rectified
            : new FormatConvertedBitmap(rectified, PixelFormats.Bgra32, null, 0);
        int size = image.PixelWidth;
        if (size != image.PixelHeight) throw new ArgumentException("扇区校验需要矫正后的正方形照片。");
        if (!target.HasAngularReference || !double.IsFinite(canvasRadiusMm) || canvasRadiusMm <= 0)
            return new(null, 0, 0, 0, 0, 0);
        var pixels = new byte[checked(size * size * 4)];
        image.CopyPixels(pixels, size * 4, 0);
        double centre = (size - 1) / 2.0;
        double mmPerPixel = 2 * canvasRadiusMm / size;
        var direction = Enumerable.Range(0, AngleCount)
            .Select(i => Complex.FromPolarCoordinates(1, Math.Tau * i / AngleCount)).ToArray();
        var map = target.CreateMapping(apparent);
        double PhotoRadius(double source)
        {
            var point = map(new(source, 0));
            return Math.Sqrt(point.X * point.X + point.Y * point.Y);
        }
        // Estimate the local signed RGB response from the known black ring and white
        // gutters. On an iridescent disc even the polarity can reverse with azimuth.
        var strips = target.VisibleRings.Select(ring =>
        {
            token.ThrowIfCancellationRequested();
            double r = ring.RadiusMm, w = ring.WidthMm;
            double black1 = PhotoRadius(r - .15 * w), black2 = PhotoRadius(r), black3 = PhotoRadius(r + .15 * w);
            double white1 = PhotoRadius(r - .60 * w), white2 = PhotoRadius(r + .60 * w);
            var white = new Vector3[AngleCount];
            var ink = new Vector3[AngleCount];
            for (int i = 0; i < AngleCount; i++)
            {
                white[i] = (Read(white1, i) + Read(white2, i)) / 2;
                ink[i] = (Read(black1, i) + Read(black2, i) + Read(black3, i)) / 3 - white[i];
            }
            return new Strip(black2, Smooth(white), Smooth(ink));
        }).ToArray();
        return CalibrationSectorScaleFitter.Verify(target, apparent, mmPerPixel, Moment, token);

        Complex Moment(double radius)
        {
            int i = 0;
            while (i < strips.Length - 2 && radius > strips[i + 1].RadiusMm) i++;
            var a = strips[i];
            var b = strips[i + 1];
            float t = (float)Math.Clamp((radius - a.RadiusMm) / (b.RadiusMm - a.RadiusMm), 0, 1);
            Complex moment = Complex.Zero;
            double support = 0;
            for (int j = 0; j < AngleCount; j++)
            {
                Vector3 ia = a.Ink[j], ib = b.Ink[j];
                float na = ia.Length(), nb = ib.Length();
                double agreement = Vector3.Dot(ia, ib) / (na * nb + .0001);
                double weight = Math.Clamp((Math.Min(na, nb) - 6) / 8, 0, 1)
                    * Math.Clamp((agreement - .25) / .45, 0, 1);
                if (weight == 0) continue;
                var ink = Vector3.Lerp(ia, ib, t);
                var relative = Read(radius, j) - Vector3.Lerp(a.White[j], b.White[j], t);
                double amount = Math.Clamp(Vector3.Dot(relative, ink) / (ink.LengthSquared() + 16), -.4, 1.4);
                // Neutral directions have zero contribution to this first harmonic.
                moment += direction[j] * Math.Clamp(weight * (amount - .5), -.5, .5);
                support += weight;
            }
            return support < AngleCount * .08 ? Complex.Zero : moment / AngleCount;
        }

        Vector3 Read(double radius, int angle)
        {
            double r = radius / mmPerPixel;
            double x = centre + r * direction[angle].Real, y = centre + r * direction[angle].Imaginary;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            if ((uint)x0 >= (uint)(size - 1) || (uint)y0 >= (uint)(size - 1)) return Vector3.Zero;
            float fx = (float)(x - x0), fy = (float)(y - y0);
            int p = (y0 * size + x0) * 4, bottom = p + size * 4;
            return Vector3.Lerp(Vector3.Lerp(Color(p), Color(p + 4), fx),
                Vector3.Lerp(Color(bottom), Color(bottom + 4), fx), fy);
        }
        Vector3 Color(int p) => new(pixels[p + 2], pixels[p + 1], pixels[p]);
    }

    private static Vector3[] Smooth(Vector3[] values)
    {
        // The directions are sampled every 0.5 degrees; smooth the response over σ = 1 degree.
        var output = new Vector3[AngleCount];
        for (int j = -6; j <= 6; j++)
        {
            float w = (float)Math.Exp(-j * j / 8.0);
            for (int i = 0; i < AngleCount; i++) output[i] += w * values[(i + j + AngleCount) % AngleCount];
        }
        float divisor = (float)Enumerable.Range(-6, 13).Sum(j => Math.Exp(-j * j / 8.0));
        for (int i = 0; i < AngleCount; i++) output[i] /= divisor;
        return output;
    }
}
