using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace AnomalyStudio.Views;

public sealed partial class CalendarPage : Page
{
    public CalendarPage()
    {
        InitializeComponent();
    }

    public CalendarViewModel ViewModel { get; } = App.GetService<CalendarViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.ScrollToNextRequested += OnScrollToNextRequested;
        ViewModel.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.ScrollToNextRequested -= OnScrollToNextRequested;
        ViewModel.Stop();
    }

    /// <summary>次の指標の行を一覧の先頭へ（(-1, -1) は一覧の先頭へ）。一覧を作った直後なので、配置が終わってから行う。</summary>
    private void OnScrollToNextRequested(object? sender, (int Group, int Row) target) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            EventScroll.UpdateLayout();
            if (target.Group < 0)
            {
                EventScroll.ChangeView(null, 0, null, disableAnimation: true);
                return;
            }

            if (GroupList.ContainerFromIndex(target.Group) is not DependencyObject group
                || FindDescendant<ItemsControl>(group)?.ContainerFromIndex(target.Row) is not UIElement row
                || EventScroll.Content is not UIElement content)
            {
                return;
            }

            var y = row.TransformToVisual(content).TransformPoint(new Point(0, 0)).Y;
            EventScroll.ChangeView(null, y, null, disableAnimation: true);
        });

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : class, DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
