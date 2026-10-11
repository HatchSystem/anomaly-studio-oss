using System.ComponentModel;

namespace AnomalyStudio.Services;

/// <summary>
/// ソースリンクした AI サービス・モード編集・カレンダー画面用の設定。実アプリの設定ファイルや資格情報へアクセスしない。
/// AppSettings の参照される項目だけを再現する。
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private string _baseUrl = "https://unit-test.invalid/v1";
    private string _model = "test-model";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string EntriesMode { get; set; } = Core.Modes.ModeDefinition.AllModesLabel;

    public int CalendarMinStars { get; set; } = 3;

    public HashSet<string> CalendarCountries { get; } = ["US", "JP", "EU", "GB", "DE", "AU", "CN"];

    public List<string[]> SavedCalendarCountries { get; } = [];

    public Exception? CalendarSaveFailure { get; set; }

    public void NotifyCalendarFilterChanged()
    {
        SavedCalendarCountries.Add([.. CalendarCountries.Order()]);
        if (CalendarSaveFailure is { } failure)
        {
            throw failure;
        }
    }

    public string AiBaseUrl
    {
        get => _baseUrl;
        set
        {
            if (_baseUrl != value)
            {
                _baseUrl = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiBaseUrl)));
            }
        }
    }

    public string AiModel
    {
        get => _model;
        set
        {
            if (_model != value)
            {
                _model = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiModel)));
            }
        }
    }
}
