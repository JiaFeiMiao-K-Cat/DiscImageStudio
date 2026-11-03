using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Core;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.ViewModels;

/// <summary>One design layer: an image placed relative to the disc centre. Translation is in
/// millimetres (+x right, +y down), rotation in degrees clockwise, and scale a factor on the
/// fitted base size, at which the image's larger side spans the content ring's diameter —
/// the same model, and the same .discproj fields, as the Windows design page.</summary>
public partial class LayerViewModel : ObservableObject
{
    public const int DisplayDecodeSide = 1600;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private double _translateXMm;
    [ObservableProperty] private double _translateYMm;
    [ObservableProperty] private double _rotationDeg;
    [ObservableProperty] private double _scale = 1.0;

    public LayerViewModel(string id, string imagePath)
    {
        Id = id;
        ImagePath = imagePath;
    }

    public string Id { get; }

    public string ImagePath { get; }

    /// <summary>Decoded at a moderate size for the design canvas; the composite decodes the
    /// source again at generation quality.</summary>
    public Bitmap? DisplayBitmap { get; private set; }

    public Bitmap? Thumbnail { get; private set; }

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public double BaseWidthMm { get; private set; }

    public double BaseHeightMm { get; private set; }

    public bool HasImage => DisplayBitmap is not null;

    public bool IsMissing => !HasImage;

    public string PositionText => $"{TranslateXMm:0.0}, {TranslateYMm:0.0} mm";

    public string ShortPath => AppPaths.ShortenHome(ImagePath);

    /// <summary>Scale as a percentage for the property editors.</summary>
    public double ScalePercent
    {
        get => Scale * 100.0;
        set
        {
            if (double.IsFinite(value) && value > 0)
            {
                Scale = value / 100.0;
            }
        }
    }

    partial void OnScaleChanged(double value) => OnPropertyChanged(nameof(ScalePercent));

    partial void OnTranslateXMmChanged(double value) => OnPropertyChanged(nameof(PositionText));

    partial void OnTranslateYMmChanged(double value) => OnPropertyChanged(nameof(PositionText));

    /// <summary>Decodes the image and derives the base size from the content ring. Returns
    /// false when the file is missing or unreadable; the layer is then kept (so the project
    /// round-trips) but drawn as a placeholder.</summary>
    public bool LoadImage(double contentOuterRadiusMm)
    {
        (int Width, int Height)? size = Compositor.ProbeSize(ImagePath);
        if (size is not { } pixels)
        {
            DisplayBitmap = null;
            Thumbnail = null;
            BaseWidthMm = BaseHeightMm = 2 * contentOuterRadiusMm;
            Notify();
            return false;
        }

        PixelWidth = pixels.Width;
        PixelHeight = pixels.Height;
        Refit(contentOuterRadiusMm);

        try
        {
            using (FileStream stream = File.OpenRead(ImagePath))
            {
                DisplayBitmap = pixels.Width >= pixels.Height
                    ? Bitmap.DecodeToWidth(stream, Math.Min(DisplayDecodeSide, pixels.Width), BitmapInterpolationMode.HighQuality)
                    : Bitmap.DecodeToHeight(stream, Math.Min(DisplayDecodeSide, pixels.Height), BitmapInterpolationMode.HighQuality);
            }

            using (FileStream stream = File.OpenRead(ImagePath))
            {
                Thumbnail = Bitmap.DecodeToWidth(stream, 96, BitmapInterpolationMode.MediumQuality);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            DisplayBitmap = null;
            Thumbnail = null;
        }

        Notify();
        return HasImage;
    }

    /// <summary>Fit rule: the larger side spans the content ring's diameter. Re-run when the
    /// project's outer radius changes, which is what reopening the project would do.</summary>
    public void Refit(double contentOuterRadiusMm)
    {
        double diameterMm = 2.0 * contentOuterRadiusMm;
        if (PixelWidth <= 0 || PixelHeight <= 0)
        {
            BaseWidthMm = BaseHeightMm = diameterMm;
        }
        else if (PixelWidth >= PixelHeight)
        {
            BaseWidthMm = diameterMm;
            BaseHeightMm = diameterMm * PixelHeight / PixelWidth;
        }
        else
        {
            BaseHeightMm = diameterMm;
            BaseWidthMm = diameterMm * PixelWidth / PixelHeight;
        }

        OnPropertyChanged(nameof(BaseWidthMm));
        OnPropertyChanged(nameof(BaseHeightMm));
    }

    public LayerRenderItem ToRenderItem()
        => new(ImagePath, BaseWidthMm, BaseHeightMm, TranslateXMm, TranslateYMm, RotationDeg, Scale);

    public ProjectLayer ToDocument()
        => new(Id, string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(ImagePath) : Name.Trim(),
            ImagePath, IsVisible, TranslateXMm, TranslateYMm, RotationDeg, Scale);

    public static LayerViewModel FromDocument(ProjectLayer layer)
        => new(layer.Id, layer.ImagePath)
        {
            Name = layer.Name,
            IsVisible = layer.Visible,
            TranslateXMm = layer.TranslateXMm,
            TranslateYMm = layer.TranslateYMm,
            RotationDeg = layer.RotationDeg,
            Scale = layer.Scale,
        };

    private void Notify()
    {
        OnPropertyChanged(nameof(DisplayBitmap));
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(BaseWidthMm));
        OnPropertyChanged(nameof(BaseHeightMm));
    }
}
