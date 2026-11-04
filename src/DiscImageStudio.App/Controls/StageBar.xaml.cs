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
    internal StageItem(Workspace workspace, string label, bool isGroupStart, Workspace current)
    {
        Workspace = workspace;
        Label = label;
        IsGroupStart = isGroupStart;
        IsCurrent = workspace == current
            || workspace == Workspace.Calibration && current == Workspace.CalibrationBurn;
    }

    public Workspace Workspace { get; }

    public string Label { get; }

    /// <summary>True for the first stage of the calibration group: the rail draws the group
    /// divider ahead of it.</summary>
    public bool IsGroupStart { get; }

    public bool IsCurrent { get; }

    public Visibility GroupDividerVisibility => IsGroupStart ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>The workflow rail under the page header — this shell's answer to a video
/// editor's workspace switcher. The two groups mirror the sidebar: disc-making (design →
/// export → burn) first, then the calibration pair (preview → calibration). Any stage is
/// clickable and reports through StageActivated; the current stage comes from Current.</summary>
public partial class StageBar : UserControl
{
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        nameof(Current), typeof(Workspace), typeof(StageBar),
        new PropertyMetadata(Workspace.Home, static (sender, _) => ((StageBar)sender).RebuildItems()));

    private static readonly (Workspace Workspace, string Key)[] Stages =
    [
        (Workspace.Design, "设计"),
        (Workspace.Export, "导出"),
        (Workspace.Burn, "刻录"),
        (Workspace.Preview, "预览"),
        (Workspace.Calibration, "标定"),
    ];

    /// <summary>The stage that opens the second group; the rail draws its divider ahead of it.</summary>
    private const Workspace CalibrationGroupStart = Workspace.Preview;

    /// <summary>Raised when the user clicks a stage; the host wires this to NavigateTo.</summary>
    public event Action<Workspace>? StageActivated;

    public StageBar()
    {
        InitializeComponent();
        RebuildItems();
        Loaded += (_, _) =>
        {
            // Stage labels are built with the constructor's language; a switch while this
            // control is detached would otherwise survive until the next workspace change.
            RebuildItems();
            L.LanguageChanged -= Language_LanguageChanged;
            L.LanguageChanged += Language_LanguageChanged;
        };
        Unloaded += (_, _) => L.LanguageChanged -= Language_LanguageChanged;
    }

    public Workspace Current
    {
        get => (Workspace)GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    private void Language_LanguageChanged(object? sender, EventArgs e) => RebuildItems();

    private void RebuildItems()
    {
        List<StageItem> items = new(Stages.Length);
        for (int index = 0; index < Stages.Length; index++)
        {
            (Workspace workspace, string key) = Stages[index];
            items.Add(new StageItem(workspace, L.T(key), workspace == CalibrationGroupStart, Current));
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
