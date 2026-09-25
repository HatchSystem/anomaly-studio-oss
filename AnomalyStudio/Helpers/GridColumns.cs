using System.Globalization;

namespace AnomalyStudio.Helpers;

/// <summary>
/// データグリッドのヘッダーと行で同じ列定義を共有するための添付プロパティ。
/// 書式: カンマ区切りで「幅[:最小幅]」。幅は数値（px）、<c>n*</c>（比率）、<c>Auto</c>。
/// 例: <c>76, 2*:150, 1*:80</c> … 76px 固定、残りを 2:1 で分け最小 150px / 80px。
/// </summary>
public static class GridColumns
{
    public static readonly DependencyProperty LayoutProperty =
        DependencyProperty.RegisterAttached("Layout", typeof(string), typeof(GridColumns), new PropertyMetadata(null, OnLayoutChanged));

    public static string? GetLayout(Grid grid) => (string?)grid.GetValue(LayoutProperty);

    public static void SetLayout(Grid grid, string? value) => grid.SetValue(LayoutProperty, value);

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid)
        {
            return;
        }

        grid.ColumnDefinitions.Clear();
        foreach (var column in Parse(e.NewValue as string))
        {
            grid.ColumnDefinitions.Add(column);
        }
    }

    private static IEnumerable<ColumnDefinition> Parse(string? layout)
    {
        if (string.IsNullOrWhiteSpace(layout))
        {
            yield break;
        }

        foreach (var token in layout.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            var column = new ColumnDefinition { Width = ParseWidth(parts[0]) };
            if (parts.Length > 1)
            {
                column.MinWidth = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }

            yield return column;
        }
    }

    private static GridLength ParseWidth(string value)
    {
        if (value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return GridLength.Auto;
        }

        if (value.EndsWith('*'))
        {
            var weight = value.Length == 1 ? 1 : double.Parse(value[..^1], CultureInfo.InvariantCulture);
            return new GridLength(weight, GridUnitType.Star);
        }

        return new GridLength(double.Parse(value, CultureInfo.InvariantCulture));
    }
}
