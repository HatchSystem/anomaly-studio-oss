using System.ComponentModel;

namespace AnomalyStudio.Views;

public sealed partial class BacktestRunDialog : ContentDialog
{
    public BacktestRunDialog(BacktestViewModelBase viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public BacktestViewModelBase ViewModel { get; }

    /// <summary>ViewModel が実行中の間、このダイアログをページ上に表示する（閉じられたら実行を中止する）。</summary>
    public static void Attach(Page page, BacktestViewModelBase viewModel)
    {
        BacktestRunDialog? dialog = null;
        viewModel.PropertyChanged += async (object? sender, PropertyChangedEventArgs e) =>
        {
            if (e.PropertyName != nameof(BacktestViewModelBase.IsRunning))
            {
                return;
            }

            if (viewModel.IsRunning && dialog is null)
            {
                dialog = new BacktestRunDialog(viewModel) { XamlRoot = page.XamlRoot, RequestedTheme = page.ActualTheme };
                var result = await dialog.ShowAsync();
                dialog = null;
                if (result == ContentDialogResult.None && viewModel.IsRunning)
                {
                    viewModel.CancelRunCommand.Execute(null);
                }
            }
            else if (!viewModel.IsRunning)
            {
                dialog?.Hide();
            }
        };
    }
}
