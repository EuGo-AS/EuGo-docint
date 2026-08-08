using System.Net;
using DocInt.Api.Contracts;
using DocInt.Api.Engines;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace DocInt.Tests;

public class SpreadsheetEngineTests
{
    private static async Task<EngineOutcome> Run(string fixture) =>
        await Run(Golden.Bytes(fixture), fixture);

    private static async Task<EngineOutcome> Run(byte[] bytes, string name = "mem.xlsx")
    {
        var engine = new SpreadsheetEngine();
        var item = new FileItem { Index = 0, Name = name, Kind = FileKind.Xlsx,
            Bytes = bytes, SizeBytes = bytes.Length };
        return await engine.ExtractAsync(item, CancellationToken.None);
    }

    [Fact]
    public async Task Bom_xlsx_yields_exact_typed_cells()
    {
        var outcome = await Run("bom.xlsx");
        var bom = outcome.Result.Tables![0];

        Assert.Equal("BoM", bom.Name);
        Assert.Equal(["Part", "Qty", "UnitPrice", "Total", "InStock", "Updated"],
            bom.Rows[0].Cast<string>().ToArray());
        Assert.Equal("M3 screw", bom.Rows[1][0]);
        Assert.Equal(40m, bom.Rows[1][1]);
        Assert.Equal(19.99m, bom.Rows[1][2]);       // exact — never re-parsed display text
        Assert.Equal(799.6m, bom.Rows[1][3]);       // formula cell: cached value
        Assert.Equal(true, bom.Rows[1][4]);
        Assert.Equal("2026-07-19", bom.Rows[1][5]); // date-styled number → ISO-8601
        Assert.Equal(0.125m, bom.Rows[2][2]);
        Assert.Equal(false, bom.Rows[2][4]);
        Assert.Null(outcome.Result.Error);
    }

    [Fact]
    public async Task Markdown_renders_both_sheets()
    {
        var outcome = await Run("bom.xlsx");
        Assert.Equal(2, outcome.Result.Tables!.Count);
        Assert.Equal("Notes", outcome.Result.Tables[1].Name);
        Assert.Contains("## BoM", outcome.Result.Markdown);
        Assert.Contains("## Notes", outcome.Result.Markdown);
        Assert.Contains("| M3 screw | 40 | 19.99 |", outcome.Result.Markdown);
        Assert.Equal(2, outcome.PagesProcessed);
    }

