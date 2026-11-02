using DiscImageStudio.Localization;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Controls;

/// <summary>Per-stage state for the stage bar. The whole list is rebuilt whenever the
/// current workspace changes, so items need no change notification of their own.</summary>
public sealed class StageItem
{
    internal StageItem(Workspace workspace, string label, int index, Workspace current)
    {
        Workspace = workspace;
        Label = label;
        IndexText = index.ToString();
        IsBranch = workspace == Workspace.Calibration;
        IsCurrent = workspace == current;
    }

    public Workspace Workspace { get; }

    public string Label { get; }

    public string IndexText { get; }

    public bool IsBranch { get; }

    public bool IsCurrent { get; }

    public Visibility BranchVisibility => IsBranch ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>The workflow rail under the page header — this shell's answer to a video
/// editor's workspace switcher: the design → preview → export → burn path stays visible in
/// every workspace, with calibration shown as the branch that feeds back into it. Any stage
/// is clickable and reports through StageActivated; the current stage comes from Current.</summary>
public partial class StageBar : UserControl
{
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        nameof(Current), typeof(Workspace), typeof(StageBar),
        new PropertyMetadata(Workspace.Home, static (sender, _) => ((StageBar)sender).RebuildItems()));

    private static readonly (Workspace Workspace, string Label)[] Stages =
    [
        (Workspace.Design, L.T("设计")),
        (Workspace.Preview, L.T("预览")),
        (Workspace.Export, L.T("导出")),
        (Workspace.Burn, L.T("刻录")),
        (Workspace.Calibration, L.T("标定")),
    ];

    /// <summary>Raised when the user clicks a stage; the host wires this to NavigateTo.</summary>
    public event Action<Workspace>? StageActivated;

    public StageBar()
    {
        InitializeComponent();
        RebuildItems();
    }

    public Workspace Current
    {
        get => (Workspace)GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    private void RebuildItems()
    {
        List<StageItem> items = new(Stages.Length);
        for (int index = 0; index < Stages.Length; index++)
        {
            items.Add(new StageItem(Stages[index].Workspace, Stages[index].Label, index + 1, Current));
        }

        StageList.ItemsSource = items;
    }

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StageItem item)
        {
            StageActivated?.Invoke(item.Workspace);
        }
    }
}
