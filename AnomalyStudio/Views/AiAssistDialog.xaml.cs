namespace AnomalyStudio.Views;

public sealed partial class AiAssistDialog : ContentDialog
{
    public AiAssistDialog()
    {
        InitializeComponent();
    }

    public AiAssistViewModel ViewModel { get; } = App.GetService<AiAssistViewModel>();

    /// <summary>テスト抽出はダイアログを閉じずに結果だけ表示する。</summary>
    private void OnTestExtractClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ViewModel.TestExtractCommand.Execute(null);
    }
}
