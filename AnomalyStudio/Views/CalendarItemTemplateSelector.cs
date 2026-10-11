namespace AnomalyStudio.Views;

/// <summary>仮想化されたカレンダーの見出しとイベント行に、元の表示テンプレートを使う。</summary>
public sealed class CalendarItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate HeaderTemplate { get; set; } = null!;

    public DataTemplate RowTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) =>
        item is CalendarGroupViewModel ? HeaderTemplate : RowTemplate;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
