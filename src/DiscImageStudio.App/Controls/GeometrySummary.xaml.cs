using DiscImageStudio.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiscImageStudio.Controls;

/// <summary>Read-out panel: one composed line of values (plus an optional second line) and a
/// button that opens the one place that edits them. Workspaces that only consume a parameter
/// group show this instead of a second editor, which is what kept four copies of r0/r1/sectors
/// in sync by hand. The text is composed by the state (see <c>DiscParametersState</c>) so the
/// same values read identically wherever they appear.</summary>
public partial class GeometrySummary : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(GeometrySummary),
        new PropertyMetadata(string.Empty, static (sender, _) => ((GeometrySummary)sender).UpdateVisuals()));

    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(
        nameof(Details), typeof(string), typeof(GeometrySummary),
        new PropertyMetadata(string.Empty, static (sender, _) => ((GeometrySummary)sender).UpdateVisuals()));

    /// <summary>Optional extra line, e.g. the cubic-pitch formula or a boundary note.</summary>
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(GeometrySummary),
        new PropertyMetadata(string.Empty, static (sender, _) => ((GeometrySummary)sender).UpdateVisuals()));

    public static readonly DependencyProperty EditCommandProperty = DependencyProperty.Register(
        nameof(EditCommand), typeof(ICommand), typeof(GeometrySummary),
        new PropertyMetadata(null, static (sender, _) => ((GeometrySummary)sender).UpdateVisuals()));

    public static readonly DependencyProperty EditCommandParameterProperty = DependencyProperty.Register(
        nameof(EditCommandParameter), typeof(object), typeof(GeometrySummary),
        new PropertyMetadata(null, static (sender, _) => ((GeometrySummary)sender).UpdateVisuals()));

    public static readonly DependencyProperty EditLabelProperty = DependencyProperty.Register(
        nameof(EditLabel), typeof(string), typeof(GeometrySummary),
        new PropertyMetadata(string.Empty, static (sender, _) => ((GeometrySummary)sender).UpdateVisuals()));

    public GeometrySummary()
    {
        InitializeComponent();
        UpdateVisuals();
        Loaded += (_, _) =>
        {
            // The constructor may have run under the previous language while the owning page
            // was detached, so re-read the fallback text before following later switches.
            UpdateVisuals();
            L.LanguageChanged -= Language_LanguageChanged;
            L.LanguageChanged += Language_LanguageChanged;
        };
        Unloaded += (_, _) => L.LanguageChanged -= Language_LanguageChanged;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Details
    {
        get => (string)GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    public string Note
    {
        get => (string)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public ICommand? EditCommand
    {
        get => (ICommand?)GetValue(EditCommandProperty);
        set => SetValue(EditCommandProperty, value);
    }

    /// <summary>Optional argument for commands that need a target (e.g. the shell's workspace
    /// navigation when the summary's owner is another workspace).</summary>
    public object? EditCommandParameter
    {
        get => GetValue(EditCommandParameterProperty);
        set => SetValue(EditCommandParameterProperty, value);
    }

    public string EditLabel
    {
        get => (string)GetValue(EditLabelProperty);
        set => SetValue(EditLabelProperty, value);
    }

    private void Language_LanguageChanged(object? sender, EventArgs e) => UpdateVisuals();

    private void UpdateVisuals()
    {
        TitleText.Text = string.IsNullOrWhiteSpace(Title) ? L.T("盘片几何") : Title;
        DetailsText.Text = Details;
        DetailsText.Visibility = string.IsNullOrWhiteSpace(Details) ? Visibility.Collapsed : Visibility.Visible;
        PitchText.Text = Note;
        PitchText.Visibility = string.IsNullOrWhiteSpace(Note) ? Visibility.Collapsed : Visibility.Visible;
        EditButton.Content = string.IsNullOrWhiteSpace(EditLabel) ? L.T("编辑…") : EditLabel;
        EditButton.Command = EditCommand;
        EditButton.CommandParameter = EditCommandParameter;
        // No command means no jump target (e.g. a summary whose owner is reached by a separate
        // button in the page), so the slot collapses instead of showing a dead button.
        EditButton.Visibility = EditCommand is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
