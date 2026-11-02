using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace DiscImageStudio.Controls;

/// <summary>Shared path row: optional label, a two-way path field and a browse button whose
/// command comes from the owner VM. Keeping one implementation stops the text+button row from
/// drifting across views (it used to appear with 76px and default-width buttons).</summary>
public partial class PathField : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(PathField),
        new PropertyMetadata(string.Empty, static (sender, _) => ((PathField)sender).UpdateTexts()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PathField),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ButtonLabelProperty = DependencyProperty.Register(
        nameof(ButtonLabel), typeof(string), typeof(PathField),
        new PropertyMetadata("浏览", static (sender, _) => ((PathField)sender).UpdateTexts()));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(PathField),
        new PropertyMetadata(null, static (sender, args) => ((PathField)sender).BrowseButton.Command = (ICommand?)args.NewValue));

    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter), typeof(object), typeof(PathField),
        new PropertyMetadata(null, static (sender, args) => ((PathField)sender).BrowseButton.CommandParameter = args.NewValue));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(PathField), new PropertyMetadata(false));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(PathField),
        new PropertyMetadata(string.Empty, static (sender, _) => ((PathField)sender).UpdateTexts()));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PathField),
        new PropertyMetadata(string.Empty, static (sender, _) => ((PathField)sender).UpdateTexts()));

    public PathField()
    {
        InitializeComponent();
        PathTextBox.SetBinding(TextBox.TextProperty, new Binding
        {
            Path = new PropertyPath(nameof(Text)),
            Source = this,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        UpdateTexts();
    }

    /// <summary>Optional label above the field.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The path, two-way bound to the owner's state.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string ButtonLabel
    {
        get => (string)GetValue(ButtonLabelProperty);
        set => SetValue(ButtonLabelProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>Inspector density (36px fields) for use inside side panels.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>Optional hint line under the field.</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>Muted watermark shown while the field is empty and unfocused, for fields
    /// whose empty value stands for an effective default (a system folder, for example).</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    private void UpdateTexts()
    {
        LabelText.Text = Label;
        LabelText.Visibility = string.IsNullOrWhiteSpace(Label) ? Visibility.Collapsed : Visibility.Visible;
        BrowseButton.Content = ButtonLabel;
        HintTextBlock.Text = Hint;
        HintTextBlock.Visibility = string.IsNullOrWhiteSpace(Hint) ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderTextBlock.Text = Placeholder;
    }
}
