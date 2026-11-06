using System.Windows.Media.Imaging;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private BitmapSource? _targetThumbnail;
    private CancellationTokenSource? _targetImageCancellation;
    private bool _targetPreviewVisible;

    private void InstallTargetPreview(CalibrationTarget target)
    {
        _targetImageCancellation?.Cancel();
        _targetThumbnail = CalibrationPatternRenderer.Render(target, CanvasOuterRadiusMm, 900);
        TargetImage = _targetThumbnail;
        if (_targetPreviewVisible) _ = RenderHighResolutionPreviewAsync(target, CanvasOuterRadiusMm);
    }

    internal void SetTargetPreviewVisible(bool visible)
    {
        if (_targetPreviewVisible == visible) return;
        _targetPreviewVisible = visible;
        if (visible && _target is { } target)
            _ = RenderHighResolutionPreviewAsync(target, CanvasOuterRadiusMm);
        else
        {
            _targetImageCancellation?.Cancel();
            TargetImage = _targetThumbnail;
        }
    }

    private async Task RenderHighResolutionPreviewAsync(CalibrationTarget target, double radius)
    {
        using var cancellation = new CancellationTokenSource();
        _targetImageCancellation = cancellation;
        try
        {
            var image = await Task.Run(() => CalibrationPatternRenderer.Render(target, radius,
                CalibrationPatternRenderer.HighResolutionSize, cancellationToken: cancellation.Token), cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(target, _target))
                TargetImage = image;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Report(ex); }
        finally
        {
            if (ReferenceEquals(_targetImageCancellation, cancellation)) _targetImageCancellation = null;
        }
    }
}
