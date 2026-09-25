using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AnomalyStudio.Views;

public sealed partial class ShellPage : Page
{
    public ShellPage()
    {
        InitializeComponent();
        var navigation = App.GetService<NavigationService>();
        navigation.Attach(ContentFrame);
        navigation.Navigate(ViewModel.InitialScreen);
        Loaded += (_, _) => App.Current.SetTitleBar(TitleBarDragRegion);

        // 一覧がスクロールで PageUp / PageDown を処理済みでもメニュー移動させる
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
    }

    public ShellViewModel ViewModel { get; } = App.GetService<ShellViewModel>();

    /// <summary>PageUp / PageDown で前後のメニューへ移動する（テキスト入力中と選択肢を開いているときは除く）。</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.PageUp or VirtualKey.PageDown)
            || e.OriginalSource is TextBox or PasswordBox or ComboBoxItem
            || e.OriginalSource is ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        var command = e.Key == VirtualKey.PageUp ? ViewModel.PreviousMenuCommand : ViewModel.NextMenuCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }
}
