using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Storage;
using AnomalyStudio.Core.Symbols;
using AnomalyStudio.Core.Trading;

namespace AnomalyStudio.Models;

public sealed record Country(string Code, string Name);

/// <summary>最新の分析結果から抽出されたポイント（銘柄・モード付き）。</summary>
/// <param name="IsComposite">複合ポイント（全銘柄をまとめた順位で選んだポイント）。<see cref="EntryPoint.Rank"/> は全銘柄を通した順位。</param>
public sealed record PointInfo(SymbolProfile Symbol, ModeDefinition Mode, EntryPoint Point, CandidateSummary? Summary, bool IsComposite = false);

/// <summary>ポイントを特定の日に展開したエントリーと、その結果。</summary>
public sealed record EntryItem(PointInfo Info, TradeResult Trade)
{
    public DateTime EntryJst => Trade.EntryJst;

    public DateTime CloseJst => Trade.CloseJst;
}
