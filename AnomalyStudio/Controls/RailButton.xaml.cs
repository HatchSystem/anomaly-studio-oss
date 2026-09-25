using System.Windows.Input;

namespace AnomalyStudio.Controls;

public sealed partial class RailButton : UserControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(RailButton), new PropertyMetadata(string.Empty, OnGlyphChanged));

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.Register(nameof(Key), typeof(string), typeof(RailButton), new PropertyMetadata(string.Empty, OnSelectionChanged));

    public static readonly DependencyProperty SelectedKeyProperty =
        DependencyProperty.Register(nameof(SelectedKey), typeof(string), typeof(RailButton), new PropertyMetadata(string.Empty, OnSelectionChanged));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(RailButton), new PropertyMetadata(null));

    public RailButton()
    {
        InitializeComponent();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Key
    {
        get => (string)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public string SelectedKey
    {
        get => (string)GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (RailButton)d;
        button.PART_Icon.Text = (string)e.NewValue;
        button.PART_IconSelected.Text = (string)e.NewValue;
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (RailButton)d;
        var selected = !string.IsNullOrEmpty(button.Key) && button.Key == button.SelectedKey;
        button.PART_Selection.Opacity = selected ? 1 : 0;
        button.PART_Indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        button.PART_Icon.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        button.PART_IconSelected.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClick(object sender, RoutedEventArgs e) => Command?.Execute(Key);
}
