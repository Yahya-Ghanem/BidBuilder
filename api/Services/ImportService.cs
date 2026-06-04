using System.Globalization;
using ClosedXML.Excel;

namespace BidBuilder.Api.Services;

/// <summary>A parsed BOQ item from an uploaded spreadsheet (pre-persistence).</summary>
public record ImportItem(string ItemCode, string Description, string Unit, decimal Quantity, decimal UnitRate, string? AssemblyCode);

/// <summary>A parsed BOQ section grouping its items (pre-persistence).</summary>
public record ImportSection(string Code, string Title, List<ImportItem> Items);

/// <summary>Raised when an uploaded workbook can't be understood; the message is
/// safe to surface to the user (missing headers, bad row, etc.).</summary>
public class ImportException(string message) : Exception(message);

/// <summary>
/// Reads a Bill of Quantities from an .xlsx upload and produces a clean,
/// validated in-memory model (no DB). Header-driven and forgiving of column
/// order and common aliases; rows are grouped into sections by Section Code
/// (falling back to Section Title). Parsing fully validates before the caller
/// persists, so an import is all-or-nothing.
/// </summary>
public class ImportService
{
    private static readonly string[] DescCols = { "description", "desc" };
    private static readonly string[] QtyCols  = { "quantity", "qty" };
    private static readonly string[] SecCode  = { "section code", "section ref" };
    private static readonly string[] SecTitle = { "section title", "section", "section name" };
    private static readonly string[] ItemCode = { "item code", "code", "ref" };
    private static readonly string[] UnitCols = { "unit", "uom" };
    private static readonly string[] RateCols = { "unit rate", "rate" };
    private static readonly string[] AsmCols  = { "assembly code", "assembly" };

    /// <summary>Parse the first worksheet into sections + items, in sheet order.</summary>
    public List<ImportSection> ParseBoq(Stream stream)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(stream); }
        catch { throw new ImportException("The file is not a valid .xlsx workbook."); }

        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault()
                ?? throw new ImportException("The workbook has no worksheets.");

            var headerRow = ws.FirstRowUsed()
                ?? throw new ImportException("The sheet is empty — download the template for the expected columns.");

            // Map header text → column number.
            var cols = new Dictionary<string, int>();
            foreach (var cell in headerRow.CellsUsed())
            {
                var key = cell.GetString().Trim().ToLowerInvariant();
                if (key.Length > 0 && !cols.ContainsKey(key)) cols[key] = cell.Address.ColumnNumber;
            }

            int? Find(string[] names) => names.Select(n => cols.TryGetValue(n, out var c) ? (int?)c : null).FirstOrDefault(c => c is not null);

            var descCol = Find(DescCols) ?? throw new ImportException("Missing required column 'Description'.");
            var qtyCol  = Find(QtyCols)  ?? throw new ImportException("Missing required column 'Quantity'.");
            var secCodeCol = Find(SecCode);
            var secTitleCol = Find(SecTitle);
            var itemCodeCol = Find(ItemCode);
            var unitCol = Find(UnitCols);
            var rateCol = Find(RateCols);
            var asmCol  = Find(AsmCols);

            string Str(IXLRow row, int? col) => col is null ? "" : row.Cell(col.Value).GetString().Trim();

            // Preserve first-seen section order; key by code (else title).
            var order = new List<string>();
            var byKey = new Dictionary<string, ImportSection>();

            int firstData = headerRow.RowNumber() + 1;
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();

            for (int rn = firstData; rn <= lastRow; rn++)
            {
                var row = ws.Row(rn);
                var desc = Str(row, descCol);
                var qtyStr = Str(row, qtyCol);
                var secCode = Str(row, secCodeCol);
                var secTitle = Str(row, secTitleCol);
                var itemCode = Str(row, itemCodeCol);

                // Skip a fully blank line.
                if (desc.Length == 0 && qtyStr.Length == 0 && secCode.Length == 0 && secTitle.Length == 0 && itemCode.Length == 0)
                    continue;

                if (desc.Length == 0) throw new ImportException($"Row {rn}: Description is required.");
                if (!TryDecimal(qtyStr, out var qty)) throw new ImportException($"Row {rn}: Quantity '{qtyStr}' is not a valid number.");
                if (qty < 0) throw new ImportException($"Row {rn}: Quantity cannot be negative.");

                var rateStr = Str(row, rateCol);
                if (rateStr.Length > 0 && !TryDecimal(rateStr, out _)) throw new ImportException($"Row {rn}: Unit rate '{rateStr}' is not a valid number.");
                TryDecimal(rateStr, out var rate);

                var asm = Str(row, asmCol);
                var key = secCode.Length > 0 ? "C:" + secCode : (secTitle.Length > 0 ? "T:" + secTitle : "__default__");
                if (!byKey.TryGetValue(key, out var section))
                {
                    var title = secTitle.Length > 0 ? secTitle : (secCode.Length > 0 ? secCode : "General");
                    section = new ImportSection(secCode, title, new List<ImportItem>());
                    byKey[key] = section;
                    order.Add(key);
                }
                section.Items.Add(new ImportItem(itemCode, desc, unitCol is null ? "" : Str(row, unitCol), qty, rate, asm.Length > 0 ? asm : null));
            }

            var sections = order.Select(k => byKey[k]).Where(s => s.Items.Count > 0).ToList();
            if (sections.Count == 0) throw new ImportException("No data rows found below the header.");
            return sections;
        }
    }

    private static bool TryDecimal(string s, out decimal value)
    {
        if (string.IsNullOrWhiteSpace(s)) { value = 0m; return true; }
        return decimal.TryParse(s.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A ready-to-fill .xlsx template with the expected columns + examples.</summary>
    public byte[] BuildBoqTemplate()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("BOQ");
        var headers = new[] { "Section Code", "Section Title", "Item Code", "Description", "Unit", "Quantity", "Unit Rate", "Assembly Code" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

        // Example rows: one ad-hoc (Unit Rate), one assembly-priced (Assembly Code).
        ws.Cell(2, 1).Value = "03"; ws.Cell(2, 2).Value = "Concrete Works"; ws.Cell(2, 3).Value = "C-01";
        ws.Cell(2, 4).Value = "RC footing (assembly-priced)"; ws.Cell(2, 5).Value = "m3"; ws.Cell(2, 6).Value = 250; ws.Cell(2, 8).Value = "ASM-RC-FOOT";
        ws.Cell(3, 1).Value = "03"; ws.Cell(3, 2).Value = "Concrete Works"; ws.Cell(3, 3).Value = "C-02";
        ws.Cell(3, 4).Value = "Miscellaneous (ad-hoc rate)"; ws.Cell(3, 5).Value = "no"; ws.Cell(3, 6).Value = 100; ws.Cell(3, 7).Value = 80;
        ws.Columns().AdjustToContents();

        var notes = wb.AddWorksheet("Instructions");
        var lines = new[]
        {
            "BidBuilder — BOQ import template",
            "",
            "Fill the 'BOQ' sheet, one row per item:",
            "• Description and Quantity are required.",
            "• Rows are grouped into sections by Section Code (or Section Title if no code).",
            "• For an assembly-priced item, put the Assembly Code (the unit rate is derived).",
            "• For an ad-hoc item, leave Assembly Code blank and enter a Unit Rate.",
            "• Imported sections/items are appended to the estimate.",
        };
        for (int i = 0; i < lines.Length; i++) notes.Cell(i + 1, 1).Value = lines[i];
        notes.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        notes.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
