using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace BidBuilder.Api.Services;

/// <summary>Header metadata + the computed breakdown, fed to the exporters.</summary>
public record ExportModel(
    string CompanyName, string? CompanyAddress, string? CompanyContact,
    string ProjectCode, string ProjectName,
    string? Client, string? Location, string GeneratedOn, EstimateBreakdown Estimate,
    byte[]? LogoBytes = null, AreaRollupResult? AreaRollup = null);

/// <summary>Editable fields for the bid submission letter; blanks fall back to sensible defaults.</summary>
public record BidLetterOptions(
    string? Recipient = null, string? RecipientTitle = null,
    string? Signatory = null, string? SignatoryTitle = null,
    int? ValidityDays = null, string? Note = null, string? Reference = null);

/// <summary>
/// Renders an estimate as a priced-BOQ + bid-summary in Excel (ClosedXML) and
/// PDF (QuestPDF) — the client-facing deliverable.
/// </summary>
public class ExportService
{
    // ── Workbook palette ─────────────────────────────────────────────────────
    // A small, consistent theme so every sheet reads as one branded document.
    private static readonly XLColor Brand       = XLColor.FromArgb(0x0F, 0x76, 0x6E); // teal-700 — headers / accents
    private static readonly XLColor BrandDark   = XLColor.FromArgb(0x13, 0x4E, 0x4A); // teal-900 — total figures
    private static readonly XLColor GroupFill   = XLColor.FromArgb(0xCC, 0xFB, 0xF1); // teal-100 — area / top-level rows
    private static readonly XLColor SubGroupFill   = XLColor.FromArgb(0xCB, 0xD5, 0xE1); // slate-300 — sub-area rows
    private static readonly XLColor SubGroupFillLt = XLColor.FromArgb(0xE2, 0xE8, 0xF0); // slate-200 — deeper sub-area rows
    private static readonly XLColor BandFill    = XLColor.FromArgb(0xF1, 0xF5, 0xF9); // slate-100 — zebra stripe
    private static readonly XLColor BorderColor = XLColor.FromArgb(0x94, 0xA3, 0xB8); // slate-400 — table outline
    private static readonly XLColor BorderLight = XLColor.FromArgb(0xE2, 0xE8, 0xF0); // slate-200 — inner gridlines
    private static readonly XLColor MatColor    = XLColor.FromArgb(0xF5, 0x9E, 0x0B); // amber-500 — material bars
    private static readonly XLColor ManColor    = XLColor.FromArgb(0x3B, 0x82, 0xF6); // blue-500  — manpower bars

    /// <summary>Brand-fill a header row range (bold white on teal, centred).</summary>
    private static void StyleHeader(IXLRange r)
    {
        r.Style.Font.SetBold().Font.SetFontColor(XLColor.White);
        r.Style.Fill.SetBackgroundColor(Brand);
        r.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
    }

    /// <summary>Thin outline + hairline inner gridlines around a table range.</summary>
    private static void Box(IXLRange r)
    {
        r.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
        r.Style.Border.SetOutsideBorderColor(BorderColor);
        r.Style.Border.SetInsideBorder(XLBorderStyleValues.Hair);
        r.Style.Border.SetInsideBorderColor(BorderLight);
    }

    // ── Excel ──────────────────────────────────────────────────────────────
    public byte[] BuildExcel(ExportModel m)
    {
        var e = m.Estimate;
        using var wb = new XLWorkbook();

        // ── Summary sheet ───────────────────────────────────────────────────
        var sum = wb.AddWorksheet("Bid Summary");
        sum.Cell("A1").Value = m.CompanyName;
        sum.Cell("A1").Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(Brand);

        if (m.LogoBytes is { Length: > 0 })
        {
            // Embedding the branding logo must never take down the whole export — a
            // bad/edge-case image just gets skipped.
            try
            {
                // NOT disposed here on purpose: ClosedXML reads the picture stream lazily
                // at SaveAs, so the stream must outlive this block (MemoryStream has no
                // unmanaged resources to leak).
                var logoStream = new MemoryStream(m.LogoBytes);
                var pic = sum.AddPicture(logoStream, "logo");
                // MoveTo first: ClosedXML only allows resizing once the picture has a
                // Move placement — scaling before this throws ("placement should be …Move").
                pic.MoveTo(sum.Cell("E1"));
                const double maxH = 64; // px — keep the header compact, preserve aspect ratio
                if (pic.OriginalHeight > maxH) pic.Scale(maxH / pic.OriginalHeight);
            }
            catch { /* skip the logo, keep the document */ }
        }
        int h = 2;
        if (!string.IsNullOrWhiteSpace(m.CompanyAddress)) { sum.Cell(h, 1).Value = m.CompanyAddress; sum.Cell(h, 1).Style.Font.FontColor = XLColor.Gray; h++; }
        if (!string.IsNullOrWhiteSpace(m.CompanyContact)) { sum.Cell(h, 1).Value = m.CompanyContact; sum.Cell(h, 1).Style.Font.FontColor = XLColor.Gray; h++; }
        h++; // spacer row
        sum.Cell(h, 1).Value = $"Bid Summary — {m.ProjectCode}";
        sum.Cell(h, 1).Style.Font.SetBold().Font.SetFontSize(12).Font.SetFontColor(BrandDark); h++;
        sum.Cell(h, 1).Value = m.ProjectName; sum.Cell(h, 1).Style.Font.SetBold(); h++;
        sum.Cell(h, 1).Value = $"Client: {m.Client ?? "—"}    Location: {m.Location ?? "—"}"; sum.Cell(h, 1).Style.Font.FontColor = XLColor.Gray; h++;
        sum.Cell(h, 1).Value = $"Generated: {m.GeneratedOn}    Currency: {e.Currency}"; sum.Cell(h, 1).Style.Font.FontColor = XLColor.Gray; h++;

        int r = h + 1;
        sum.Cell(r, 1).Value = "Component"; sum.Cell(r, 2).Value = $"Amount ({e.Currency})";
        StyleHeader(sum.Range(r, 1, r, 2));
        sum.Cell(r, 2).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
        int sumHead = r; r++;
        void Line(string label, decimal value, bool bold = false)
        {
            sum.Cell(r, 1).Value = label;
            sum.Cell(r, 2).Value = value; sum.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
            if (bold) { sum.Cell(r, 1).Style.Font.SetBold(); sum.Cell(r, 2).Style.Font.SetBold(); }
            r++;
        }
        Line("Direct cost", e.DirectCost);
        Line("Indirect (preliminaries)", e.IndirectCost);
        Line("Markups", e.MarkupCost);
        // BID PRICE — the headline figure, reversed out on the brand colour.
        sum.Cell(r, 1).Value = "BID PRICE";
        sum.Cell(r, 2).Value = e.BidPrice; sum.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
        var bidRange = sum.Range(r, 1, r, 2);
        bidRange.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Font.SetFontSize(12);
        bidRange.Style.Fill.SetBackgroundColor(Brand);
        int sumLast = r; r++;
        if (e.Fx is not null)
        {
            sum.Cell(r, 1).Value = $"≈ in {e.Fx.SecondaryCurrency}  (1 {e.Currency} = {e.Fx.Rate:#,##0.######} {e.Fx.SecondaryCurrency}{(e.Fx.Frozen ? ", frozen" : "")})";
            sum.Cell(r, 1).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
            sum.Cell(r, 2).Value = e.Fx.ConvertedBidPrice;
            sum.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
            sum.Cell(r, 2).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
            sumLast = r; r++;
        }
        Box(sum.Range(sumHead, 1, sumLast, 2));
        sum.Columns().AdjustToContents();
        sum.Column(2).Width = Math.Max(sum.Column(2).Width, 18);

        // ── Priced BOQ sheet ────────────────────────────────────────────────
        var boq = wb.AddWorksheet("Priced BOQ");
        boq.Cell(1, 1).Value = "Code"; boq.Cell(1, 2).Value = "Description"; boq.Cell(1, 3).Value = "Unit";
        boq.Cell(1, 4).Value = "Qty"; boq.Cell(1, 5).Value = $"Rate ({e.Currency})"; boq.Cell(1, 6).Value = $"Total ({e.Currency})";
        StyleHeader(boq.Range(1, 1, 1, 6));
        boq.Range(1, 4, 1, 6).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);

