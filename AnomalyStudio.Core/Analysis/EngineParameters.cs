namespace AnomalyStudio.Core.Analysis;

/// <summary>分析ロジックのパラメーター。既定値は docs/design/anomaly-engine.md §2 の計算仕様。</summary>
public sealed record EngineParameters
{
    /// <summary>計算方式の版（分析結果に記録し、ウォークフォワード用に保存したポイントのキーに含める。計算を変えたら上げる）。</summary>
    public const string LogicVersion = "1";

    /// <summary>検証期間（日）。最長の期間が取得範囲になる。</summary>
    public IReadOnlyList<int> Periods { get; init; } = [30, 90, 180, 365];

    /// <summary>スコア平均に使う期間（180 日は表示のみ）。</summary>
    public IReadOnlyList<int> ScorePeriods { get; init; } = [30, 90, 365];

    /// <summary>各スコア期間で最大サンプル数のこの割合未満の候補を、正規化・ポイント抽出の母集団から外す。</summary>
    public double MinSampleRatio { get; init; } = 0.80;

    /// <summary>実測 spread が取れないときの控除値（価格単位、銘柄プロファイルから渡す）。</summary>
    public double FallbackSpread { get; init; } = 0.5;

    public int MaxPeriod => Periods.Max();
}
