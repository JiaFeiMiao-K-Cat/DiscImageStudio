using System.Runtime.InteropServices;
using SkiaSharp;

namespace DiscImageStudio.Linux.Services;

internal static class PngWriter
{
    /// <summary>Writes a straight-alpha BGRA32 buffer as PNG.</summary>
    public static void WriteBgra(string path, int width, int height, byte[] pixels)
    {
        SKImageInfo info = new(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using SKPixmap pixmap = new(info, handle.AddrOfPinnedObject(), width * 4);
            using SKData? data = pixmap.Encode(SKPngEncoderOptions.Default)
                ?? throw new InvalidOperationException("PNG encoding failed.");
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using FileStream stream = new(full, FileMode.Create, FileAccess.Write, FileShare.None);
            data.SaveTo(stream);
        }
        finally
        {
            handle.Free();
        }
    }
}