        int row = 2;
        foreach (var s in e.Sections)
        {
            boq.Cell(row, 1).Value = s.Code;
            boq.Cell(row, 2).Value = s.Title;
            boq.Cell(row, 6).Value = s.SectionTotal; boq.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            var secRange = boq.Range(row, 1, row, 6);
            secRange.Style.Font.SetBold();
            secRange.Style.Fill.SetBackgroundColor(GroupFill);
            boq.Cell(row, 6).Style.Font.SetFontColor(BrandDark);
            row++;
            bool band = false;
            foreach (var i in s.Items)
            {
                var fill = band ? BandFill : XLColor.White;
                boq.Cell(row, 1).Value = i.ItemCode;
                boq.Cell(row, 2).Value = i.Description;
                boq.Cell(row, 3).Value = i.Unit;
                boq.Cell(row, 4).Value = i.Quantity; boq.Cell(row, 4).Style.NumberFormat.Format = "#,##0.####";
                boq.Cell(row, 5).Value = i.UnitRate;  boq.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                boq.Cell(row, 6).Value = i.LineTotal; boq.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
                boq.Cell(row, 6).Style.Font.SetFontColor(BrandDark);
                boq.Range(row, 1, row, 6).Style.Fill.SetBackgroundColor(fill);
                row++;
                // Unit-rate build-up detail (Material/Labor/… + Waste%/Overheads%).
                foreach (var comp in i.Components)
                {
                    var detail = comp.CalcKind == "Percent" ? $" ({comp.Value:#,##0.##}%)"
                        : comp.Quantity is not null && comp.Rate is not null ? $" ({comp.Quantity:#,##0.####} × {comp.Rate:#,##0.##})"
                        : "";
                    boq.Cell(row, 2).Value = $"    • {comp.Name}{detail}";
                    boq.Cell(row, 5).Value = comp.Amount; boq.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                    boq.Range(row, 1, row, 6).Style.Fill.SetBackgroundColor(fill);
                    boq.Row(row).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
                    row++;
                }
                band = !band;
            }
        }
        if (row > 2) Box(boq.Range(1, 1, row - 1, 6));
        boq.SheetView.FreezeRows(1);
        boq.Columns().AdjustToContents();

