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

/// <summary>
/// Renders an estimate as a priced-BOQ + bid-summary in Excel (ClosedXML) and
/// PDF (QuestPDF) — the client-facing deliverable.
/// </summary>
public class ExportService
{
    // ── Excel ──────────────────────────────────────────────────────────────
    public byte[] BuildExcel(ExportModel m)
    {
        var e = m.Estimate;
        using var wb = new XLWorkbook();

        // Summary sheet
        var sum = wb.AddWorksheet("Bid Summary");
        sum.Cell("A1").Value = m.CompanyName; sum.Cell("A1").Style.Font.SetBold().Font.FontSize = 16;

        if (m.LogoBytes is { Length: > 0 })
        {
            // NOT disposed here on purpose: ClosedXML reads the picture stream lazily
            // at SaveAs, so the stream must outlive this block (MemoryStream has no
            // unmanaged resources to leak).
            var logoStream = new MemoryStream(m.LogoBytes);
            var pic = sum.AddPicture(logoStream, "logo");
            const double maxH = 64; // px — keep the header compact, preserve aspect ratio
            if (pic.OriginalHeight > maxH) pic.Scale(maxH / pic.OriginalHeight);
            pic.MoveTo(sum.Cell("E1"));
        }
        int h = 2;
        if (!string.IsNullOrWhiteSpace(m.CompanyAddress)) { sum.Cell(h, 1).Value = m.CompanyAddress; sum.Cell(h, 1).Style.Font.FontColor = XLColor.Gray; h++; }
        if (!string.IsNullOrWhiteSpace(m.CompanyContact)) { sum.Cell(h, 1).Value = m.CompanyContact; sum.Cell(h, 1).Style.Font.FontColor = XLColor.Gray; h++; }
        h++; // spacer row
        sum.Cell(h++, 1).Value = $"Bid Summary — {m.ProjectCode}";
        sum.Cell(h, 1).Value = m.ProjectName; sum.Cell(h, 1).Style.Font.SetBold(); h++;
        sum.Cell(h++, 1).Value = $"Client: {m.Client ?? "—"}    Location: {m.Location ?? "—"}";
        sum.Cell(h++, 1).Value = $"Generated: {m.GeneratedOn}    Currency: {e.Currency}";

        int r = h + 1;
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
        Line("BID PRICE", e.BidPrice, bold: true);
        if (e.Fx is not null)
        {
            sum.Cell(r, 1).Value = $"≈ in {e.Fx.SecondaryCurrency}  (1 {e.Currency} = {e.Fx.Rate:#,##0.######} {e.Fx.SecondaryCurrency}{(e.Fx.Frozen ? ", frozen" : "")})";
            sum.Cell(r, 1).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
            sum.Cell(r, 2).Value = e.Fx.ConvertedBidPrice;
            sum.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
            sum.Cell(r, 2).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
            r++;
        }
        sum.Columns().AdjustToContents();

        // Priced BOQ sheet
        var boq = wb.AddWorksheet("Priced BOQ");
        var head = boq.Row(1);
        boq.Cell(1, 1).Value = "Code"; boq.Cell(1, 2).Value = "Description"; boq.Cell(1, 3).Value = "Unit";
        boq.Cell(1, 4).Value = "Qty"; boq.Cell(1, 5).Value = "Rate"; boq.Cell(1, 6).Value = "Total";
        head.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

        int row = 2;
        foreach (var s in e.Sections)
        {
            boq.Cell(row, 1).Value = s.Code;
            boq.Cell(row, 2).Value = s.Title; boq.Range(row, 2, row, 5).Style.Font.SetBold();
            boq.Cell(row, 6).Value = s.SectionTotal; boq.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            boq.Row(row).Style.Fill.SetBackgroundColor(XLColor.FromArgb(0xF1, 0xF5, 0xF9));
            boq.Cell(row, 6).Style.Font.SetBold();
            row++;
            foreach (var i in s.Items)
            {
                boq.Cell(row, 1).Value = i.ItemCode;
                boq.Cell(row, 2).Value = i.Description;
                boq.Cell(row, 3).Value = i.Unit;
                boq.Cell(row, 4).Value = i.Quantity; boq.Cell(row, 4).Style.NumberFormat.Format = "#,##0.####";
                boq.Cell(row, 5).Value = i.UnitRate;  boq.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                boq.Cell(row, 6).Value = i.LineTotal; boq.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
                row++;
                // Unit-rate build-up detail (Material/Labor/… + Waste%/Overheads%).
                foreach (var comp in i.Components)
                {
                    var detail = comp.CalcKind == "Percent" ? $" ({comp.Value:#,##0.##}%)"
                        : comp.Quantity is not null && comp.Rate is not null ? $" ({comp.Quantity:#,##0.####} × {comp.Rate:#,##0.##})"
                        : "";
                    boq.Cell(row, 2).Value = $"    • {comp.Name}{detail}";
                    boq.Cell(row, 5).Value = comp.Amount; boq.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                    boq.Row(row).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
                    row++;
                }
            }
        }
        boq.Columns().AdjustToContents();

        // Cost-by-Area sheet (line totals escalated up the area tree)
        if (m.AreaRollup is { Areas.Count: > 0 })
        {
            var ar = wb.AddWorksheet("Cost by Area");
            ar.Cell(1, 1).Value = "Area"; ar.Cell(1, 2).Value = "Level"; ar.Cell(1, 3).Value = "Items";
            ar.Cell(1, 4).Value = "Total"; ar.Cell(1, 5).Value = "Measure"; ar.Cell(1, 6).Value = "Cost / unit";
            ar.Row(1).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);
            int ai = 2;
            foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas))
            {
                ar.Cell(ai, 1).Value = new string(' ', depth * 4) + node.Name;
                ar.Cell(ai, 2).Value = node.Kind;
                ar.Cell(ai, 3).Value = node.ItemCount;
                ar.Cell(ai, 4).Value = node.RollupTotal; ar.Cell(ai, 4).Style.NumberFormat.Format = "#,##0.00";
                if (node.Quantity > 0)
                {
                    ar.Cell(ai, 5).Value = $"{node.Quantity:0.####} {node.Unit}".Trim();
                    if (node.CostPerUnit is decimal cpu) { ar.Cell(ai, 6).Value = cpu; ar.Cell(ai, 6).Style.NumberFormat.Format = "#,##0.00"; }
                }
                ai++;
            }
            ar.Cell(ai, 1).Value = "Assigned to areas"; ar.Cell(ai, 1).Style.Font.SetBold();
            ar.Cell(ai, 4).Value = m.AreaRollup.AssignedTotal; ar.Cell(ai, 4).Style.NumberFormat.Format = "#,##0.00"; ar.Cell(ai, 4).Style.Font.SetBold();
            ai++;
            if (m.AreaRollup.UnassignedTotal > 0)
            {
                ar.Cell(ai, 1).Value = "Unassigned";
                ar.Cell(ai, 4).Value = m.AreaRollup.UnassignedTotal; ar.Cell(ai, 4).Style.NumberFormat.Format = "#,##0.00";
            }
            ar.Columns().AdjustToContents();
        }

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

        static string Esc(string? v)
        {
            v ??= "";
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
                || (v.Length > 0 && (v[0] == ' ' || v[^1] == ' ')))
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        sb.Append("Section Code,Section Title,Item Code,Description,Unit,Qty,Unit Rate,Line Total,Currency\r\n");
        foreach (var s in e.Sections)
            foreach (var i in s.Items)
                sb.Append(Esc(s.Code)).Append(',')
                  .Append(Esc(s.Title)).Append(',')
                  .Append(Esc(i.ItemCode)).Append(',')
                  .Append(Esc(i.Description)).Append(',')
                  .Append(Esc(i.Unit)).Append(',')
                  .Append(i.Quantity.ToString("0.####", ci)).Append(',')
                  .Append(i.UnitRate.ToString("0.00", ci)).Append(',')
                  .Append(i.LineTotal.ToString("0.00", ci)).Append(',')
                  .Append(Esc(e.Currency)).Append("\r\n");

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
    public byte[] BuildActivitiesExcel(ExportModel m)
    {
        var e = m.Estimate;
        var items = e.Sections.SelectMany(s => s.Items).Where(i => i.AreaId != null).ToList();
        decimal Comp(ItemBreakdown i, string code) => i.Components.FirstOrDefault(c => c.Code == code)?.Amount ?? 0;

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Activities by Unit");
        var r = ExcelHeader(ws, m, "Activities by Unit");
        ws.Cell(r, 1).Value = "Area / Activity"; ws.Cell(r, 2).Value = "Material"; ws.Cell(r, 3).Value = "Manpower"; ws.Cell(r, 4).Value = "Total";
        ws.Row(r).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray); r++;
        if (m.AreaRollup is { Areas.Count: > 0 })
            foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas))
            {
                ws.Cell(r, 1).Value = new string(' ', depth * 4) + node.Name + "  (" + node.Kind + ")";
                ws.Cell(r, 1).Style.Font.SetBold(); r++;
                foreach (var i in items.Where(x => x.AreaId == node.Id))
                {
                    ws.Cell(r, 1).Value = new string(' ', depth * 4 + 4) + i.Description;
                    ws.Cell(r, 2).Value = Comp(i, "MAT"); ws.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 3).Value = Comp(i, "LAB"); ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(r, 4).Value = i.LineTotal;    ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                    r++;
                }
            }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    public byte[] BuildActivitiesCsv(ExportModel m)
    {
        var e = m.Estimate; var ci = CultureInfo.InvariantCulture;
        var items = e.Sections.SelectMany(s => s.Items).Where(i => i.AreaId != null).ToList();
        decimal Comp(ItemBreakdown i, string code) => i.Components.FirstOrDefault(c => c.Code == code)?.Amount ?? 0;
        var sb = new StringBuilder(); sb.Append('﻿');
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

    public byte[] BuildActivitiesPdf(ExportModel m)
    {
        var e = m.Estimate;
        var items = e.Sections.SelectMany(s => s.Items).Where(i => i.AreaId != null).ToList();
        decimal Comp(ItemBreakdown i, string code) => i.Components.FirstOrDefault(c => c.Code == code)?.Amount ?? 0;

        var doc = Document.Create(container => container.Page(page =>
        {
            page.Margin(36); page.Size(PageSizes.A4); page.DefaultTextStyle(x => x.FontSize(9));
            PdfHeader(page, m, "Activities by Unit");
            page.Content().PaddingVertical(8).Table(table =>
            {
                table.ColumnsDefinition(c => { c.RelativeColumn(3); c.ConstantColumn(70); c.ConstantColumn(70); c.ConstantColumn(80); });
                table.Header(hd =>
                {
                    hd.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text("Area / Activity").Bold();
                    hd.Cell().Background(Colors.Grey.Lighten2).Padding(3).AlignRight().Text("Material").Bold();
                    hd.Cell().Background(Colors.Grey.Lighten2).Padding(3).AlignRight().Text("Manpower").Bold();
                    hd.Cell().Background(Colors.Grey.Lighten2).Padding(3).AlignRight().Text("Total").Bold();
                });
                if (m.AreaRollup is { Areas.Count: > 0 })
                    foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas))
                    {
                        table.Cell().ColumnSpan(4).Background(Colors.Grey.Lighten4).PaddingLeft(depth * 12).Padding(3).Text($"{node.Name} ({node.Kind})").SemiBold();
                        foreach (var i in items.Where(x => x.AreaId == node.Id))
                        {
                            table.Cell().PaddingLeft(depth * 12 + 8).Padding(3).Text(i.Description);
                            table.Cell().Padding(3).AlignRight().Text($"{Comp(i, "MAT"):#,##0.00}");
                            table.Cell().Padding(3).AlignRight().Text($"{Comp(i, "LAB"):#,##0.00}");
                            table.Cell().Padding(3).AlignRight().Text($"{i.LineTotal:#,##0.00}");
                        }
                    }
            });
            page.Footer().AlignCenter().Text(x => { x.Span("BidBuilder · "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        }));
        return doc.GeneratePdf();
    }

    // ── Cost by area (standalone) ───────────────────────────────────────────
    public byte[] BuildCostByAreaExcel(ExportModel m)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Cost by Area");
        var r = ExcelHeader(ws, m, "Cost by Area");
        ws.Cell(r, 1).Value = "Area"; ws.Cell(r, 2).Value = "Level"; ws.Cell(r, 3).Value = "Items";
        ws.Cell(r, 4).Value = "Total"; ws.Cell(r, 5).Value = "Measure"; ws.Cell(r, 6).Value = "Cost / unit";
        ws.Row(r).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray); r++;
        var rollup = m.AreaRollup;
        if (rollup is { Areas.Count: > 0 })
        {
            foreach (var (node, depth) in FlattenAreas(rollup.Areas))
            {
                ws.Cell(r, 1).Value = new string(' ', depth * 4) + node.Name;
                ws.Cell(r, 2).Value = node.Kind;
                ws.Cell(r, 3).Value = node.ItemCount;
                ws.Cell(r, 4).Value = node.RollupTotal; ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                if (node.Quantity > 0)
                {
                    ws.Cell(r, 5).Value = $"{node.Quantity:0.####} {node.Unit}".Trim();
                    if (node.CostPerUnit is decimal cpu) { ws.Cell(r, 6).Value = cpu; ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00"; }
                }
                r++;
            }
            ws.Cell(r, 1).Value = "Assigned to areas"; ws.Cell(r, 1).Style.Font.SetBold();
            ws.Cell(r, 4).Value = rollup.AssignedTotal; ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(r, 4).Style.Font.SetBold(); r++;
            if (rollup.UnassignedTotal > 0)
            {
                ws.Cell(r, 1).Value = "Unassigned";
                ws.Cell(r, 4).Value = rollup.UnassignedTotal; ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00"; r++;
            }
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    public byte[] BuildCostByAreaCsv(ExportModel m)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(); sb.Append('﻿');
        sb.Append("Area,Level,Items,Total,Measure,Cost per unit,Currency\r\n");
        if (m.AreaRollup is { Areas.Count: > 0 })
            foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas))
                sb.Append(CsvEsc(new string(' ', depth * 2) + node.Name)).Append(',')
                  .Append(CsvEsc(node.Kind)).Append(',')
                  .Append(node.ItemCount.ToString(ci)).Append(',')
                  .Append(node.RollupTotal.ToString("0.00", ci)).Append(',')
                  .Append(CsvEsc(node.Quantity > 0 ? $"{node.Quantity.ToString("0.####", ci)} {node.Unit}".Trim() : "")).Append(',')
                  .Append(node.CostPerUnit is decimal cpu ? cpu.ToString("0.00", ci) : "").Append(',')
                  .Append(CsvEsc(m.Estimate.Currency)).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public byte[] BuildCostByAreaPdf(ExportModel m)
    {
        var e = m.Estimate;
        string Money(decimal v) => $"{e.Currency} {v:#,##0.00}";
        var doc = Document.Create(container => container.Page(page =>
        {
            page.Margin(36); page.Size(PageSizes.A4); page.DefaultTextStyle(x => x.FontSize(9));
            PdfHeader(page, m, "Cost by Area");
            page.Content().PaddingVertical(8).Column(col =>
            {
                if (m.AreaRollup is { Areas.Count: > 0 })
                {
                    foreach (var (node, depth) in FlattenAreas(m.AreaRollup.Areas))
                        col.Item().Row(rr =>
                        {
                            var measure = node.Quantity > 0 ? $", {node.Quantity:0.####} {node.Unit}".TrimEnd() : "";
                            rr.RelativeItem().PaddingLeft(depth * 12)
                              .Text($"{node.Name}  ({node.Kind}{(node.ItemCount > 0 ? $", {node.ItemCount} item(s)" : "")}{measure})").FontSize(8);
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
        ws.Cell("A1").Value = m.CompanyName; ws.Cell("A1").Style.Font.SetBold().Font.FontSize = 14;
        int h = 2;
        ws.Cell(h++, 1).Value = $"{title} — {m.ProjectCode}";
        ws.Cell(h, 1).Value = m.ProjectName; ws.Cell(h, 1).Style.Font.SetBold(); h++;
        ws.Cell(h++, 1).Value = $"Client: {m.Client ?? "—"}    Location: {m.Location ?? "—"}";
        ws.Cell(h++, 1).Value = $"Generated: {m.GeneratedOn}    Currency: {m.Estimate.Currency}";
        return h + 1; // leave a spacer row before the table
    }

    /// <summary>Compact PDF document header (company + project + title).</summary>
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
