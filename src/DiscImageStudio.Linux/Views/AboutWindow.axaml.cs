using Avalonia.Controls;
using DiscImageStudio.Linux.ViewModels;

namespace DiscImageStudio.Linux.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {MainViewModel.Version} · Linux · .NET {Environment.Version}";
        LicenseText.Text = ReadResource("DiscImageStudio.Linux.Licenses.GPL");
        NoticesText.Text = ReadResource("DiscImageStudio.Linux.Licenses.ThirdParty");
        CloseButton.Click += (_, _) => Close();
    }

    private static string ReadResource(string name)
    {
        using Stream? stream = typeof(AboutWindow).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return string.Empty;
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
