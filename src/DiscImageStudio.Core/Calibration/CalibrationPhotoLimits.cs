namespace DiscImageStudio.Core.Calibration;

/// <summary>Shared bounds for decoded photos, rectification buffers and portable sessions.</summary>
public static class CalibrationPhotoLimits
{
    public const int MaxDimension = 8192;
    // A BGRA working buffer stays within 128 MiB, including for panoramic photos.
    public const int MaxPixelCount = 32 * 1024 * 1024;
    // Allow an incompressible 32-bit PNG plus Base64 and JSON escaping overhead.
    public const int MaxEncodedPhotoLength = 192 * 1024 * 1024;
    public const int MaxSessionLength = 256 * 1024 * 1024;
    public static int MaxRectifiedDimension => (int)Math.Sqrt(MaxPixelCount);

    public static bool Contains(int width, int height) => width > 0 && height > 0
        && width <= MaxDimension && height <= MaxDimension && (long)width * height <= MaxPixelCount;
}
