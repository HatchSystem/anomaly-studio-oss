using System.Text.Json.Serialization;
using AnomalyStudio.Core.Analysis;
using AnomalyStudio.Core.Modes;
using AnomalyStudio.Core.Symbols;

namespace AnomalyStudio.Core;

/// <summary>分析実行時のパラメーター（analysis_run.params に保存）。</summary>
public sealed record RunParameters(
    IReadOnlyList<int> Periods,
    IReadOnlyList<int> ScorePeriods,
    double MinSampleRatio,
    double FallbackSpread,
    Dictionary<string, QualityThreshold> Quality);

public sealed record QualityThreshold(int Max, int Threshold);

/// <summary>Core が読み書きする JSON の型情報（トリミング・AOT でも動くようソース生成を使う）。</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunParameters))]
[JsonSerializable(typeof(List<SymbolProfile>))]
[JsonSerializable(typeof(List<ModeDefinition>))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
