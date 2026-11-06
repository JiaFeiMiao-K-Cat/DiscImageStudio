using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Desktop;

public sealed partial class MainWindow
{
    internal async Task CaptureSnapshots(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string image = Session.CachePath("snapshot-source.png");
            var pixels = new byte[256 * 256 * 4];
            for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
            {
                int i = (y * 256 + x) * 4;
                byte gray = (byte)(((x - 128) * (x - 128) + (y - 128) * (y - 128) < 75 * 75) ? 30 : 235);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = gray; pixels[i + 3] = 255;
            }
            RasterCodec.SavePng(image, 256, 256, pixels, false);
            foreach (string page in new[] { "主页", "设计", "预览", "导出", "刻录", "标定" })
            {
                if (page == "设计")
                {
                    Session.Create("cd", "macOS desktop validation", new(24.5, 56.8, 359849));
                    Session.Update(Session.Document! with { Layers = [new ProjectLayer("fixture", "Validation image", image, true, 0, 0, 0, 1)] });
                    _selectedLayer = 0;
                }
                Navigate(page);
                await Task.Delay(250);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                bitmap.Render(this);
                bitmap.Save(Path.Combine(directory, $"{Array.IndexOf(new[] { "主页", "设计", "预览", "导出", "刻录", "标定" }, page)}.png"));
            }
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(directory, "error.txt"), e.ToString()); Environment.ExitCode = 1; }
        finally { _closingApproved = true; Close(); }
    }
}
