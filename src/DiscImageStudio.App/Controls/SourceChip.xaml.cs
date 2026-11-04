using DiscImageStudio.Localization;
using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Controls;

/// <summary>Read-only source strip naming what the current workspace consumes. <see cref="Actions"/>
/// is an optional trailing slot for the link to whoever owns the described setting, so a
/// "summary + jump to owner" row keeps one chrome instead of each page building its own.</summary>
public partial class SourceChip : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SourceChip),
        new PropertyMetadata(string.Empty, static (sender, _) => ((SourceChip)sender).UpdateText()));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(SourceChip),
        new PropertyMetadata(null, static (sender, _) => ((SourceChip)sender).UpdateActions()));

    public SourceChip()
    {
        InitializeComponent();
        UpdateText();
        UpdateActions();
        // The constructor may have run under the previous language and the control receives no
        // language event while its page is detached, so re-read the text when it loads.
        Loaded += (_, _) =>
        {
            UpdateText();
            L.LanguageChanged -= Language_LanguageChanged;
            L.LanguageChanged += Language_LanguageChanged;
        };
        Unloaded += (_, _) => L.LanguageChanged -= Language_LanguageChanged;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Trailing action slot; empty collapses the column.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    private void Language_LanguageChanged(object? sender, EventArgs e) => UpdateText();

    private void UpdateText()
        => ChipText.Text = string.IsNullOrWhiteSpace(Text) ? L.T("来自设计页合成图") : Text;

    private void UpdateActions()
    {
        ActionHost.Content = Actions;
        ActionHost.Visibility = Actions is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
