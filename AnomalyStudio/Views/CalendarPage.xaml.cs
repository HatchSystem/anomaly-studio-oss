using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace AnomalyStudio.Views;

public sealed partial class CalendarPage : Page
{
    private bool _isActive;
    private bool _refreshQueued;
    private int _navigationVersion;
    private int _scrollVersion;
    private (int Group, int Row)? _pendingScroll;
    private (int Navigation, int Scroll, int Index)? _scrollCorrection;

    public CalendarPage()
    {
        InitializeComponent();
    }

    public CalendarViewModel ViewModel { get; } = App.GetService<CalendarViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _isActive = true;
        _navigationVersion++;
        _refreshQueued = false;
        ViewModel.Groups.CollectionChanged += OnGroupsChanged;
        ViewModel.ScrollToNextRequested += OnScrollToNextRequested;
        ViewModel.Start();
        QueueDisplayRefresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isActive = false;
        _navigationVersion++;
        _pendingScroll = null;
        CancelScrollCorrection();
        ViewModel.Groups.CollectionChanged -= OnGroupsChanged;
        ViewModel.ScrollToNextRequested -= OnScrollToNextRequested;
        ViewModel.Stop();
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新しい一覧では古い添字への移動を破棄する。移動要求はグループ構築後に届く。
        _scrollVersion++;
        _pendingScroll = null;
        CancelScrollCorrection();
        QueueDisplayRefresh();
    }

    private void OnScrollToNextRequested(object? sender, (int Group, int Row) target)
    {
        _scrollVersion++;
        CancelScrollCorrection();
        _pendingScroll = target;
        QueueDisplayRefresh();
    }

    /// <summary>Clear と日付グループ追加を 1 回にまとめ、見出しと行の表示用リストを更新する。</summary>
    private void QueueDisplayRefresh()
    {
        if (!_isActive || _refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        var version = _navigationVersion;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_isActive || version != _navigationVersion)
            {
                return;
            }

            _refreshQueued = false;
            var items = new List<object>();
            foreach (var group in ViewModel.Groups)
            {
                items.Add(group);
                items.AddRange(group.Rows);
            }

            EventRepeater.ItemsSource = items;
            if (_pendingScroll is { } target)
            {
                _pendingScroll = null;
                EventScroll.UpdateLayout();
                var position = GetScrollPosition(target);
                if (position.Index >= 0)
                {
                    _scrollCorrection = (_navigationVersion, _scrollVersion, position.Index);
                    EventRepeater.LayoutUpdated += OnScrollCorrectionLayoutUpdated;
                }

                EventScroll.ChangeView(null, position.Offset, null, disableAnimation: true);
            }
        }))
        {
            _refreshQueued = false;
        }
    }

    /// <summary>次の指標を一覧の先頭へ。(-1, -1) は日付見出しを含めた一覧の先頭。</summary>
    private (double Offset, int Index) GetScrollPosition((int Group, int Row) target)
    {
        if (target.Group < 0 || target.Group >= ViewModel.Groups.Count
            || target.Row < 0 || target.Row >= ViewModel.Groups[target.Group].Rows.Count)
        {
            return (0, -1);
        }

        // テンプレートと同じ高さを使い、表示範囲外の要素を作らず移動先を求める。
        var headerHeight = (double)Resources["CalendarHeaderHeight"];
        var rowHeight = (double)Application.Current.Resources["AsGridRowHeight"];
        var offset = headerHeight + target.Row * rowHeight;
        var index = 1 + target.Row;
        for (var i = 0; i < target.Group; i++)
        {
            offset += headerHeight + ViewModel.Groups[i].Rows.Count * rowHeight;
            index += 1 + ViewModel.Groups[i].Rows.Count;
        }

        return (offset, index);
    }

    /// <summary>推定位置への移動後、既に表示された行の実位置で 1 回だけ補正する。</summary>
    private void OnScrollCorrectionLayoutUpdated(object? sender, object e)
    {
        var correction = _scrollCorrection;
        CancelScrollCorrection();
        if (correction is not { } request)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_isActive || request.Navigation != _navigationVersion || request.Scroll != _scrollVersion
                || EventRepeater.TryGetElement(request.Index) is not UIElement row
                || EventScroll.Content is not UIElement content)
            {
                return;
            }

            var offset = row.TransformToVisual(content).TransformPoint(new Point(0, 0)).Y;
            EventScroll.ChangeView(null, offset, null, disableAnimation: true);
        });
    }

    private void CancelScrollCorrection()
    {
        EventRepeater.LayoutUpdated -= OnScrollCorrectionLayoutUpdated;
        _scrollCorrection = null;
    }
}