    [Fact]
    public async Task Corrupt_xlsx_maps_to_corrupt_error()
    {
        var outcome = await Run("corrupt.xlsx");
        Assert.Equal(ErrorCodes.Corrupt, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Chartsheet_sheet_is_skipped_with_warning_not_thrown()
    {
        // Regression: a <sheet> pointing at a chartsheet/dialogsheet resolves via GetPartById to a
        // non-WorksheetPart; the engine must skip that tab (with a warning) rather than throw, and
        // must still extract the real worksheet(s) present in the same workbook.
        var outcome = await Run("chartsheet.xlsx");

        Assert.Null(outcome.Result.Error);
        var data = Assert.Single(outcome.Result.Tables!);
        Assert.Equal("Data", data.Name);
        Assert.Equal(["Item", "Count"], data.Rows[0].Cast<string>().ToArray());
        Assert.Equal("Widget", data.Rows[1][0]);
        Assert.Equal(7m, data.Rows[1][1]);
        Assert.Contains(outcome.Result.Warnings, w => w.Contains("Chart1") && w.Contains("skipped"));
        Assert.Equal(1, outcome.PagesProcessed);
    }

    [Fact]
    public async Task Http_contract_returns_typed_json_numbers()
    {
        using var factory = new ContractTestFactory();
        using var form = Multipart.Form(("bom.xlsx", Golden.Bytes("bom.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        var response = await factory.CreateClient().PostAsync("/v1/extract", form);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"kind\":\"xlsx\"", json);
        Assert.Contains("19.99", json);
        Assert.DoesNotContain("\"19.99\"", json);   // number, not string
        Assert.Contains("\"2026-07-19\"", json);
    }

    // --- Regression: crafted "1e400" numeric cell must never 500 the request. ---
    // decimal.Parse overflows (as before); the OverflowException fallback previously
    // returned double.Parse's result unconditionally, and double.Parse("1e400") yields
    // double.PositiveInfinity without throwing. That non-finite double used to survive
    // into the response and crash System.Text.Json serialization outside any try/catch.

    [Fact]
    public async Task Overflow_cell_is_kept_as_text_with_warning_not_infinity()
    {
        var outcome = await Run("overflow.xlsx");

        Assert.Null(outcome.Result.Error);
        var sheet = Assert.Single(outcome.Result.Tables!);
        Assert.Equal("normal", sheet.Rows[1][0]);
        Assert.Equal(42m, sheet.Rows[1][1]);            // sibling row unaffected
        Assert.Equal("huge", sheet.Rows[2][0]);
        Assert.Equal("1e400", sheet.Rows[2][1]);         // kept as text, never a non-finite double
        Assert.Contains(outcome.Result.Warnings,
            w => w.Contains("B3") && w.Contains("out of numeric range"));
    }

    [Fact]
    public async Task Http_contract_returns_200_for_overflow_cell()
    {
        using var factory = new ContractTestFactory();
        using var form = Multipart.Form(("overflow.xlsx", Golden.Bytes("overflow.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

        var response = await factory.CreateClient().PostAsync("/v1/extract", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"error\":{", json);
        Assert.Contains("\"1e400\"", json);
    }

    // --- Regression: a malformed-but-openable date/numeric cell must degrade to text +
    // warning on that one cell, not fail the whole file (and must never surface the raw
    // parse-exception text, which can embed cell content, as the file-level error message). ---

    [Fact]
    public async Task Malformed_cells_degrade_to_warnings_not_engine_error()
    {
        var outcome = await Run("malformed-cells.xlsx");

        Assert.Null(outcome.Result.Error);
        var sheet = Assert.Single(outcome.Result.Tables!);

        Assert.Equal("good", sheet.Rows[1][0]);
        Assert.Equal(42m, sheet.Rows[1][1]);
        Assert.Equal(1m, sheet.Rows[1][2]);

        Assert.Equal("bad number", sheet.Rows[2][0]);
        Assert.Equal("not-a-number", sheet.Rows[2][1]);  // FormatException on decimal/double.Parse
        Assert.Equal(2m, sheet.Rows[2][2]);              // sibling cell in the same row intact

        Assert.Equal("bad date", sheet.Rows[3][0]);
        Assert.Equal("not-a-date", sheet.Rows[3][1]);    // FormatException on DateTime.Parse
        Assert.Equal(3m, sheet.Rows[3][2]);

        Assert.Equal("bad serial", sheet.Rows[4][0]);
        Assert.Equal("1e30", sheet.Rows[4][1]);          // ArgumentException on DateTime.FromOADate
        Assert.Equal(4m, sheet.Rows[4][2]);

        Assert.Contains(outcome.Result.Warnings, w => w.Contains("B3") && w.Contains("number"));
        Assert.Contains(outcome.Result.Warnings, w => w.Contains("B4") && w.Contains("date"));
        Assert.Contains(outcome.Result.Warnings, w => w.Contains("B5") && w.Contains("date"));
    }

    // --- Newline escaping: a cell value with an embedded CR/LF must stay inside its own
    // Markdown table row (a raw newline in a "| ... |" row terminates the row). ---

    [Fact]
    public async Task Embedded_newline_in_cell_renders_as_br_on_a_single_row()
    {
        var outcome = await Run(BuildXlsx("Notes",
            Row(1, Str("A1", "Note")),
            Row(2, Str("A2", "line1\nline2"))));

        Assert.Null(outcome.Result.Error);
        Assert.Contains("| line1<br>line2 |", outcome.Result.Markdown);
        Assert.DoesNotContain("line1\nline2", outcome.Result.Markdown);
    }

    // --- Interior row-gap fidelity: a blank row between two populated rows must survive as an
    // all-null row rather than collapsing (leading gaps are not synthesized; trailing rows are
    // still trimmed). ---

    [Fact]
    public async Task Interior_blank_row_is_preserved_as_all_null_row()
    {
        var outcome = await Run(BuildXlsx("Gapped",
            Row(1, Str("A1", "Part"), Str("B1", "Qty")),
            Row(2, Str("A2", "x"), Str("B2", "1")),
            Row(4, Str("A4", "y"), Str("B4", "2"))));   // row 3 is blank

        Assert.Null(outcome.Result.Error);
        var rows = outcome.Result.Tables![0].Rows;
        Assert.Equal(4, rows.Count);
        Assert.Equal("x", rows[1][0]);
        Assert.True(rows[2].All(v => v is null));       // the missing row 3 surfaces as all-null
        Assert.Equal("y", rows[3][0]);
        Assert.Equal("2", rows[3][1]);
    }

    // --- Bounded gap synthesis: RowIndex is attacker-controlled, so a huge r= delta must not
    // synthesize an unbounded run of empty rows (OOM). Cap it, warn once, keep extracting. ---

    [Fact]
    public async Task Huge_row_gap_is_bounded_and_warns_instead_of_exhausting_memory()
    {
        // Populated rows at r=1 and r=2,000,000: an unbounded loop would allocate ~2M empty rows.
        var outcome = await Run(BuildXlsx("Huge",
            Row(1, Str("A1", "first")),
            Row(2_000_000, Str("A2000000", "last"))));

        Assert.Null(outcome.Result.Error);
        var rows = outcome.Result.Tables![0].Rows;
        Assert.True(rows.Count <= SpreadsheetEngine.MaxSyntheticGapRows + 2,
            $"grid had {rows.Count} rows; expected <= cap ({SpreadsheetEngine.MaxSyntheticGapRows}) + 2 populated");
        Assert.Equal("first", rows[0][0]);
        Assert.Equal("last", rows[^1][0]);
        Assert.Contains(outcome.Result.Warnings, w => w.Contains("preservation limit"));
    }

    // --- CellValue branch coverage: the degrade-to-warning and typed paths that the existing
    // golden fixtures don't already exercise. ---

    [Fact]
    public async Task Formula_cell_without_cached_value_is_null_with_warning()
    {
        var outcome = await Run(BuildXlsx("Calc",
            Row(1, Str("A1", "label")),
            Row(2, Str("A2", "sum"), FormulaNoCache("B2", "1+1"))));

        Assert.Null(outcome.Result.Error);
        Assert.Null(outcome.Result.Tables![0].Rows[1][1]);
        Assert.Contains(outcome.Result.Warnings,
            w => w.Contains("B2") && w.Contains("has no cached value"));
    }

    [Fact]
    public async Task Error_cell_is_null_with_warning()
    {
        var outcome = await Run(BuildXlsx("Errs",
            Row(1, Str("A1", "label")),
            Row(2, Str("A2", "div"), ErrorCell("B2", "#DIV/0!"))));

        Assert.Null(outcome.Result.Error);
        Assert.Null(outcome.Result.Tables![0].Rows[1][1]);
        Assert.Contains(outcome.Result.Warnings,
            w => w.Contains("B2") && w.Contains("contains error"));
    }

    [Fact]
    public async Task Explicit_date_cell_renders_iso_string()
    {
        var outcome = await Run(BuildXlsx("Dates",
            Row(1, Str("A1", "when")),
            Row(2, DateCell("A2", "2026-07-19"))));

        Assert.Null(outcome.Result.Error);
        Assert.Equal("2026-07-19", outcome.Result.Tables![0].Rows[1][0]);
    }

    [Fact]
    public async Task Non_finite_double_cell_is_kept_as_raw_text_with_warning()
    {
        var outcome = await Run("overflow.xlsx");   // B3 raw <v> is "1e400"

        Assert.Null(outcome.Result.Error);
        var value = outcome.Result.Tables![0].Rows[2][1];
        Assert.IsType<string>(value);               // raw-text fallback, never a non-finite double
        Assert.Equal("1e400", value);
        Assert.Contains(outcome.Result.Warnings,
            w => w.Contains("B3") && w.Contains("out of numeric range, kept as text"));
    }

    // --- In-memory XLSX construction: keeps these shapes out of the committed golden fixtures
    // (whose OpenXML generator writes non-deterministic ids), so the fixture set stays stable. ---

    private static byte[] BuildXlsx(string sheetName, params S.Row[] rows)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();
            var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new S.SheetData();
            foreach (var row in rows) sheetData.Append(row);
            sheetPart.Worksheet = new S.Worksheet(sheetData);
            workbookPart.Workbook.AppendChild(new S.Sheets(
                new S.Sheet { Id = workbookPart.GetIdOfPart(sheetPart), SheetId = 1U, Name = sheetName }));
            workbookPart.Workbook.Save();
        }
        return ms.ToArray();
    }

    private static S.Row Row(uint index, params S.Cell[] cells)
    {
        var row = new S.Row { RowIndex = index };
        row.Append(cells);
        return row;
    }

    private static S.Cell Str(string reference, string text) => new()
    { CellReference = reference, DataType = S.CellValues.String, CellValue = new S.CellValue(text) };

    private static S.Cell FormulaNoCache(string reference, string formula) => new()
    { CellReference = reference, CellFormula = new S.CellFormula(formula) };

    private static S.Cell ErrorCell(string reference, string code) => new()
    { CellReference = reference, DataType = S.CellValues.Error, CellValue = new S.CellValue(code) };

    private static S.Cell DateCell(string reference, string value) => new()
    { CellReference = reference, DataType = S.CellValues.Date, CellValue = new S.CellValue(value) };
}
