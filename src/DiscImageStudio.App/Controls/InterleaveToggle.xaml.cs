using DiscImageStudio.Localization;
using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Controls;

/// <summary>The shared CD-DA interleave switch, one control for both sides of the same
/// state: the export workspace toggles generation, the preview's read-back toggles the
/// matching de-interleave. Each site names its own operation through <see cref="Label"/>;
/// the hint stays identical so the correlation is explained the same way everywhere.</summary>
public partial class InterleaveToggle : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(InterleaveToggle),
        new PropertyMetadata(string.Empty, static (sender, _) => ((InterleaveToggle)sender).UpdateLabel()));

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(InterleaveToggle),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(InterleaveToggle),
        new PropertyMetadata(string.Empty,
            static (sender, _) => ((InterleaveToggle)sender).UpdateHint()));

    public InterleaveToggle()
    {
        InitializeComponent();
        UpdateLabel();
        UpdateHint();
        Loaded += (_, _) => L.LanguageChanged += Language_LanguageChanged;
        Unloaded += (_, _) => L.LanguageChanged -= Language_LanguageChanged;
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    private void Language_LanguageChanged(object? sender, EventArgs e)
    {
        UpdateLabel();
        UpdateHint();
    }

    private void UpdateLabel()
        => Toggle.Content = string.IsNullOrWhiteSpace(Label) ? L.T("启用 CD-DA 延迟交织") : Label;

    private void UpdateHint()
        => HintSpot.Hint = string.IsNullOrWhiteSpace(Hint)
            ? L.T("如您在盘片制作时勾选了“启用 CD-DA 延迟交织”，则应勾选该选项。")
            : Hint;
}
