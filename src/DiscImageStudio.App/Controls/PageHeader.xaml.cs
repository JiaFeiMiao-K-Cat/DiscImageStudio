using System;
using System.Windows;
using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Controls;

/// <summary>Shared workspace page header: title, optional family badge, optional hint hotspot,
/// optional subtitle, the workflow stage rail, and a right-aligned action slot used for the
/// "next step" affordance. Keeping one implementation is what stops each workspace from
/// re-inventing its title block (which had drifted to four different font sizes and three
/// left insets).</summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, static (sender, _) => ((PageHeader)sender).UpdateVisuals()));

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, static (sender, _) => ((PageHeader)sender).UpdateVisuals()));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, static (sender, _) => ((PageHeader)sender).UpdateVisuals()));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, static (sender, _) => ((PageHeader)sender).UpdateVisuals()));

    /// <summary>The workspace this header belongs to.</summary>
    public static readonly DependencyProperty WorkspaceProperty = DependencyProperty.Register(
        nameof(Workspace), typeof(Workspace), typeof(PageHeader),
        new PropertyMetadata(Workspace.Home, static (sender, _) => ((PageHeader)sender).UpdateVisuals()));

    public static readonly DependencyProperty ShowStagesProperty = DependencyProperty.Register(
        nameof(ShowStages), typeof(bool), typeof(PageHeader),
        new PropertyMetadata(true, static (sender, _) => ((PageHeader)sender).UpdateVisuals()));

    /// <summary>The workspace the shell currently shows; forwarded to the stage rail.</summary>
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        nameof(Current), typeof(Workspace), typeof(PageHeader),
        new PropertyMetadata(Workspace.Home, static (sender, args) => ((PageHeader)sender).Stages.Current = (Workspace)args.NewValue));

    /// <summary>Right-aligned action slot: typically the "下一步" button that moves the user to
    /// the next workspace.</summary>
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageHeader),
        new PropertyMetadata(null, static (sender, args) => ((PageHeader)sender).ActionHost.Content = args.NewValue));

    public PageHeader()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Short family or state chip next to the title, e.g. "DVD".</summary>
    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    /// <summary>Hint sentence shown from a "?" hotspot next to the title.</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public Workspace Workspace
    {
        get => (Workspace)GetValue(WorkspaceProperty);
        set => SetValue(WorkspaceProperty, value);
    }

    /// <summary>Shows workspace navigation when the page is hosted by an open project.</summary>
    public bool ShowStages
    {
        get => (bool)GetValue(ShowStagesProperty);
        set => SetValue(ShowStagesProperty, value);
    }

    public Workspace Current
    {
        get => (Workspace)GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    /// <summary>A stage was clicked; the window wires this to NavigateTo.</summary>
    public event Action<Workspace>? StageActivated
    {
        add => Stages.StageActivated += value;
        remove => Stages.StageActivated -= value;
    }

    private void UpdateVisuals()
    {
        TitleText.Text = Title;
        BadgeText.Text = Badge;
        BadgeChip.Visibility = string.IsNullOrWhiteSpace(Badge) ? Visibility.Collapsed : Visibility.Visible;
        HintSpot.Hint = Hint;
        HintSpot.Visibility = string.IsNullOrWhiteSpace(Hint) ? Visibility.Collapsed : Visibility.Visible;
        SubtitleText.Text = Subtitle;
        SubtitleText.Visibility =
            string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        Stages.Visibility = ShowStages && Workspace != Workspace.Home
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
