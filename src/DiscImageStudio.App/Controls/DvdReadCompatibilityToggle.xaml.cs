using System.Windows;
using System.Windows.Controls;
using DiscImageStudio.Core;

namespace DiscImageStudio.Controls;

/// <summary>One generation option shared by export, streaming burn and calibration setup.</summary>
public partial class DvdReadCompatibilityToggle : UserControl
{
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(DvdReadCompatibilityToggle),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(DvdReadCompatibilityToggle), new PropertyMetadata(true));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(DvdReadCompatibilityToggle),
        new FrameworkPropertyMetadata(DvdReadCompatibilityModes.Current, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public DvdReadCompatibilityToggle() => InitializeComponent();

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }
}
