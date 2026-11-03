using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Automation;
using DiscImageStudio.Localization;

namespace DiscImageStudio.Desktop;

public sealed partial class MainWindow
{
    internal static StudioIcon Glyph(string symbol, double size = 16) => new()
    { Symbol = symbol, Width = size, Height = size, Ink = Surface("#697894", "#B7C5E0"), VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Muted(string value, double size = 12)
    {
        var text = Text(value, size); text.Foreground = Surface("#8590A6", "#98A6C1"); return text;
    }
    private static Button Quiet(Button button) { button.Classes.Add("quiet"); return button; }
    private Button IconButton(string symbol, string title, Action action)
        => IconButton(symbol, title, () => { action(); return Task.CompletedTask; });
    private Button IconButton(string symbol, string title, Func<Task> action)
    {
        var button = Quiet(Button(title, action)); button.Content = Glyph(symbol, 18);
        button.Width = 36; button.Padding = new Thickness(8);
        ToolTip.SetTip(button, L.T(title)); AutomationProperties.SetName(button, L.T(title)); return button;
    }
}
