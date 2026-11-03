using Avalonia.Controls;
using DiscImageStudio.Linux.Services;

namespace DiscImageStudio.Linux.Views;

public partial class MessageWindow : Window
{
    public MessageWindow() : this("Disc Image Studio", string.Empty, "OK", null, null, false)
    {
    }

    public MessageWindow(string title, string message, string primary, string? secondary, string? cancel, bool danger)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        PrimaryButton.Click += (_, _) => Close(DialogChoice.Primary);
        if (danger)
        {
            PrimaryButton.Classes.Remove("accent");
            PrimaryButton.Classes.Add("danger");
        }

        SecondaryButton.IsVisible = secondary is not null;
        SecondaryButton.Content = secondary;
        SecondaryButton.Click += (_, _) => Close(DialogChoice.Secondary);
        CancelButton.IsVisible = cancel is not null;
        CancelButton.Content = cancel;
        CancelButton.Click += (_, _) => Close(DialogChoice.Cancel);
    }
}
