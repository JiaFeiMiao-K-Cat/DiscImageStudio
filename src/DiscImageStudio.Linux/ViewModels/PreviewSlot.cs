using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

/// <summary>One live preview image: debounces requests, cancels a render that a newer one
/// supersedes, loads the PNG the engine wrote and recycles the previous file.</summary>
public partial class PreviewSlot : ObservableObject
{
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cancellation;
    private Func<CancellationToken, Task<PreviewInfo?>>? _pending;
    private int _generation;
    private string? _currentPath;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private PreviewInfo? _info;
    [ObservableProperty] private bool _isRendering;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _hasError;

    public PreviewSlot(TimeSpan debounce)
    {
        _debounce = new DispatcherTimer { Interval = debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_pending is { } render)
            {
                _pending = null;
                _ = RunAsync(render);
            }
        };
    }

    public bool HasImage => Image is not null;

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));

    /// <summary>Queues a render after the debounce interval; later calls replace it.</summary>
    public void Schedule(Func<CancellationToken, Task<PreviewInfo?>> render)
    {
        _pending = render;
        _cancellation?.Cancel();
        _debounce.Stop();
        _debounce.Start();
    }

    public Task RenderNowAsync(Func<CancellationToken, Task<PreviewInfo?>> render)
    {
        _pending = null;
        _debounce.Stop();
        return RunAsync(render);
    }

    public void Clear(string status)
    {
        _pending = null;
        _debounce.Stop();
        _cancellation?.Cancel();
        _generation++;
        Image = null;
        Info = null;
        IsRendering = false;
        HasError = false;
        Status = status;
        AppPaths.TryDelete(_currentPath);
        _currentPath = null;
    }

    private async Task RunAsync(Func<CancellationToken, Task<PreviewInfo?>> render)
    {
        _cancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _cancellation = cancellation;
        int generation = ++_generation;
        IsRendering = true;
        HasError = false;
        Status = "Rendering…";
        try
        {
            PreviewInfo? info = await render(cancellation.Token);
            if (generation != _generation)
            {
                if (info is not null)
                {
                    AppPaths.TryDelete(info.Path);
                }

                return;
            }

            if (info is null)
            {
                Image = null;
                Info = null;
                Status = "Import an image on the Design page to see a preview.";
                return;
            }

            Bitmap bitmap = await Task.Run(() =>
            {
                using FileStream stream = File.OpenRead(info.Path);
                return new Bitmap(stream);
            }, cancellation.Token);
            if (generation != _generation)
            {
                AppPaths.TryDelete(info.Path);
                return;
            }

            string? previous = _currentPath;
            _currentPath = info.Path;
            Image = bitmap;
            Info = info;
            Status = "Up to date";
            if (previous != info.Path)
            {
                AppPaths.TryDelete(previous);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (generation == _generation)
            {
                HasError = true;
                Status = Translator.T(exception.Message);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsRendering = false;
            }
        }
    }
}
