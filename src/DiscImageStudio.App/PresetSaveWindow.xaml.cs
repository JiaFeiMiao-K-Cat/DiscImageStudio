using System.Windows;
using System.Windows.Input;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

/// <summary>Asks for a user preset's display name before the parameter set lands in the
/// catalog. The name is required; the geometry itself is described by the card, so there is
/// no free-form note to fill in. This window validates nothing about the geometry — the
/// caller parses it before opening here.</summary>
public partial class PresetSaveWindow : TransitionWindow
{
    public PresetSaveWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowCorners.Apply(this);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                Close();
            }
        };
        NameBox.TextChanged += (_, _) => SaveButton.IsEnabled = PresetName.Length > 0;
        Loaded += (_, _) => NameBox.Focus();
        SaveButton.IsEnabled = false;
    }

    public string PresetName => NameBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Shows the dialog over its owner and returns the confirmed name, or null when
    /// the user cancelled.</summary>
    public static PresetSaveResult? Show(Window owner)
    {
        PresetSaveWindow window = new() { Owner = owner };
        return window.ShowDialog() == true
            ? new PresetSaveResult(window.PresetName)
            : null;
    }
}
