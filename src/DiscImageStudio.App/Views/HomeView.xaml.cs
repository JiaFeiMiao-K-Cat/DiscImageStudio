using System;
using System.Windows;
using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class HomeView : UserControl
{
    public static readonly DependencyProperty RecentColumnsProperty = DependencyProperty.Register(
        nameof(RecentColumns), typeof(int), typeof(HomeView), new PropertyMetadata(3));

    public int RecentColumns
    {
        get => (int)GetValue(RecentColumnsProperty);
        private set => SetValue(RecentColumnsProperty, value);
    }

    public HomeView(HomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        RecentProjects.SizeChanged += (_, _) => UpdateRecentColumns();
    }

    private void UpdateRecentColumns()
    {
        // Preserve a readable project name and path before adding another column.
        RecentColumns = Math.Clamp((int)(RecentProjects.ActualWidth / 280), 1, 3);
    }
}
