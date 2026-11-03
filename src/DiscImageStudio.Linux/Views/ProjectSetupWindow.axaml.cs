using Avalonia.Controls;
using DiscImageStudio.Linux.ViewModels;

namespace DiscImageStudio.Linux.Views;

public partial class ProjectSetupWindow : Window
{
    public ProjectSetupWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ProjectSetupViewModel model)
        {
            model.CloseRequested += Close;
        }
    }
}