        // ── Cost-by-Area sheet (line totals escalated up the area tree) ───────
        if (m.AreaRollup is { Areas.Count: > 0 })
            RenderCostByArea(wb.AddWorksheet("Cost by Area"), m, 1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── CSV ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Flat, machine-readable priced BOQ — one row per line item with its section
    /// repeated (denormalized) so procurement tools / spreadsheets can pivot and
    /// re-import it. RFC-4180 quoting; UTF-8 BOM so Excel detects the encoding.
    /// </summary>
    public byte[] BuildCsv(ExportModel m)
    {
        var e = m.Estimate;
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append('﻿'); // UTF-8 BOM — Excel opens it as UTF-8 instead of ANSI

        sb.Append("Section Code,Section Title,Item Code,Description,Unit,Qty,Unit Rate,Line Total,Currency\r\n");
        foreach (var s in e.Sections)
            foreach (var i in s.Items)
                sb.Append(CsvEsc(s.Code)).Append(',')
                  .Append(CsvEsc(s.Title)).Append(',')
                  .Append(CsvEsc(i.ItemCode)).Append(',')
                  .Append(CsvEsc(i.Description)).Append(',')
                  .Append(CsvEsc(i.Unit)).Append(',')
                  .Append(i.Quantity.ToString("0.####", ci)).Append(',')
                  .Append(i.UnitRate.ToString("0.00", ci)).Append(',')
                  .Append(i.LineTotal.ToString("0.00", ci)).Append(',')
                  .Append(CsvEsc(e.Currency)).Append("\r\n");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // ── PDF ────────────────────────────────────────────────────────────────
    public byte[] BuildPdf(ExportModel m)
    {
        var e = m.Estimate;
        string Money(decimal v) => $"{e.Currency} {v:#,##0.00}";

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(36);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Row(top =>
                    {
                        top.RelativeItem().Column(left =>
                        {
                            left.Item().Text(m.CompanyName).FontSize(16).Bold();
                            if (!string.IsNullOrWhiteSpace(m.CompanyAddress))
                                left.Item().Text(m.CompanyAddress).FontSize(8).FontColor(Colors.Grey.Darken1);
                            if (!string.IsNullOrWhiteSpace(m.CompanyContact))
                                left.Item().Text(m.CompanyContact).FontSize(8).FontColor(Colors.Grey.Darken1);
                        });
                        if (m.LogoBytes is { Length: > 0 })
                            top.ConstantItem(140).MaxHeight(50).AlignRight().AlignTop().Image(m.LogoBytes).FitArea();
                    });
                    col.Item().PaddingTop(2).Text($"Bid Summary — {m.ProjectCode}").FontSize(11).FontColor(Colors.Grey.Darken2);
                    col.Item().Text(m.ProjectName).Bold();
                    col.Item().Text($"Client: {m.Client ?? "—"}   Location: {m.Location ?? "—"}   Generated: {m.GeneratedOn}")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                });

                page.Content().PaddingVertical(8).Column(col =>
                {
                    col.Item().Text("Priced Bill of Quantities").FontSize(11).Bold();
                    col.Item().PaddingTop(4).Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.ConstantColumn(45); c.RelativeColumn(3); c.ConstantColumn(35); c.ConstantColumn(50); c.ConstantColumn(60); c.ConstantColumn(70); });
                        table.Header(h =>
                        {
                            foreach (var (t, al) in new[] { ("Code", "l"), ("Description", "l"), ("Unit", "l"), ("Qty", "r"), ("Rate", "r"), ("Total", "r") })
                            {
                                var cell = h.Cell().Background(Colors.Grey.Lighten2).Padding(3);
                                if (al == "r") cell.AlignRight().Text(t).Bold(); else cell.Text(t).Bold();
                            }
                        });

                        foreach (var s in e.Sections)
                        {
                            table.Cell().Background(Colors.Grey.Lighten4).Padding(3).Text(s.Code).SemiBold();
                            table.Cell().ColumnSpan(4).Background(Colors.Grey.Lighten4).Padding(3).Text(s.Title).SemiBold();
                            table.Cell().Background(Colors.Grey.Lighten4).Padding(3).AlignRight().Text(Money(s.SectionTotal)).SemiBold();
                            foreach (var i in s.Items)
                            {
                                table.Cell().Padding(3).Text(i.ItemCode);
                                table.Cell().Padding(3).Column(dc =>
                                {
                                    dc.Item().Text(i.Description);
                                    if (i.Components.Count > 0)
                                        dc.Item().Text(string.Join("   +   ", i.Components.Select(comp =>
                                            comp.CalcKind == "Percent" ? $"{comp.Name} {comp.Value:#,##0.##}%"
                                            : comp.Quantity is not null && comp.Rate is not null ? $"{comp.Name} {comp.Quantity:#,##0.####}×{comp.Rate:#,##0.##}={comp.Amount:#,##0.00}"
                                            : $"{comp.Name} {comp.Amount:#,##0.00}")))
                                            .FontSize(7).FontColor(Colors.Grey.Darken1);
                                });
                                table.Cell().Padding(3).Text(i.Unit);
                                table.Cell().Padding(3).AlignRight().Text($"{i.Quantity:#,##0.####}");
                                table.Cell().Padding(3).AlignRight().Text($"{i.UnitRate:#,##0.00}");
                                table.Cell().Padding(3).AlignRight().Text($"{i.LineTotal:#,##0.00}");
                            }
                        }
                    });

                    if (e.Preliminaries.Count > 0)
                    {
                        col.Item().PaddingTop(10).Text("Preliminaries").FontSize(11).Bold();
                        foreach (var p in e.Preliminaries)
                            col.Item().Row(rr => { rr.RelativeItem().Text($"{p.Description} ({p.Kind})"); rr.ConstantItem(90).AlignRight().Text(Money(p.ComputedTotal)); });
                    }

                    if (e.Markups.Count > 0)
                    {
                        col.Item().PaddingTop(10).Text("Markups").FontSize(11).Bold();
                        foreach (var mk in e.Markups)
                            col.Item().Row(rr => { rr.RelativeItem().Text($"{mk.Type} ({mk.Percentage}%)"); rr.ConstantItem(90).AlignRight().Text(Money(mk.ComputedAmount)); });
                    }

                    if (m.AreaRollup is { Areas.Count: > 0 })
                    {
                        col.Item().PaddingTop(10).Text("Cost by Area").FontSize(11).Bold();
                        foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas))
                            col.Item().Row(rr =>
                            {
                                var measure = node.Quantity > 0 ? $", {node.Quantity:0.####} {node.Unit}".TrimEnd() : "";
                                rr.RelativeItem().PaddingLeft(depth * 12)
                                  .Text($"{node.Name}  ({node.Kind}{(node.ItemCount > 0 ? $", {node.ItemCount} item(s)" : "")}{measure})").FontSize(8);
                                rr.ConstantItem(120).AlignRight().Text(
                                    node.CostPerUnit is decimal cpu
                                        ? $"{Money(node.RollupTotal)}  ({Money(cpu)}/{node.Unit ?? "unit"})"
                                        : Money(node.RollupTotal)).FontSize(8);
                            });
                        if (m.AreaRollup.UnassignedTotal > 0)
                            col.Item().Row(rr =>
                            {
                                rr.RelativeItem().Text("Unassigned").FontSize(8).FontColor(Colors.Grey.Darken1);
                                rr.ConstantItem(90).AlignRight().Text(Money(m.AreaRollup.UnassignedTotal)).FontSize(8);
                            });
                    }

                    col.Item().PaddingTop(14).AlignRight().Column(t =>
                    {
                        t.Item().Text($"Direct cost: {Money(e.DirectCost)}");
                        t.Item().Text($"Indirect (prelims): {Money(e.IndirectCost)}");
                        t.Item().Text($"Markups: {Money(e.MarkupCost)}");
                        t.Item().PaddingTop(3).Text($"BID PRICE: {Money(e.BidPrice)}").FontSize(13).Bold().FontColor(Colors.Teal.Darken2);
                        if (e.Fx is not null)
                            t.Item().Text($"≈ {e.Fx.SecondaryCurrency} {e.Fx.ConvertedBidPrice:#,##0.00}  (1 {e.Currency} = {e.Fx.Rate:#,##0.######} {e.Fx.SecondaryCurrency}{(e.Fx.Frozen ? ", frozen" : "")})")
                                .FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                });

                page.Footer().AlignCenter().Text(x => { x.Span("BidBuilder · "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
            });
        });

        return doc.GeneratePdf();
    }

    // ── Activities by unit (standalone) ─────────────────────────────────────
    public byte[] BuildActivitiesExcel(ExportModel m, string level = "detail")
    {
        level = NormalizeLevel(level);
        using var wb = new XLWorkbook();
        var title = ActivitiesTitle(level);
        var ws = wb.AddWorksheet(title);
        var r = ExcelHeader(ws, m, title);
        RenderActivities(ws, m, r, level);
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    /// <summary>Normalise the export grouping level from a query string ("area" / "subarea" / "detail").</summary>
    private static string NormalizeLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "area" => "area",
        "subarea" or "sub-area" => "subarea",
        "unit" => "unit",
        _ => "detail",
    };

    private static string ActivitiesTitle(string level) => level switch
    {
        "area" => "Activities by Area",
        "subarea" => "Activities by Sub-area",
        "unit" => "Activities by Unit (summary)",
        _ => "Activities by Unit",
    };

    /// <summary>
    /// Writes the Area → activities table (Material / Manpower / Total) starting at
    /// <paramref name="r"/>: brand header, shaded area rows, zebra-striped activities,
    /// and colour-coded data bars so material vs manpower magnitudes read at a glance.
    /// </summary>
    private static void RenderActivities(IXLWorksheet ws, ExportModel m, int r, string level = "detail")
    {
        var e = m.Estimate;
        var items = e.Sections.SelectMany(s => s.Items).Where(i => i.AreaId != null).ToList();
        decimal Comp(ItemBreakdown i, string code) => i.Components.FirstOrDefault(c => c.Code == code)?.Amount ?? 0;
        var rolled = RollupMatLab(m.AreaRollup?.Areas ?? new(), items);

        var firstHeader = level switch { "area" => "Area", "subarea" => "Sub-area", "unit" => "Unit", _ => "Area / Activity" };
        ws.Cell(r, 1).Value = firstHeader; ws.Cell(r, 2).Value = $"Material ({e.Currency})";
        ws.Cell(r, 3).Value = $"Manpower ({e.Currency})"; ws.Cell(r, 4).Value = $"Total ({e.Currency})";
        StyleHeader(ws.Range(r, 1, r, 4));
        ws.Range(r, 2, r, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
        int header = r; r++;

        // Summary modes keep the hierarchy down to the chosen level — Sub-area shows
        // Area → Sub-area; Unit shows Area → Sub-area → Unit — each row rolled up, with
        // NO activity detail. Rows are indented and styled by Level (Area bold on teal,
        // Sub-area bold on slate, Unit plain).
        if (level is "area" or "subarea" or "unit")
        {
            int maxRank = level switch { "area" => 0, "subarea" => 1, _ => 2 };
            int firstSum = r;
            if (m.AreaRollup is { Areas.Count: > 0 })
                foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas).Where(t => KindRank(t.Node.Kind) <= maxRank))
                {
                    var (mat, lab) = rolled.GetValueOrDefault(node.Id);
                    ws.Cell(r, 1).Value = new string(' ', depth * 4) + node.Name + "  (" + node.Kind + ")";
                    ws.Cell(r, 2).Value = mat;              ws.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 3).Value = lab;              ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 4).Value = node.RollupTotal; ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 4).Style.Font.SetFontColor(BrandDark);
                    var rng = ws.Range(r, 1, r, 4);
                    if (node.Kind == "Area") { rng.Style.Font.SetBold(); rng.Style.Fill.SetBackgroundColor(GroupFill); }
                    else if (node.Kind == "SubArea") { rng.Style.Font.SetBold(); rng.Style.Fill.SetBackgroundColor(depth <= 1 ? SubGroupFill : SubGroupFillLt); }
                    r++;
                }
            int lastSum = r - 1;
            if (lastSum >= firstSum)
            {
                ws.Range(firstSum, 2, lastSum, 2).AddConditionalFormat().DataBar(MatColor).LowestValue().HighestValue();
                ws.Range(firstSum, 3, lastSum, 3).AddConditionalFormat().DataBar(ManColor).LowestValue().HighestValue();
                ws.Range(firstSum, 4, lastSum, 4).AddConditionalFormat().DataBar(Brand).LowestValue().HighestValue();
                Box(ws.Range(header, 1, lastSum, 4));
            }
            ws.SheetView.FreezeRows(header);
            ws.Columns().AdjustToContents();
            return;
        }

        // Detail mode: the full Area → Sub-area → Unit tree with each unit's activities.
        var detailBlocks = new List<(int From, int To)>();   // contiguous activity rows, for data bars

        if (m.AreaRollup is { Areas.Count: > 0 })
            foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas))
            {
                var (mat, lab) = rolled.GetValueOrDefault(node.Id);
                ws.Cell(r, 1).Value = new string(' ', depth * 4) + node.Name + "  (" + node.Kind + ")";
                ws.Cell(r, 2).Value = mat;              ws.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 3).Value = lab;              ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 4).Value = node.RollupTotal; ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                var gr = ws.Range(r, 1, r, 4);
                gr.Style.Font.SetBold(); gr.Style.Fill.SetBackgroundColor(GroupFill);
                gr.Style.Font.SetFontColor(BrandDark);
                r++;
                int blockStart = r;
                bool band = false;
                foreach (var i in items.Where(x => x.AreaId == node.Id))
                {
                    ws.Cell(r, 1).Value = new string(' ', depth * 4 + 4) + i.Description;
                    ws.Cell(r, 2).Value = Comp(i, "MAT"); ws.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 3).Value = Comp(i, "LAB"); ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 4).Value = i.LineTotal;    ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 4).Style.Font.SetFontColor(BrandDark);
                    if (band) ws.Range(r, 1, r, 4).Style.Fill.SetBackgroundColor(BandFill);
                    band = !band;
                    r++;
                }
                if (r > blockStart) detailBlocks.Add((blockStart, r - 1));
            }
        int lastData = r - 1;
        // Data bars cover the activity (detail) rows only — the group subtotals would
        // otherwise dominate the scale and flatten the activity bars.
        foreach (var (from, to) in detailBlocks)
        {
            ws.Range(from, 2, to, 2).AddConditionalFormat().DataBar(MatColor).LowestValue().HighestValue();
            ws.Range(from, 3, to, 3).AddConditionalFormat().DataBar(ManColor).LowestValue().HighestValue();
            ws.Range(from, 4, to, 4).AddConditionalFormat().DataBar(Brand).LowestValue().HighestValue();
        }
        if (lastData > header) Box(ws.Range(header, 1, lastData, 4));
        ws.SheetView.FreezeRows(header);
        ws.Columns().AdjustToContents();
    }

    public byte[] BuildActivitiesCsv(ExportModel m, string level = "detail")
    {
        level = NormalizeLevel(level);
        var e = m.Estimate; var ci = CultureInfo.InvariantCulture;
        var items = e.Sections.SelectMany(s => s.Items).Where(i => i.AreaId != null).ToList();
        decimal Comp(ItemBreakdown i, string code) => i.Components.FirstOrDefault(c => c.Code == code)?.Amount ?? 0;
        var sb = new StringBuilder(); sb.Append('﻿');

        // Summary modes keep the hierarchy down to the chosen level (Sub-area ⇒ Area +
        // Sub-area; Unit ⇒ Area + Sub-area + Unit), rolled up, with no activity detail.
        if (level is "area" or "subarea" or "unit")
        {
            int maxRank = level switch { "area" => 0, "subarea" => 1, _ => 2 };
            var rolled = RollupMatLab(m.AreaRollup?.Areas ?? new(), items);
            sb.Append("Name,Level,Material,Manpower,Total,Currency\r\n");
            if (m.AreaRollup is { Areas.Count: > 0 })
                foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas).Where(t => KindRank(t.Node.Kind) <= maxRank))
                {
                    var (mat, lab) = rolled.GetValueOrDefault(node.Id);
                    sb.Append(CsvEsc(new string(' ', depth * 2) + node.Name)).Append(',')
                      .Append(CsvEsc(node.Kind)).Append(',')
                      .Append(mat.ToString("0.00", ci)).Append(',')
                      .Append(lab.ToString("0.00", ci)).Append(',')
                      .Append(node.RollupTotal.ToString("0.00", ci)).Append(',')
                      .Append(CsvEsc(e.Currency)).Append("\r\n");
                }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        sb.Append("Area,Level,Activity,Material,Manpower,Total,Currency\r\n");
        if (m.AreaRollup is { Areas.Count: > 0 })
            foreach (var (node, _) in FlattenAreas(m.AreaRollup.Areas))
                foreach (var i in items.Where(x => x.AreaId == node.Id))
                    sb.Append(CsvEsc(node.Name)).Append(',')
                      .Append(CsvEsc(node.Kind)).Append(',')
                      .Append(CsvEsc(i.Description)).Append(',')
                      .Append(Comp(i, "MAT").ToString("0.00", ci)).Append(',')
                      .Append(Comp(i, "LAB").ToString("0.00", ci)).Append(',')
                      .Append(i.LineTotal.ToString("0.00", ci)).Append(',')
                      .Append(CsvEsc(e.Currency)).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public byte[] BuildActivitiesPdf(ExportModel m, string level = "detail")
    {
        level = NormalizeLevel(level);
        var e = m.Estimate;
        var items = e.Sections.SelectMany(s => s.Items).Where(i => i.AreaId != null).ToList();
        decimal Comp(ItemBreakdown i, string code) => i.Components.FirstOrDefault(c => c.Code == code)?.Amount ?? 0;
        var rolled = RollupMatLab(m.AreaRollup?.Areas ?? new(), items);
        var firstHeader = level switch { "area" => "Area", "subarea" => "Sub-area", "unit" => "Unit", _ => "Area / Activity" };

        var doc = Document.Create(container => container.Page(page =>
        {
            page.Margin(36); page.Size(PageSizes.A4); page.DefaultTextStyle(x => x.FontSize(9));
            PdfHeader(page, m, ActivitiesTitle(level));
            page.Content().PaddingVertical(8).Table(table =>
            {
                table.ColumnsDefinition(c => { c.RelativeColumn(3); c.ConstantColumn(70); c.ConstantColumn(70); c.ConstantColumn(80); });
                table.Header(hd =>
                {
                    hd.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text(firstHeader).Bold();
                    hd.Cell().Background(Colors.Grey.Lighten2).Padding(3).AlignRight().Text("Material").Bold();
                    hd.Cell().Background(Colors.Grey.Lighten2).Padding(3).AlignRight().Text("Manpower").Bold();
                    hd.Cell().Background(Colors.Grey.Lighten2).Padding(3).AlignRight().Text("Total").Bold();
                });
                if (m.AreaRollup is { Areas.Count: > 0 })
                {
                    if (level is "area" or "subarea" or "unit")
                    {
                        int maxRank = level switch { "area" => 0, "subarea" => 1, _ => 2 };
                        foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas).Where(t => KindRank(t.Node.Kind) <= maxRank))
                        {
                            var (gMat, gLab) = rolled.GetValueOrDefault(node.Id);
                            table.Cell().PaddingLeft(depth * 12).Padding(3).Text($"{node.Name}  ({node.Kind})").SemiBold();
                            table.Cell().Padding(3).AlignRight().Text($"{gMat:#,##0.00}");
                            table.Cell().Padding(3).AlignRight().Text($"{gLab:#,##0.00}");
                            table.Cell().Padding(3).AlignRight().Text($"{node.RollupTotal:#,##0.00}");
                        }
                    }
                    else
                        foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas))
                        {
                            var (gMat, gLab) = rolled.GetValueOrDefault(node.Id);
                            table.Cell().Background(Colors.Grey.Lighten4).PaddingLeft(depth * 12).Padding(3).Text($"{node.Name} ({node.Kind})").SemiBold();
                            table.Cell().Background(Colors.Grey.Lighten4).Padding(3).AlignRight().Text($"{gMat:#,##0.00}").SemiBold();
                            table.Cell().Background(Colors.Grey.Lighten4).Padding(3).AlignRight().Text($"{gLab:#,##0.00}").SemiBold();
                            table.Cell().Background(Colors.Grey.Lighten4).Padding(3).AlignRight().Text($"{node.RollupTotal:#,##0.00}").SemiBold();
                            foreach (var i in items.Where(x => x.AreaId == node.Id))
                            {
                                table.Cell().PaddingLeft(depth * 12 + 8).Padding(3).Text(i.Description);
                                table.Cell().Padding(3).AlignRight().Text($"{Comp(i, "MAT"):#,##0.00}");
                                table.Cell().Padding(3).AlignRight().Text($"{Comp(i, "LAB"):#,##0.00}");
                                table.Cell().Padding(3).AlignRight().Text($"{i.LineTotal:#,##0.00}");
                            }
                        }
                }
            });
            page.Footer().AlignCenter().Text(x => { x.Span("BidBuilder · "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        }));
        return doc.GeneratePdf();
    }

    // ── Cost by area (standalone) ───────────────────────────────────────────
    public byte[] BuildCostByAreaExcel(ExportModel m, string level = "detail")
    {
        level = NormalizeLevel(level);
        using var wb = new XLWorkbook();
        var title = CostByAreaTitle(level);
        var ws = wb.AddWorksheet(title);
        var r = ExcelHeader(ws, m, title);
        RenderCostByArea(ws, m, r, level);
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    private static string CostByAreaTitle(string level) => level switch
    {
        "area" => "Cost by Area",
        "subarea" => "Cost by Sub-area",
        "unit" => "Cost by Unit",
        _ => "Cost by Area",
    };

    /// <summary>Flattened area nodes for a Cost-by-Area level: the full tree for "detail",
    /// or a flat (depth 0) list filtered to one Level for the summary modes.</summary>
    private static List<(AreaRollupRow Node, int Depth)> NodesForLevel(List<AreaRollupRow> areas, string level)
    {
        var flat = FlattenAreas(areas);
        if (level is "area" or "subarea" or "unit")
        {
            // Keep the hierarchy down to the chosen level (Sub-area ⇒ Area + Sub-area,
            // Unit ⇒ Area + Sub-area + Unit), preserving depth for indentation.
            int maxRank = level switch { "area" => 0, "subarea" => 1, _ => 2 };
            return flat.Where(t => KindRank(t.Node.Kind) <= maxRank).ToList();
        }
        return flat;
    }

    /// <summary>Depth rank of an area Level for level-limited summaries: Area &lt; Sub-area &lt; Unit.</summary>
    private static int KindRank(string kind) => kind switch { "Area" => 0, "SubArea" => 1, "Unit" => 2, _ => 3 };

    /// <summary>
    /// Writes the area roll-up table starting at <paramref name="r"/>: brand header,
    /// top-level areas shaded, nested levels zebra-striped, a teal data bar on the
    /// Total column, and a highlighted "Assigned to areas" total row.
    /// </summary>
    private static void RenderCostByArea(IXLWorksheet ws, ExportModel m, int r, string level = "detail")
    {
        var e = m.Estimate;
        var firstHeader = level switch { "subarea" => "Sub-area", "unit" => "Unit", _ => "Area" };
        ws.Cell(r, 1).Value = firstHeader; ws.Cell(r, 2).Value = "Level"; ws.Cell(r, 3).Value = "Items";
        ws.Cell(r, 4).Value = $"Total ({e.Currency})"; ws.Cell(r, 5).Value = "Measure"; ws.Cell(r, 6).Value = "Cost / unit";
        StyleHeader(ws.Range(r, 1, r, 6));
        ws.Range(r, 3, r, 4).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
        ws.Cell(r, 6).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
        int header = r; r++;
        int firstData = r, lastData = r - 1;
        var rollup = m.AreaRollup;
        if (rollup is { Areas.Count: > 0 })
        {
            var counts = RollupCounts(rollup.Areas);
            foreach (var (node, depth) in NodesForLevel(rollup.Areas, level))
            {
                ws.Cell(r, 1).Value = new string(' ', depth * 4) + node.Name;
                ws.Cell(r, 2).Value = node.Kind;
                ws.Cell(r, 3).Value = counts.GetValueOrDefault(node.Id);
                ws.Cell(r, 4).Value = node.RollupTotal; ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 4).Style.Font.SetFontColor(BrandDark);
                if (node.Quantity > 0)
                {
                    ws.Cell(r, 5).Value = $"{node.Quantity:0.####} {node.Unit}".Trim();
                    if (node.CostPerUnit is decimal cpu) { ws.Cell(r, 6).Value = cpu; ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00"; }
                }
                // Style by Level so the hierarchy reads at a glance: Areas bold on teal,
                // Sub-areas bold on slate (lighter the deeper they nest), Units plain.
                var rowRange = ws.Range(r, 1, r, 6);
                if (node.Kind == "Area")
                {
                    rowRange.Style.Font.SetBold();
                    rowRange.Style.Fill.SetBackgroundColor(GroupFill);
                }
                else if (node.Kind == "SubArea")
                {
                    rowRange.Style.Font.SetBold();
                    rowRange.Style.Fill.SetBackgroundColor(depth <= 1 ? SubGroupFill : SubGroupFillLt);
                }
                r++;
            }
            lastData = r - 1;

            ws.Cell(r, 1).Value = "Assigned to areas";
            ws.Cell(r, 4).Value = rollup.AssignedTotal; ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
            var ta = ws.Range(r, 1, r, 6);
            ta.Style.Font.SetBold().Font.SetFontColor(XLColor.White);
            ta.Style.Fill.SetBackgroundColor(Brand);
            r++;
            if (rollup.UnassignedTotal > 0)
            {
                ws.Cell(r, 1).Value = "Unassigned";
                ws.Cell(r, 4).Value = rollup.UnassignedTotal; ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                ws.Range(r, 1, r, 6).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
                r++;
            }
            if (lastData >= firstData)
                ws.Range(firstData, 4, lastData, 4).AddConditionalFormat().DataBar(Brand).LowestValue().HighestValue();
            Box(ws.Range(header, 1, r - 1, 6));
        }
        ws.SheetView.FreezeRows(header);
        ws.Columns().AdjustToContents();
    }

    public byte[] BuildCostByAreaCsv(ExportModel m, string level = "detail")
    {
        level = NormalizeLevel(level);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(); sb.Append('﻿');
        sb.Append("Area,Level,Items,Total,Measure,Cost per unit,Currency\r\n");
        if (m.AreaRollup is { Areas.Count: > 0 })
        {
            var counts = RollupCounts(m.AreaRollup.Areas);
            foreach (var (node, depth) in NodesForLevel(m.AreaRollup.Areas, level))
                sb.Append(CsvEsc(new string(' ', depth * 2) + node.Name)).Append(',')
                  .Append(CsvEsc(node.Kind)).Append(',')
                  .Append(counts.GetValueOrDefault(node.Id).ToString(ci)).Append(',')
                  .Append(node.RollupTotal.ToString("0.00", ci)).Append(',')
                  .Append(CsvEsc(node.Quantity > 0 ? $"{node.Quantity.ToString("0.####", ci)} {node.Unit}".Trim() : "")).Append(',')
                  .Append(node.CostPerUnit is decimal cpu ? cpu.ToString("0.00", ci) : "").Append(',')
                  .Append(CsvEsc(m.Estimate.Currency)).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public byte[] BuildCostByAreaPdf(ExportModel m, string level = "detail")
    {
        level = NormalizeLevel(level);
        var e = m.Estimate;
        string Money(decimal v) => $"{e.Currency} {v:#,##0.00}";
        var doc = Document.Create(container => container.Page(page =>
        {
            page.Margin(36); page.Size(PageSizes.A4); page.DefaultTextStyle(x => x.FontSize(9));
            PdfHeader(page, m, CostByAreaTitle(level));
            page.Content().PaddingVertical(8).Column(col =>
            {
                if (m.AreaRollup is { Areas.Count: > 0 })
                {
                    var counts = RollupCounts(m.AreaRollup.Areas);
                    foreach (var (node, depth) in NodesForLevel(m.AreaRollup.Areas, level))
                        col.Item().Row(rr =>
                        {
                            var count = counts.GetValueOrDefault(node.Id);
                            var measure = node.Quantity > 0 ? $", {node.Quantity:0.####} {node.Unit}".TrimEnd() : "";
                            rr.RelativeItem().PaddingLeft(depth * 12)
                              .Text($"{node.Name}  ({node.Kind}{(count > 0 ? $", {count} item(s)" : "")}{measure})").FontSize(8);
                            rr.ConstantItem(150).AlignRight().Text(
                                node.CostPerUnit is decimal cpu ? $"{Money(node.RollupTotal)}  ({Money(cpu)}/{node.Unit ?? "unit"})" : Money(node.RollupTotal)).FontSize(8);
                        });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                    col.Item().Row(rr => { rr.RelativeItem().Text("Assigned to areas").Bold(); rr.ConstantItem(150).AlignRight().Text(Money(m.AreaRollup.AssignedTotal)).Bold(); });
                    if (m.AreaRollup.UnassignedTotal > 0)
                        col.Item().Row(rr => { rr.RelativeItem().Text("Unassigned").FontColor(Colors.Grey.Darken1); rr.ConstantItem(150).AlignRight().Text(Money(m.AreaRollup.UnassignedTotal)); });
                }
            });
            page.Footer().AlignCenter().Text(x => { x.Span("BidBuilder · "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        }));
        return doc.GeneratePdf();
    }

    /// <summary>Compact document header (company + project + title) for a standalone Excel sheet.</summary>
    private static int ExcelHeader(IXLWorksheet ws, ExportModel m, string title)
    {
        ws.Cell("A1").Value = m.CompanyName;
        ws.Cell("A1").Style.Font.SetBold().Font.SetFontSize(15).Font.SetFontColor(Brand);
        int h = 2;
        ws.Cell(h, 1).Value = $"{title} — {m.ProjectCode}";
        ws.Cell(h, 1).Style.Font.SetBold().Font.SetFontSize(11).Font.SetFontColor(BrandDark); h++;
        ws.Cell(h, 1).Value = m.ProjectName; ws.Cell(h, 1).Style.Font.SetBold(); h++;
        ws.Cell(h, 1).Value = $"Client: {m.Client ?? "—"}    Location: {m.Location ?? "—"}"; ws.Cell(h, 1).Style.Font.FontColor = XLColor.Gray; h++;
        ws.Cell(h, 1).Value = $"Generated: {m.GeneratedOn}    Currency: {m.Estimate.Currency}"; ws.Cell(h, 1).Style.Font.FontColor = XLColor.Gray; h++;
        return h + 1; // leave a spacer row before the table
    }

    /// <summary>Compact PDF document header (company + project + title).</summary>
    // ── Bid submission letter ───────────────────────────────────────────────
    /// <summary>
    /// A formal tender cover letter on the company letterhead: addressed to the client,
    /// stating the tender sum (figures + words) and validity, signed off. Editable fields
    /// (recipient, signatory, validity, optional note) come from <paramref name="o"/>;
    /// everything else is pulled from the project / estimate / company profile.
    /// </summary>
    public byte[] BuildBidLetterPdf(ExportModel m, BidLetterOptions o)
    {
        var e = m.Estimate;
        var recipient   = Clean(o.Recipient) ?? m.Client ?? "Sir/Madam";
        var validity    = o.ValidityDays is > 0 ? o.ValidityDays.Value : 90;
        var reference   = Clean(o.Reference) ?? m.ProjectCode;
        var signatory   = Clean(o.Signatory) ?? "____________________";
        var amount      = $"{e.Currency} {e.BidPrice:#,##0.00}";
        var amountWords = MoneyInWords.Money(e.BidPrice, e.Currency);

        var doc = Document.Create(container => container.Page(page =>
        {
            page.Margin(50); page.Size(PageSizes.A4); page.DefaultTextStyle(x => x.FontSize(10));

            // Letterhead
            page.Header().Column(h =>
            {
                h.Item().Row(top =>
                {
                    top.RelativeItem().Column(left =>
                    {
                        left.Item().Text(m.CompanyName).FontSize(16).Bold().FontColor(Colors.Teal.Darken2);
                        if (!string.IsNullOrWhiteSpace(m.CompanyAddress)) left.Item().Text(m.CompanyAddress).FontSize(8).FontColor(Colors.Grey.Darken1);
                        if (!string.IsNullOrWhiteSpace(m.CompanyContact)) left.Item().Text(m.CompanyContact).FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                    if (m.LogoBytes is { Length: > 0 })
                        top.ConstantItem(130).MaxHeight(48).AlignRight().AlignTop().Image(m.LogoBytes).FitArea();
                });
                h.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
            });

            page.Content().PaddingVertical(14).Column(col =>
            {
                col.Spacing(10);
                col.Item().AlignRight().Text(m.GeneratedOn).FontSize(9).FontColor(Colors.Grey.Darken1);

                col.Item().Column(to =>
                {
                    to.Item().Text("To:").FontSize(9).FontColor(Colors.Grey.Darken1);
                    to.Item().Text(recipient).Bold();
                    if (!string.IsNullOrWhiteSpace(o.RecipientTitle)) to.Item().Text(o.RecipientTitle!.Trim()).FontSize(9);
                    if (!string.IsNullOrWhiteSpace(m.Client) && !string.Equals(m.Client, recipient, StringComparison.OrdinalIgnoreCase))
                        to.Item().Text(m.Client!).FontSize(9);
                    if (!string.IsNullOrWhiteSpace(m.Location)) to.Item().Text(m.Location!).FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                col.Item().Text($"Ref: {reference}").FontSize(9).FontColor(Colors.Grey.Darken1);
                col.Item().Text($"Subject: Bid Submission — {m.ProjectName}").Bold().FontSize(11);

                col.Item().Text($"Dear {recipient},");

                col.Item().Text(
                    $"We are pleased to submit our bid for the above-referenced project, {m.ProjectName} ({m.ProjectCode})"
                    + (string.IsNullOrWhiteSpace(m.Location) ? "" : $", located at {m.Location}") + ".")
                    .LineHeight(1.4f);

                col.Item().Text(t =>
                {
                    t.Span("Having reviewed the tender documents, we hereby offer to execute the works described therein for the total tender sum of ");
                    t.Span(amount).Bold();
                    t.Span($" ({amountWords}).");
                });

                if (!string.IsNullOrWhiteSpace(o.Note))
                    col.Item().Text(o.Note!.Trim()).LineHeight(1.4f);

                col.Item().Text($"This offer shall remain valid for {validity} days from the date of this letter.").LineHeight(1.4f);
                col.Item().Text("We trust our submission meets your requirements and look forward to your favourable consideration.").LineHeight(1.4f);

                col.Item().PaddingTop(18).Text("Yours faithfully,");
                col.Item().PaddingTop(24).Text(m.CompanyName).Bold();
                col.Item().Text(signatory);
                if (!string.IsNullOrWhiteSpace(o.SignatoryTitle)) col.Item().Text(o.SignatoryTitle!.Trim()).FontSize(9).FontColor(Colors.Grey.Darken1);
            });

            page.Footer().AlignCenter().Text(x => { x.Span("BidBuilder · "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        }));
        return doc.GeneratePdf();
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static void PdfHeader(PageDescriptor page, ExportModel m, string title)
    {
        page.Header().Column(col =>
        {
            col.Item().Row(top =>
            {
                top.RelativeItem().Column(left =>
                {
                    left.Item().Text(m.CompanyName).FontSize(16).Bold();
                    if (!string.IsNullOrWhiteSpace(m.CompanyContact))
                        left.Item().Text(m.CompanyContact).FontSize(8).FontColor(Colors.Grey.Darken1);
                });
                if (m.LogoBytes is { Length: > 0 })
                    top.ConstantItem(140).MaxHeight(50).AlignRight().AlignTop().Image(m.LogoBytes).FitArea();
            });
            col.Item().PaddingTop(2).Text($"{title} — {m.ProjectCode}").FontSize(11).FontColor(Colors.Grey.Darken2);
            col.Item().Text(m.ProjectName).Bold();
            col.Item().Text($"Client: {m.Client ?? "—"}   Location: {m.Location ?? "—"}   Generated: {m.GeneratedOn}").FontSize(8).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static string CsvEsc(string? v)
    {
        v ??= "";
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || (v.Length > 0 && (v[0] == ' ' || v[^1] == ' ')))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    /// <summary>Depth-first flatten of the area roll-up into (node, depth) in tree order.</summary>
    /// <summary>
    /// Material + Manpower totals per area, rolled UP the tree (each area carries the sum
    /// of its own activities plus every descendant's), keyed by area id. Mirrors how
    /// AreaRollupRow.RollupTotal aggregates the grand total, so a group row can show a
    /// Material / Manpower / Total subtotal for everything beneath it.
    /// </summary>
    private static Dictionary<int, (decimal Mat, decimal Lab)> RollupMatLab(
        List<AreaRollupRow> areas, List<ItemBreakdown> items)
    {
        decimal Comp(ItemBreakdown i, string code) => i.Components.FirstOrDefault(c => c.Code == code)?.Amount ?? 0;
        var directMat = new Dictionary<int, decimal>();
        var directLab = new Dictionary<int, decimal>();
        foreach (var i in items.Where(x => x.AreaId != null))
        {
            int aid = i.AreaId!.Value;
            directMat[aid] = directMat.GetValueOrDefault(aid) + Comp(i, "MAT");
            directLab[aid] = directLab.GetValueOrDefault(aid) + Comp(i, "LAB");
        }
        var childrenOf = areas.Where(a => a.ParentAreaId is not null)
            .GroupBy(a => a.ParentAreaId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        var memo = new Dictionary<int, (decimal, decimal)>();
        (decimal Mat, decimal Lab) Roll(int id)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            var mat = directMat.GetValueOrDefault(id);
            var lab = directLab.GetValueOrDefault(id);
            if (childrenOf.TryGetValue(id, out var kids))
                foreach (var k in kids) { var (cm, cl) = Roll(k); mat += cm; lab += cl; }
            return memo[id] = (mat, lab);
        }
        foreach (var a in areas) Roll(a.Id);
        return memo;
    }

    /// <summary>
    /// Item counts rolled UP the area tree (each area carries its own directly-assigned
    /// items plus every descendant's), keyed by area id — so a Sub-area / Area row shows
    /// the total number of priced items beneath it instead of its (usually zero) direct count.
    /// </summary>
    private static Dictionary<int, int> RollupCounts(List<AreaRollupRow> areas)
    {
        var direct = areas.ToDictionary(a => a.Id, a => a.ItemCount);
        var childrenOf = areas.Where(a => a.ParentAreaId is not null)
            .GroupBy(a => a.ParentAreaId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        var memo = new Dictionary<int, int>();
        int Roll(int id)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            var n = direct.GetValueOrDefault(id);
            if (childrenOf.TryGetValue(id, out var kids)) foreach (var k in kids) n += Roll(k);
            return memo[id] = n;
        }
        foreach (var a in areas) Roll(a.Id);
        return memo;
    }

    private static List<(AreaRollupRow Node, int Depth)> FlattenAreas(List<AreaRollupRow> areas)
    {
        var result = new List<(AreaRollupRow, int)>();
        void Walk(int? parent, int depth)
        {
            foreach (var n in areas.Where(a => a.ParentAreaId == parent))
            {
                result.Add((n, depth));
                Walk(n.Id, depth + 1);
            }
        }
        Walk(null, 0);
        return result;
    }
}
