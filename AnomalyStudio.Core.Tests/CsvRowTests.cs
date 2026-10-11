using AnomalyStudio.Core.Modes;
using Microsoft.VisualBasic.FileIO;

namespace AnomalyStudio.Core.Tests;

[TestClass]
public sealed class CsvRowTests
{
    [TestMethod]
    public void TradeRow_WithCommaInAllowedModeName_PreservesAllFifteenCells()
    {
        const string modeName = "朝,買い";
        Assert.IsNull(ModeDefinition.ValidateName(modeName, [], null));
        string[] cells =
        [
            "2026-10-11", "2026-10-10", "USDJPY", modeName, "Long", "09:00", "09:05", "5",
            "Settled", "150.25", "0.15", "0.02", "0.13", "13", "勝",
        ];

        var csv = CsvRow.Format(cells);
        StringAssert.Contains(csv, "USDJPY,\"朝,買い\",Long");
        var parsed = ReadRow(csv);
        Assert.AreEqual(15, parsed.Length);
        CollectionAssert.AreEqual(cells, parsed);
    }

    [TestMethod]
    [DataRow("通常の値", "通常の値")]
    [DataRow("朝,買い", "\"朝,買い\"")]
    [DataRow("a\"b", "\"a\"\"b\"")]
    [DataRow("a\rb", "\"a\rb\"")]
    [DataRow("a\nb", "\"a\nb\"")]
    [DataRow("a\r\nb", "\"a\r\nb\"")]
    [DataRow("", "")]
    public void Cell_EscapesOnlyCsvDelimitersWithoutChangingTheValue(string value, string expected)
    {
        Assert.AreEqual(expected, CsvRow.Format(value));
    }

    [TestMethod]
    public void ReportRow_PreservesQuotedAndMultilineCellsAndTrailingEmptyValues()
    {
        string[] cells = ["期間,集計", "10", "6", "4", "60.0%", "\"表示値\"", "1行目" + Environment.NewLine + "2行目", ""];
        CollectionAssert.AreEqual(cells, ReadRow(CsvRow.Format(cells)));
        Assert.AreEqual("a,,", CsvRow.Format("a", null, string.Empty));
        CollectionAssert.AreEqual(new[] { "a", "", "" }, ReadRow(CsvRow.Format("a", null, string.Empty)));
    }

    private static string[] ReadRow(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv))
        {
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");
        return parser.ReadFields() ?? [];
    }
}
