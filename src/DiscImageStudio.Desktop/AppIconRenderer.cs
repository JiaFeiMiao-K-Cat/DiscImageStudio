using SkiaSharp;

namespace DiscImageStudio.Desktop;

/// <summary>Original scalable disc mark. The app bundle generates all icon sizes from this master.</summary>
public static class AppIconRenderer
{
    public static void Save(string path)
    {
        using var bitmap = new SKBitmap(1024, 1024, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, IsDither = true };
        paint.Color = new SKColor(33, 52, 104, 35);
        canvas.DrawRoundRect(new SKRect(75, 88, 949, 962), 207, 207, paint);
        using var background = SKShader.CreateLinearGradient(new SKPoint(120, 80), new SKPoint(880, 940),
            [SKColor.Parse("#7697FF"), SKColor.Parse("#4A6EE5"), SKColor.Parse("#314DBD")], [0f, .45f, 1f], SKShaderTileMode.Clamp);
        paint.Shader = background;
        canvas.DrawRoundRect(new SKRect(75, 65, 949, 939), 207, 207, paint);
        paint.Shader = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 2;
        paint.Color = new SKColor(255, 255, 255, 75);
        canvas.DrawRoundRect(new SKRect(78, 68, 946, 936), 205, 205, paint);
        paint.Color = new SKColor(190, 211, 255, 100); paint.StrokeWidth = 5;
        canvas.DrawCircle(512, 502, 342, paint);
        paint.Color = new SKColor(255, 255, 255, 245); paint.StrokeWidth = 17;
        canvas.DrawCircle(512, 502, 268, paint);
        paint.Style = SKPaintStyle.Fill;
        paint.Color = SKColor.Parse("#F8FAFF"); canvas.DrawCircle(512, 502, 79, paint);
        paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 17; paint.StrokeCap = SKStrokeCap.Round;
        paint.Color = SKColor.Parse("#BCD2FF"); canvas.DrawLine(296, 286, 368, 358, paint);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path); encoded.SaveTo(output);
    }
}
