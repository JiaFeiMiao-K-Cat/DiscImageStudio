using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DiscImageStudio.Linux.Services;
using DiscImageStudio.Linux.ViewModels;
using DiscImageStudio.Linux.Views;

namespace DiscImageStudio.Linux;

/// <summary>Developer and documentation aid (<c>--screenshot DIR</c>): renders each page and
/// the new-project dialog to PNG once its previews have finished, then exits. Runs under any
/// X server, including Xvfb in CI.</summary>
internal static class ScreenshotRunner
{
    public static async Task<int> RunAsync(MainWindow window, MainViewModel model, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            model.NavigateCommand.Execute(AppPage.Home);
            await Settle(model);
            Capture(window, Path.Combine(directory, "home.png"));
            if (model.Project is not null)
            {
                foreach (AppPage page in new[] { AppPage.Design, AppPage.Preview, AppPage.Export, AppPage.Burn, AppPage.Calibrate })
                {
                    model.NavigateCommand.Execute(page);
                    await Settle(model);
                    Capture(window, Path.Combine(directory, $"{page.ToString().ToLowerInvariant()}.png"));
                }
            }

            ProjectSetupViewModel setup = new(isEditMode: false, AppPaths.DefaultProjectsDirectory);
            ProjectSetupWindow dialog = new() { DataContext = setup };
            dialog.Show(window);
            await Task.Delay(600);
            Capture(dialog, Path.Combine(directory, "new-project.png"));
            dialog.Close();
            Console.WriteLine($"Screenshots written to {Path.GetFullPath(directory)}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: screenshot failed: {exception}");
            return 1;
        }
    }

    private static async Task Settle(MainViewModel model)
    {
        await Task.Delay(900);
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline
               && (model.Design.TonePreview.IsRendering || model.Preview.Slot.IsRendering || model.IsBusy))
        {
            await Task.Delay(200);
        }

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(300);
    }

    private static void Capture(TopLevel topLevel, string path)
    {
        PixelSize size = new((int)topLevel.ClientSize.Width, (int)topLevel.ClientSize.Height);
        using RenderTargetBitmap bitmap = new(size, new Vector(96, 96));
        bitmap.Render(topLevel);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"  {path}");
    }
}
