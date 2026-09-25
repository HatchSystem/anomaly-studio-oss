namespace AnomalyStudio.Views;

/// <summary>CSV・レポートを保存したあと、「ファイルを開く」「保存先を開く」を尋ねる（表示だけを受け持つ）。</summary>
public static class SavedFileDialog
{
    public static void Attach(Page page, BacktestViewModelBase viewModel) =>
        viewModel.FileSaved += async (_, path) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = page.XamlRoot,
                RequestedTheme = page.ActualTheme,
                Title = "保存しました",
                Content = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                PrimaryButtonText = "ファイルを開く",
                SecondaryButtonText = "保存先を開く",
                CloseButtonText = "閉じる",
                DefaultButton = ContentDialogButton.Primary,
            };

            switch (await dialog.ShowAsync())
            {
                case ContentDialogResult.Primary:
                    FileLauncher.OpenFile(path);
                    break;
                case ContentDialogResult.Secondary:
                    FileLauncher.OpenFolder(path);
                    break;
            }
        };
}
