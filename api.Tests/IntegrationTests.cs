using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>Small helpers to resolve seeded ids and create scratch fixtures over HTTP.</summary>
internal static class Api
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<int> ProjectIdAsync(HttpClient c)
    {
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/projects");
        return arr.EnumerateArray().First(p => p.GetProperty("code").GetString() == "PRJ-2026-001").GetProperty("id").GetInt32();
    }

    public static async Task<int> FirstEstimateIdAsync(HttpClient c, int pid)
    {
        var arr = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/estimates");
        return arr.EnumerateArray().First().GetProperty("id").GetInt32();
    }

    public static async Task<int> NewEstimateAsync(HttpClient c, int pid, string title)
    {
        var bd = await (await c.PostAsJsonAsync($"/api/projects/{pid}/estimates", new { title })).Json();
        return bd.GetProperty("id").GetInt32();
    }

    public static async Task<int> AddSectionAsync(HttpClient c, int eid, string code)
    {
        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections", new { code, title = "T", sortOrder = 0 })).Json();
        return bd.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("code").GetString() == code).GetProperty("id").GetInt32();
    }

    public static async Task<int> TypeIdAsync(HttpClient c, string code)
    {
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/cost-components");
        return arr.EnumerateArray().First(t => t.GetProperty("code").GetString() == code).GetProperty("id").GetInt32();
    }

    /// <summary>PUT estimate meta with a fresh If-Match (read the current rowVersion first).</summary>
    public static async Task<HttpResponseMessage> PutMetaAsync(HttpClient c, int eid, object body)
    {
        var bd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        var rv = bd.GetProperty("rowVersion").GetString();
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/estimates/{eid}") { Content = JsonContent.Create(body) };
        req.Headers.TryAddWithoutValidation("If-Match", rv);
        return await c.SendAsync(req);
    }

    /// <summary>An in-memory .xlsx whose first worksheet is filled by <paramref name="fill"/>.</summary>
    public static byte[] Xlsx(Action<IXLWorksheet> fill)
    {
        using var wb = new XLWorkbook();
        fill(wb.AddWorksheet("BOQ"));
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Wrap workbook bytes as a multipart upload under the "file" field.</summary>
    public static MultipartFormDataContent FileForm(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(content, "file", "boq.xlsx");
        return form;
    }
}

[Collection("api")]
public class HealthAndAuthTests(ApiFixture fx)
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        var r = await fx.Client().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Projects_require_authentication()
    {
        var r = await fx.Client().GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Admin_sees_seeded_sample_project()
    {
        var c = await fx.AdminClientAsync();
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/projects");
        var codes = arr.EnumerateArray().Select(p => p.GetProperty("code").GetString());
        Assert.Contains("PRJ-2026-001", codes);
    }

    [Fact]
    public async Task Admin_permissions_are_all_true()
    {
        var c = await fx.AdminClientAsync();
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/permissions");
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
    }
}

[Collection("api")]
public class CostBuildupTests(ApiFixture fx)
{
    [Fact]
    public async Task Default_cost_types_are_seeded()
    {
        var c = await fx.AdminClientAsync();
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/cost-components");
        var codes = arr.EnumerateArray().Select(t => t.GetProperty("code").GetString()).ToList();
        Assert.Contains("MAT", codes);
        Assert.Contains("WST", codes);
    }

    [Fact]
    public async Task Buildup_computes_rate_and_line_total()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "buildup");
        var sid = await Api.AddSectionAsync(c, eid, "B1");
        var mat = await Api.TypeIdAsync(c, "MAT");
        var wst = await Api.TypeIdAsync(c, "WST");

        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Wall", unit = "m2", quantity = 2, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[] { new { typeId = mat, value = 100 }, new { typeId = wst, value = 10 } },
            areaId = (int?)null,
        })).Json();

        var item = bd.GetProperty("sections")[0].GetProperty("items")[0];
        Assert.Equal(110m, item.GetProperty("unitRate").GetDecimal());   // 100 + 10% of 100
        Assert.Equal(220m, item.GetProperty("lineTotal").GetDecimal());  // ×2
        Assert.Equal(2, item.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task Quantity_times_rate_components_compute_amounts()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "qtyrate");
        var sid = await Api.AddSectionAsync(c, eid, "QR");
        var mat = await Api.TypeIdAsync(c, "MAT");
        var lab = await Api.TypeIdAsync(c, "LAB");
        var wst = await Api.TypeIdAsync(c, "WST");

        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Fitout", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[]
            {
                new { typeId = mat, value = 0, quantity = 100m, rate = 50m },   // material 100 × 50 = 5000
                new { typeId = lab, value = 0, quantity = 40m, rate = 50m },    // manpower 40 h × 50 = 2000
                new { typeId = wst, value = 10m },                              // 10% of 7000 = 700
            },
            areaId = (int?)null,
        })).Json();

        var item = bd.GetProperty("sections")[0].GetProperty("items")[0];
        Assert.Equal(7700m, item.GetProperty("unitRate").GetDecimal());    // 5000 + 2000 + 700
        Assert.Equal(7700m, item.GetProperty("lineTotal").GetDecimal());   // × qty 1
        var matLine = item.GetProperty("components").EnumerateArray().First(x => x.GetProperty("code").GetString() == "MAT");
        Assert.Equal(100m, matLine.GetProperty("quantity").GetDecimal());
        Assert.Equal(50m, matLine.GetProperty("rate").GetDecimal());
        Assert.Equal(5000m, matLine.GetProperty("amount").GetDecimal());

        // negative rate rejected
        var bad = await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "x", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 1,
            components = new object[] { new { typeId = mat, value = 0, quantity = 1m, rate = -5m } }, areaId = (int?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Duplicate_code_and_builtin_delete_are_conflicts()
    {
        var c = await fx.AdminClientAsync();
        var dup = await c.PostAsJsonAsync("/api/cost-components", new { code = "MAT", name = "dup", calcKind = "Amount", sortOrder = 9, isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var matId = await Api.TypeIdAsync(c, "MAT");
        var del = await c.DeleteAsync($"/api/cost-components/{matId}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
    }
}

[Collection("api")]
public class AreaRollupTests(ApiFixture fx)
{
    [Fact]
    public async Task Item_totals_escalate_up_the_area_tree()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas", new { name = "Bldg", code = "B", kind = "Area", parentAreaId = (int?)null, sortOrder = 0 })).Json();
        int aid = area.GetProperty("id").GetInt32();
        var unit = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas", new { name = "Unit", kind = "Unit", parentAreaId = aid, sortOrder = 0 })).Json();
        int uid = unit.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "area");
        var sid = await Api.AddSectionAsync(c, eid, "A1");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "x", unit = "no", quantity = 3, assemblyId = (int?)null, unitRate = 200, sortOrder = 0,
            components = (object?)null, areaId = uid,
        });

        var roll = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/areas-rollup");
        var node = roll.GetProperty("areas").EnumerateArray().First(x => x.GetProperty("id").GetInt32() == aid);
        Assert.Equal(600m, node.GetProperty("rollupTotal").GetDecimal());     // 200 × 3 escalated to the parent
        Assert.Equal(600m, roll.GetProperty("assignedTotal").GetDecimal());

        // delete guard: parent has a child
        var del = await c.DeleteAsync($"/api/projects/{pid}/areas/{aid}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
    }

    [Fact]
    public async Task Area_measure_yields_cost_per_unit()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        // an area with a 100 m² measure
        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Floor", code = "F", kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 100m, unit = "m²" })).Json();
        int aid = area.GetProperty("id").GetInt32();
        Assert.Equal(100m, area.GetProperty("quantity").GetDecimal());
        Assert.Equal("m²", area.GetProperty("unit").GetString());

        // 5000 of cost assigned to it → 50 / m²
        var eid = await Api.NewEstimateAsync(c, pid, "measure");
        var sid = await Api.AddSectionAsync(c, eid, "M1");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "slab", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 5000, sortOrder = 0,
            components = (object?)null, areaId = aid,
        });

        var roll = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}/areas-rollup");
        var node = roll.GetProperty("areas").EnumerateArray().First(x => x.GetProperty("id").GetInt32() == aid);
        Assert.Equal(5000m, node.GetProperty("rollupTotal").GetDecimal());
        Assert.Equal(50m, node.GetProperty("costPerUnit").GetDecimal());   // 5000 / 100

        // negative quantity rejected
        var bad = await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "x", kind = "Unit", parentAreaId = aid, sortOrder = 0, quantity = -1m, unit = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

[Collection("api")]
public class ConcurrencyTests(ApiFixture fx)
{
    [Fact]
    public async Task Stale_if_match_yields_409()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "concur");

        var bd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        var stale = bd.GetProperty("rowVersion").GetString();

        // A mutation bumps the estimate's xmin (recompute touches the row).
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections", new { code = "z", title = "z", sortOrder = 0 });

        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/estimates/{eid}")
        {
            Content = JsonContent.Create(new { title = "renamed", status = (string?)null, secondaryCurrency = (string?)null }),
        };
        req.Headers.TryAddWithoutValidation("If-Match", stale);
        var resp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }
}

[Collection("api")]
public class ExportTests(ApiFixture fx)
{
    [Fact]
    public async Task Csv_export_returns_priced_boq()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.FirstEstimateIdAsync(c, pid);
        var r = await c.GetAsync($"/api/estimates/{eid}/export.csv");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Section Code,Section Title,Item Code", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Xlsx_export_returns_spreadsheet()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.FirstEstimateIdAsync(c, pid);
        var r = await c.GetAsync($"/api/estimates/{eid}/export.xlsx");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("activities.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "ActivitiesByUnit.xlsx")]
    [InlineData("activities.csv", "text/csv", "ActivitiesByUnit.csv")]
    [InlineData("activities.pdf", "application/pdf", "ActivitiesByUnit.pdf")]
    [InlineData("cost-by-area.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "CostByArea.xlsx")]
    [InlineData("cost-by-area.csv", "text/csv", "CostByArea.csv")]
    [InlineData("cost-by-area.pdf", "application/pdf", "CostByArea.pdf")]
    public async Task Activities_and_cost_by_area_exports(string path, string mime, string filename)
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.FirstEstimateIdAsync(c, pid);
        var r = await c.GetAsync($"/api/estimates/{eid}/{path}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(mime, r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(filename, r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName);
    }

    // The Activities-by-Unit group rows must show the rolled-up Material / Manpower / Total
    // of the activities beneath them — not blank (the bug shown in the report screenshot).
    [Fact]
    public async Task Activities_group_rows_show_rolled_up_subtotals()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "actsub");
        var sid = await Api.AddSectionAsync(c, eid, "ACT");
        var mat = await Api.TypeIdAsync(c, "MAT");
        var lab = await Api.TypeIdAsync(c, "LAB");

        var areaName = "Unit-" + System.Guid.NewGuid().ToString("N")[..6];
        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas", new
        {
            name = areaName, code = "U1", kind = "Unit", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null,
        })).Json();
        int aid = area.GetProperty("id").GetInt32();

        // Two activities: MAT 1000 + LAB 300 (total 1300), MAT 500 + LAB 200 (total 700).
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Plastering", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[] { new { typeId = mat, value = 1000 }, new { typeId = lab, value = 300 } }, areaId = aid,
        });
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Painting", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 1,
            components = new object[] { new { typeId = mat, value = 500 }, new { typeId = lab, value = 200 } }, areaId = aid,
        });

        var bytes = await (await c.GetAsync($"/api/estimates/{eid}/activities.xlsx")).Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheets.First(w => w.CellsUsed().Any(x => x.GetString() == "Area / Activity"));
        var groupRow = ws.RowsUsed().First(row =>
            row.Cell(1).GetString().Contains(areaName) && row.Cell(1).GetString().Contains("(Unit)"));

        Assert.Equal(1500m, groupRow.Cell(2).GetValue<decimal>());   // Material:  1000 + 500
        Assert.Equal(500m, groupRow.Cell(3).GetValue<decimal>());    // Manpower:   300 + 200
        Assert.Equal(2000m, groupRow.Cell(4).GetValue<decimal>());   // Total:     1300 + 700
    }

    // Cost-by-Area sub-area rows must be bold with a distinct (non-white) fill, like the
    // Area rows — styled by Level, not left plain.
    [Fact]
    public async Task Cost_by_area_subarea_rows_are_bold_and_filled()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "subareastyle");
        var sid = await Api.AddSectionAsync(c, eid, "SA");

        async Task<int> Area(string name, string kind, int? parent) =>
            (await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas", new
            {
                name, code = name[..System.Math.Min(8, name.Length)], kind, parentAreaId = parent,
                sortOrder = 0, quantity = 0m, unit = (string?)null,
            })).Json()).GetProperty("id").GetInt32();

        var subName = "Floor-" + System.Guid.NewGuid().ToString("N")[..6];
        var top  = await Area("Bldg-" + System.Guid.NewGuid().ToString("N")[..6], "Area", null);
        var sub  = await Area(subName, "SubArea", top);
        var unit = await Area("Unit-" + System.Guid.NewGuid().ToString("N")[..6], "Unit", sub);

        // An item under the unit so the sub-area rolls up a non-zero total.
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "x", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 500, sortOrder = 0,
            components = (object?)null, areaId = unit,
        });

        var bytes = await (await c.GetAsync($"/api/estimates/{eid}/cost-by-area.xlsx")).Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheets.First(w => w.CellsUsed().Any(x => x.GetString() == "Area"));
        var subRow = ws.RowsUsed().First(row => row.Cell(1).GetString().Contains(subName));

        Assert.True(subRow.Cell(1).Style.Font.Bold);
        Assert.NotEqual(XLColor.White, subRow.Cell(1).Style.Fill.BackgroundColor);
        Assert.NotEqual(XLColor.NoColor, subRow.Cell(1).Style.Fill.BackgroundColor);

        // The Items column rolls up: the sub-area has no direct items, but the one item on
        // the unit beneath it must be counted (was 0 before the roll-up fix).
        Assert.Equal(1, subRow.Cell(3).GetValue<int>());
    }

    // The Activities export honours ?level= : Area / Sub-Area produce a rolled-up summary
    // (one row per area of that level, no activity detail), Detail keeps the full tree.
    [Fact]
    public async Task Activities_export_respects_level()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "levels");
        var sid = await Api.AddSectionAsync(c, eid, "LV");
        var mat = await Api.TypeIdAsync(c, "MAT");
        var lab = await Api.TypeIdAsync(c, "LAB");

        async Task<int> Area(string name, string kind, int? parent) =>
            (await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas", new
            {
                name, code = name[..System.Math.Min(8, name.Length)], kind, parentAreaId = parent,
                sortOrder = 0, quantity = 0m, unit = (string?)null,
            })).Json()).GetProperty("id").GetInt32();

        var bldg = "Bldg-" + System.Guid.NewGuid().ToString("N")[..6];
        var floor = "Floor-" + System.Guid.NewGuid().ToString("N")[..6];
        var unitName = "Unit-" + System.Guid.NewGuid().ToString("N")[..6];
        var top  = await Area(bldg, "Area", null);
        var sub  = await Area(floor, "SubArea", top);
        var unit = await Area(unitName, "Unit", sub);

        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Plastering", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[] { new { typeId = mat, value = 1000 }, new { typeId = lab, value = 300 } }, areaId = unit,
        });
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Painting", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 1,
            components = new object[] { new { typeId = mat, value = 500 }, new { typeId = lab, value = 200 } }, areaId = unit,
        });

        async Task<IXLWorksheet> SheetAsync(string level)
        {
            var bytes = await (await c.GetAsync($"/api/estimates/{eid}/activities.xlsx?level={level}")).Content.ReadAsByteArrayAsync();
            var wb = new XLWorkbook(new MemoryStream(bytes));
            return wb.Worksheets.First();
        }

        // Area level: just the "Bldg" row with rolled-up totals; no sub-area/unit/activities.
        var areaWs = await SheetAsync("area");
        var areaRow = areaWs.RowsUsed().First(row => row.Cell(1).GetString().Contains(bldg));
        Assert.Equal(1500m, areaRow.Cell(2).GetValue<decimal>());
        Assert.Equal(500m, areaRow.Cell(3).GetValue<decimal>());
        Assert.Equal(2000m, areaRow.Cell(4).GetValue<decimal>());
        Assert.DoesNotContain(areaWs.RowsUsed(), row => row.Cell(1).GetString().Contains(floor));
        Assert.DoesNotContain(areaWs.RowsUsed(), row => row.Cell(1).GetString().Contains("Plastering"));

        // Sub-Area level: hierarchy Area → Sub-area (the "Floor" row), no unit/activities.
        var subWs = await SheetAsync("subarea");
        Assert.Contains(subWs.RowsUsed(), row => row.Cell(1).GetString().Contains(bldg));
        var subRow = subWs.RowsUsed().First(row => row.Cell(1).GetString().Contains(floor));
        Assert.Equal(2000m, subRow.Cell(4).GetValue<decimal>());
        Assert.DoesNotContain(subWs.RowsUsed(), row => row.Cell(1).GetString().Contains(unitName));
        Assert.DoesNotContain(subWs.RowsUsed(), row => row.Cell(1).GetString().Contains("Painting"));

        // Unit level: full hierarchy Area → Sub-area → Unit, still no activity detail.
        var unitWs = await SheetAsync("unit");
        Assert.Contains(unitWs.RowsUsed(), row => row.Cell(1).GetString().Contains(bldg));
        Assert.Contains(unitWs.RowsUsed(), row => row.Cell(1).GetString().Contains(floor));
        var unitRow = unitWs.RowsUsed().First(row => row.Cell(1).GetString().Contains(unitName));
        Assert.Equal(1500m, unitRow.Cell(2).GetValue<decimal>());
        Assert.Equal(2000m, unitRow.Cell(4).GetValue<decimal>());
        Assert.DoesNotContain(unitWs.RowsUsed(), row => row.Cell(1).GetString().Contains("Plastering"));

        // Detail level (default): the activity rows are present.
        var detailWs = await SheetAsync("detail");
        Assert.Contains(detailWs.RowsUsed(), row => row.Cell(1).GetString().Contains("Plastering"));
    }

    // Cost-by-Area honours ?level= too: each summary level lists only rows of that Level.
    [Fact]
    public async Task Cost_by_area_export_respects_level()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "cbalevels");
        var sid = await Api.AddSectionAsync(c, eid, "CB");

        async Task<int> Area(string name, string kind, int? parent) =>
            (await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas", new
            {
                name, code = name[..System.Math.Min(8, name.Length)], kind, parentAreaId = parent,
                sortOrder = 0, quantity = 0m, unit = (string?)null,
            })).Json()).GetProperty("id").GetInt32();

        var bldg = "CBldg-" + System.Guid.NewGuid().ToString("N")[..6];
        var floor = "CFloor-" + System.Guid.NewGuid().ToString("N")[..6];
        var unitName = "CUnit-" + System.Guid.NewGuid().ToString("N")[..6];
        var top  = await Area(bldg, "Area", null);
        var sub  = await Area(floor, "SubArea", top);
        var unit = await Area(unitName, "Unit", sub);
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "x", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 750, sortOrder = 0,
            components = (object?)null, areaId = unit,
        });

        async Task<IXLWorksheet> SheetAsync(string level)
        {
            var bytes = await (await c.GetAsync($"/api/estimates/{eid}/cost-by-area.xlsx?level={level}")).Content.ReadAsByteArrayAsync();
            return new XLWorkbook(new MemoryStream(bytes)).Worksheets.First();
        }

        // Area level shows the Area row (total 750 rolled up) but NOT the sub-area or unit.
        var areaWs = await SheetAsync("area");
        var areaRow = areaWs.RowsUsed().First(row => row.Cell(1).GetString().Contains(bldg));
        Assert.Equal(750m, areaRow.Cell(4).GetValue<decimal>());
        Assert.DoesNotContain(areaWs.RowsUsed(), row => row.Cell(1).GetString().Contains(floor));
        Assert.DoesNotContain(areaWs.RowsUsed(), row => row.Cell(1).GetString().Contains(unitName));

        // Sub-Area level: hierarchy Area → Sub-area, no unit.
        var subWs = await SheetAsync("subarea");
        Assert.Contains(subWs.RowsUsed(), row => row.Cell(1).GetString().Contains(bldg));
        Assert.Contains(subWs.RowsUsed(), row => row.Cell(1).GetString().Contains(floor));
        Assert.DoesNotContain(subWs.RowsUsed(), row => row.Cell(1).GetString().Contains(unitName));

        // Unit level: full hierarchy Area → Sub-area → Unit (parents retained for context).
        var unitWs = await SheetAsync("unit");
        Assert.Contains(unitWs.RowsUsed(), row => row.Cell(1).GetString().Contains(unitName));
        Assert.Contains(unitWs.RowsUsed(), row => row.Cell(1).GetString().Contains(bldg));
        Assert.Contains(unitWs.RowsUsed(), row => row.Cell(1).GetString().Contains(floor));
    }

    // The priced-BOQ Excel must build when the tenant has a logo. ClosedXML rejects a
    // resize before the picture has a Move placement, so scaling the logo before MoveTo
    // used to 500 the whole export (only when a logo > 64px was set).
    [Fact]
    public async Task Priced_boq_excel_export_works_with_a_logo()
    {
        // A real 96px-tall PNG (> the 64px cap) so the export exercises the scale path.
        const string pngB64 =
            "iVBORw0KGgoAAAANSUhEUgAAAGAAAABgCAIAAABt+uBvAAAAjklEQVR42u3QMQ0AAAgDsMmZfz2IwQEnV5Mq" +
            "aKblEAWCBAkSJEiQIEEIEiRIkCBBggQhSJAgQYIECRKEIEGCBAkSJEiQIAQJEiRIkCBBghAkSJAgQYIECUKQ" +
            "IEGCBAkSJEgQggQJEiRIkCBBCBIkSJAgQYIEIUiQIEGCBAkSJAhBggQJEiRIkCAECRL0F7S4ODJ3G0gzdgAA" +
            "AABJRU5ErkJggg==";
        var c = await fx.AdminClientAsync();
        var form = new MultipartFormDataContent();
        var img = new ByteArrayContent(Convert.FromBase64String(pngB64));
        img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(img, "file", "logo.png");
        (await c.PostAsync("/api/settings/logo", form)).EnsureSuccessStatusCode();
        try
        {
            var pid = await Api.ProjectIdAsync(c);
            var eid = await Api.FirstEstimateIdAsync(c, pid);
            var r = await c.GetAsync($"/api/estimates/{eid}/export.xlsx");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);   // regression: was 500 with a logo
            using var wb = new XLWorkbook(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
            Assert.NotEmpty(wb.Worksheets);
        }
        finally
        {
            await c.DeleteAsync("/api/settings/logo");   // restore the no-logo baseline for other tests
        }
    }
}

[Collection("api")]
public class BenchmarkTests(ApiFixture fx)
{
    [Fact]
    public async Task Benchmarks_group_area_cost_per_unit_across_projects()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        // an area measured in "key" (e.g. hotel keys), 10 keys
        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Tower", code = "T", kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 10m, unit = "key" })).Json();
        int aid = area.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "bench");
        var sid = await Api.AddSectionAsync(c, eid, "BM1");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "fitout", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 2000, sortOrder = 0,
            components = (object?)null, areaId = aid,
        });
        // publish so this revision is the representative one for the project
        var bd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        var rv = bd.GetProperty("rowVersion").GetString();
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/estimates/{eid}")
        { Content = JsonContent.Create(new { title = "bench", status = "Published", secondaryCurrency = (string?)null }) };
        put.Headers.TryAddWithoutValidation("If-Match", rv);
        (await c.SendAsync(put)).EnsureSuccessStatusCode();

        var res = await c.GetFromJsonAsync<JsonElement>("/api/benchmarks");
        Assert.Equal("AED", res.GetProperty("baseCurrency").GetString());
        var group = res.GetProperty("units").EnumerateArray().First(u => u.GetProperty("unit").GetString() == "key");
        // 2000 / 10 keys = 200 per key; in base currency (AED) so base == native
        Assert.Contains(group.GetProperty("points").EnumerateArray(),
            p => p.GetProperty("costPerUnit").GetDecimal() == 200m
              && p.GetProperty("costPerUnitBase").GetDecimal() == 200m);
        Assert.True(group.GetProperty("max").GetDecimal() >= 200m);
    }

    [Fact]
    public async Task Benchmarks_normalize_mixed_currencies_to_base()
    {
        var c = await fx.AdminClientAsync();

        // A project that bids in USD (the tenant base is AED).
        var proj = await (await c.PostAsJsonAsync("/api/projects",
            new { name = "FX Bench", currency = "USD" })).Json();
        int pid = proj.GetProperty("id").GetInt32();

        // Tenant rate: 1 USD = 3.6725 AED.
        (await c.PutAsJsonAsync("/api/settings/currencies/USD", new { rateToBase = 3.6725m })).EnsureSuccessStatusCode();

        // 100 m2bench; one 1000 item → cost/m2bench = 10 USD.
        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Block", code = "B", kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 100m, unit = "m2bench" })).Json();
        int aid = area.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "fxbench");
        var sid = await Api.AddSectionAsync(c, eid, "FX1");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "fitout", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 1000, sortOrder = 0,
            components = (object?)null, areaId = aid,
        });
        var bd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        var rv = bd.GetProperty("rowVersion").GetString();
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/estimates/{eid}")
        { Content = JsonContent.Create(new { title = "fxbench", status = "Published", secondaryCurrency = (string?)null }) };
        put.Headers.TryAddWithoutValidation("If-Match", rv);
        (await c.SendAsync(put)).EnsureSuccessStatusCode();

        var res = await c.GetFromJsonAsync<JsonElement>("/api/benchmarks");
        Assert.Equal("AED", res.GetProperty("baseCurrency").GetString());
        var group = res.GetProperty("units").EnumerateArray().First(u => u.GetProperty("unit").GetString() == "m2bench");

        // The USD point: 10 USD/unit normalized to AED = 10 × 3.6725 = 36.73 (round2).
        var pt = group.GetProperty("points").EnumerateArray().First(p => p.GetProperty("currency").GetString() == "USD");
        Assert.Equal(10m, pt.GetProperty("costPerUnit").GetDecimal());
        Assert.Equal(36.73m, pt.GetProperty("costPerUnitBase").GetDecimal());

        // Aggregate is present, in base currency, and nothing was excluded.
        Assert.Equal(group.GetProperty("count").GetInt32(), group.GetProperty("convertibleCount").GetInt32());
        Assert.Equal(36.73m, group.GetProperty("max").GetDecimal());

        // cleanup: drop the scratch rate so other runs see a clean table
        await c.DeleteAsync("/api/settings/currencies/USD");
    }

    [Fact]
    public async Task Benchmarks_require_authentication()
    {
        var r = await fx.Client().GetAsync("/api/benchmarks");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

[Collection("api")]
public class CloneRoomTests(ApiFixture fx)
{
    [Fact]
    public async Task Clone_room_duplicates_unit_and_its_activities()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var unit = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "CloneSrc", code = "CS", kind = "Unit", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json();
        int aid = unit.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "clone");
        var sid = await Api.AddSectionAsync(c, eid, "ACT");
        var mat = await Api.TypeIdAsync(c, "MAT");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Plastering", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[] { new { typeId = mat, value = 0, quantity = 10m, rate = 5m } }, areaId = aid,   // 10×5 = 50
        });

        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/areas/{aid}/clone", new { name = "CloneSrc copy" })).Json();
        var items = bd.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal(2, items.Count);   // original + clone

        var areas = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/areas");
        var copy = areas.EnumerateArray().First(a => a.GetProperty("name").GetString() == "CloneSrc copy");
        int copyId = copy.GetProperty("id").GetInt32();
        Assert.Equal("Unit", copy.GetProperty("kind").GetString());
        Assert.Contains(items, it => it.GetProperty("areaId").ValueKind == JsonValueKind.Number
                                     && it.GetProperty("areaId").GetInt32() == copyId
                                     && it.GetProperty("lineTotal").GetDecimal() == 50m);

        // cleanup (estimate first so the areas are no longer referenced by items)
        await c.DeleteAsync($"/api/projects/{pid}/estimates/{eid}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{copyId}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{aid}");
    }

    [Fact]
    public async Task Clone_subarea_duplicates_the_whole_subtree()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        // Apartment (sub-area) → Room (unit) with an activity
        var apt = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Apt-Z", code = "AZ", kind = "SubArea", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json();
        int aptId = apt.GetProperty("id").GetInt32();
        var room = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Room-Z", code = "RZ", kind = "Unit", parentAreaId = aptId, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json();
        int roomId = room.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "clonesub");
        var sid = await Api.AddSectionAsync(c, eid, "ACT");
        var mat = await Api.TypeIdAsync(c, "MAT");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "Tiling", unit = "m2", quantity = 1, assemblyId = (int?)null, unitRate = 0, sortOrder = 0,
            components = new object[] { new { typeId = mat, value = 0, quantity = 4m, rate = 25m } }, areaId = roomId,   // 100
        });

        // clone the apartment (subtree)
        var bd = await (await c.PostAsJsonAsync($"/api/estimates/{eid}/areas/{aptId}/clone", new { name = "Apt-Z copy" })).Json();
        var items = bd.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal(2, items.Count);   // tiling original + cloned

        var areas = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/areas")).EnumerateArray().ToList();
        var aptCopy = areas.First(a => a.GetProperty("name").GetString() == "Apt-Z copy");
        int aptCopyId = aptCopy.GetProperty("id").GetInt32();
        // a cloned child unit "Room-Z" now hangs under the apartment copy
        var roomCopy = areas.First(a => a.GetProperty("name").GetString() == "Room-Z"
                                        && a.GetProperty("parentAreaId").ValueKind == JsonValueKind.Number
                                        && a.GetProperty("parentAreaId").GetInt32() == aptCopyId);
        int roomCopyId = roomCopy.GetProperty("id").GetInt32();
        Assert.Contains(items, it => it.GetProperty("areaId").ValueKind == JsonValueKind.Number
                                     && it.GetProperty("areaId").GetInt32() == roomCopyId
                                     && it.GetProperty("lineTotal").GetDecimal() == 100m);

        // cleanup: estimate, then children before parents
        await c.DeleteAsync($"/api/projects/{pid}/estimates/{eid}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{roomCopyId}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{aptCopyId}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{roomId}");
        await c.DeleteAsync($"/api/projects/{pid}/areas/{aptId}");
    }
}

[Collection("api")]
public class ActivityCatalogTests(ApiFixture fx)
{
    [Fact]
    public async Task Builtin_activities_are_seeded()
    {
        var c = await fx.AdminClientAsync();
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/activities");
        var names = arr.EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Contains("Block work", names);
        Assert.Contains("Plastering", names);
        Assert.All(arr.EnumerateArray().Where(a => a.GetProperty("name").GetString() == "Block work"),
            a => Assert.True(a.GetProperty("builtin").GetBoolean()));
    }

    [Fact]
    public async Task Add_user_activity_then_dup_and_builtin_delete_conflicts()
    {
        var c = await fx.AdminClientAsync();

        var created = await (await c.PostAsJsonAsync("/api/activities", new { name = "Roofing", sortOrder = 0, isActive = true })).Json();
        int id = created.GetProperty("id").GetInt32();
        Assert.False(created.GetProperty("builtin").GetBoolean());

        // duplicate name (case-insensitive) → 409
        var dup = await c.PostAsJsonAsync("/api/activities", new { name = "roofing", sortOrder = 0, isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // built-in cannot be deleted → 409
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/activities");
        int builtinId = arr.EnumerateArray().First(a => a.GetProperty("builtin").GetBoolean()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/activities/{builtinId}")).StatusCode);

        // the user activity deletes cleanly (restore baseline)
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/activities/{id}")).StatusCode);
    }

    [Fact]
    public async Task Activities_require_authentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client().GetAsync("/api/activities")).StatusCode);
    }
}

[Collection("api")]
public class ProjectTypeCatalogTests(ApiFixture fx)
{
    [Fact]
    public async Task Builtin_project_types_are_seeded()
    {
        var c = await fx.AdminClientAsync();
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/project-types");
        var names = arr.EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Contains("Civil", names);
        Assert.Contains("Mechanical", names);
        Assert.Contains("Electrical", names);
    }

    [Fact]
    public async Task Create_type_assign_to_project_then_flows_back_and_guards()
    {
        var c = await fx.AdminClientAsync();

        // add a user type
        var created = await (await c.PostAsJsonAsync("/api/project-types", new { name = "Marine works", sortOrder = 0, isActive = true })).Json();
        int typeId = created.GetProperty("id").GetInt32();
        Assert.False(created.GetProperty("builtin").GetBoolean());

        // duplicate name (case-insensitive) → 409
        Assert.Equal(HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync("/api/project-types", new { name = "marine works", sortOrder = 0, isActive = true })).StatusCode);

        // create a project carrying the type → name flows back
        var proj = await (await c.PostAsJsonAsync("/api/projects", new { name = "Typed Project", projectTypeId = typeId })).Json();
        Assert.Equal(typeId, proj.GetProperty("projectTypeId").GetInt32());
        Assert.Equal("Marine works", proj.GetProperty("projectTypeName").GetString());
        int projectId = proj.GetProperty("id").GetInt32();

        // type in use → delete blocked (409)
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/project-types/{typeId}")).StatusCode);

        // built-in cannot be deleted → 409
        var arr = await c.GetFromJsonAsync<JsonElement>("/api/project-types");
        int builtinId = arr.EnumerateArray().First(a => a.GetProperty("builtin").GetBoolean()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/project-types/{builtinId}")).StatusCode);

        // detach from the project, then the user type deletes cleanly (restore baseline)
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/projects/{projectId}", new { name = "Typed Project", projectTypeId = (int?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/project-types/{typeId}")).StatusCode);
    }

    [Fact]
    public async Task Project_types_require_authentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client().GetAsync("/api/project-types")).StatusCode);
    }
}

[Collection("api")]
public class UserManagementTests(ApiFixture fx)
{
    [Fact]
    public async Task Admin_endpoints_reject_anonymous()
    {
        var r = await fx.Client().GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Create_team_grant_permission_create_user_and_login()
    {
        var c = await fx.AdminClientAsync();

        // 1. create a team
        var grp = await (await c.PostAsJsonAsync("/api/admin/groups",
            new { code = "QA-TEAM", name = "QA Team", description = (string?)null })).Json();
        int gid = grp.GetProperty("id").GetInt32();

        // 2. grant it boq view+add via the permission grid
        var mods = await c.GetFromJsonAsync<JsonElement>("/api/admin/modules");
        int boq = mods.EnumerateArray().First(m => m.GetProperty("code").GetString() == "boq").GetProperty("id").GetInt32();
        var perm = await (await c.PutAsJsonAsync($"/api/admin/groups/{gid}/permissions",
            new { permissions = new[] { new { moduleId = boq, canView = true, canAdd = true, canEdit = false, canDelete = false } } })).Json();
        Assert.Equal(1, perm.GetProperty("permissions").GetArrayLength());

        // 3. create a user in that team
        const string email = "qa.user@bidbuilder.local";
        var u = await (await c.PostAsJsonAsync("/api/admin/users",
            new { name = "QA User", email, password = "Qa@123456", role = "TenantUser", groupIds = new[] { gid } })).Json();
        int uid = u.GetProperty("id").GetInt32();
        Assert.Single(u.GetProperty("groups").EnumerateArray());

        // 4. the new account can log in
        var login = await fx.Client().PostAsJsonAsync("/api/auth/login", new { email, password = "Qa@123456" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // 5. duplicate email is a 409
        var dup = await c.PostAsJsonAsync("/api/admin/users",
            new { name = "Dup", email, password = "Qa@123456", role = "TenantUser", groupIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // 6. reset password, then log in with the new one
        var reset = await c.PostAsJsonAsync($"/api/admin/users/{uid}/reset-password", new { password = "NewPw@123" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var relogin = await fx.Client().PostAsJsonAsync("/api/auth/login", new { email, password = "NewPw@123" });
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);

        // cleanup so reruns / other tests stay clean
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/admin/users/{uid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/admin/groups/{gid}")).StatusCode);
    }

    [Fact]
    public async Task Builtin_team_and_last_admin_are_protected()
    {
        var c = await fx.AdminClientAsync();

        // built-in ADMINS team cannot be deleted
        var groups = await c.GetFromJsonAsync<JsonElement>("/api/admin/groups");
        int builtin = groups.EnumerateArray().First(g => g.GetProperty("isBuiltIn").GetBoolean()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/admin/groups/{builtin}")).StatusCode);

        // seeded admin is the only admin → cannot be deleted nor self-deactivated
        var users = await c.GetFromJsonAsync<JsonElement>("/api/admin/users");
        int adminId = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == "admin@bidbuilder.local").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/admin/users/{adminId}")).StatusCode);

        var deact = await c.PutAsJsonAsync($"/api/admin/users/{adminId}",
            new { name = "Demo Admin", email = "admin@bidbuilder.local", role = "TenantAdmin", isActive = false, groupIds = new[] { builtin } });
        Assert.Equal(HttpStatusCode.Conflict, deact.StatusCode);
    }
}

[Collection("api")]
public class SecurityTests(ApiFixture fx)
{
    static async Task MakeUserAsync(HttpClient admin, string email, params int[] groupIds) =>
        (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = email, email, password = "Pw@123456", role = "TenantUser", groupIds })).EnsureSuccessStatusCode();

    static async Task<(int gid, Func<string, int> mod)> MakeGroupAsync(HttpClient admin, string code, params (string code, bool view, bool add, bool edit, bool del)[] perms)
    {
        var mods = await admin.GetFromJsonAsync<JsonElement>("/api/admin/modules");
        int Mod(string c) => mods.EnumerateArray().First(m => m.GetProperty("code").GetString() == c).GetProperty("id").GetInt32();
        var grp = await (await admin.PostAsJsonAsync("/api/admin/groups", new { code, name = code, description = (string?)null })).Json();
        int gid = grp.GetProperty("id").GetInt32();
        await admin.PutAsJsonAsync($"/api/admin/groups/{gid}/permissions", new
        {
            permissions = perms.Select(p => new { moduleId = Mod(p.code), canView = p.view, canAdd = p.add, canEdit = p.edit, canDelete = p.del }).ToArray()
        });
        return (gid, Mod);
    }

    // ── Privilege escalation: a plain TenantUser can't reach admin/privileged endpoints ──
    [Fact]
    public async Task NonAdmin_is_blocked_from_admin_endpoints()
    {
        var admin = await fx.AdminClientAsync();
        const string email = "sec.plain@bidbuilder.local";
        await MakeUserAsync(admin, email);
        var u = await fx.AuthedClientAsync(email, "Pw@123456");

        Assert.Equal(HttpStatusCode.Forbidden, (await u.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await u.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await u.PostAsJsonAsync("/api/admin/groups", new { code = "X", name = "X", description = (string?)null })).StatusCode);
        // project create is admin-only
        Assert.Equal(HttpStatusCode.Forbidden, (await u.PostAsJsonAsync("/api/projects", new { name = "Hacker project" })).StatusCode);
    }

    // ── Object-level authorization (IDOR): no project access → 404 on REAL existing ids ──
    [Fact]
    public async Task User_without_access_gets_404_on_real_ids()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        const string email = "sec.noaccess@bidbuilder.local";
        await MakeUserAsync(admin, email);
        var u = await fx.AuthedClientAsync(email, "Pw@123456");

        // Real ids that exist — hidden as 404 (not 200, not 403): the user is on no team.
        Assert.Equal(HttpStatusCode.NotFound, (await u.GetAsync($"/api/projects/{pid}/estimates")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await u.GetAsync($"/api/estimates/{eid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await u.GetAsync($"/api/projects/{pid}/areas")).StatusCode);
    }

    // ── RBAC: a view-only teammate HAS access but mutations are 403 (not 404) ──
    [Fact]
    public async Task ViewOnly_member_can_read_but_not_mutate()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var (gid, _) = await MakeGroupAsync(admin, "SEC-VIEWERS",
            ("projects", true, false, false, false), ("boq", true, false, false, false));
        await admin.PostAsJsonAsync($"/api/projects/{pid}/teams", new { groupId = gid, isLead = false });
        const string email = "sec.viewer@bidbuilder.local";
        await MakeUserAsync(admin, email, gid);
        var u = await fx.AuthedClientAsync(email, "Pw@123456");

        // Reads succeed (team access + View).
        Assert.Equal(HttpStatusCode.OK, (await u.GetAsync($"/api/projects/{pid}/estimates")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await u.GetAsync($"/api/estimates/{eid}")).StatusCode);

        // A BOQ mutation is rejected 403 (access OK, but no boq Add) — proving the
        // access(404)/permission(403) split, i.e. NOT a 404.
        var post = await u.PostAsJsonAsync($"/api/estimates/{eid}/sections", new { code = "X", title = "X", sortOrder = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        await admin.DeleteAsync($"/api/projects/{pid}/teams/{gid}");
    }

    // ── The areas-rollup permission fix: requires `projects` View, not just access ──
    [Fact]
    public async Task Areas_rollup_requires_projects_view_permission()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        // boq View only — deliberately NO projects View.
        var (gid, _) = await MakeGroupAsync(admin, "SEC-BOQONLY", ("boq", true, false, false, false));
        await admin.PostAsJsonAsync($"/api/projects/{pid}/teams", new { groupId = gid, isLead = false });
        const string email = "sec.boqonly@bidbuilder.local";
        await MakeUserAsync(admin, email, gid);
        var u = await fx.AuthedClientAsync(email, "Pw@123456");

        // Has estimate access (boq View) → the breakdown reads.
        Assert.Equal(HttpStatusCode.OK, (await u.GetAsync($"/api/estimates/{eid}")).StatusCode);
        // But the per-area cost roll-up now requires `projects` View → 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await u.GetAsync($"/api/estimates/{eid}/areas-rollup")).StatusCode);

        await admin.DeleteAsync($"/api/projects/{pid}/teams/{gid}");
    }

    // ── Tenant isolation: a second tenant's admin can't see the first tenant's data ──
    [Fact]
    public async Task Second_tenant_cannot_see_first_tenant_data()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var t2 = await fx.SecondTenantAdminClientAsync();

        // Tenant 2 sees none of tenant 1's projects.
        var projects = await t2.GetFromJsonAsync<JsonElement>("/api/projects");
        Assert.DoesNotContain(projects.EnumerateArray(), p => p.GetProperty("code").GetString() == "PRJ-2026-001");

        // Tenant 1's real estimate id is hidden cross-tenant (404), not 200.
        Assert.Equal(HttpStatusCode.NotFound, (await t2.GetAsync($"/api/estimates/{eid}")).StatusCode);

        // Tenant 2's user list contains only its own admin, never tenant 1's.
        var users = await t2.GetFromJsonAsync<JsonElement>("/api/admin/users");
        Assert.DoesNotContain(users.EnumerateArray(), us => us.GetProperty("email").GetString() == "admin@bidbuilder.local");
        Assert.Contains(users.EnumerateArray(), us => us.GetProperty("email").GetString() == "admin@tenant2.local");
    }

    // ── Tenant-slug enumeration is closed: an unknown slug looks identical to a missing
    //    one (generic 401, no echo) and the anonymous /api/ping probe now requires auth ──
    [Fact]
    public async Task Unknown_tenant_slug_is_indistinguishable_and_not_echoed()
    {
        var ghost = fx.ClientForTenant("does-not-exist-9f3");
        var resp = await ghost.PostAsJsonAsync("/api/auth/login", new { email = "x@x.com", password = "whatever" });

        // 401 (not the old 404), and the body must not echo the probed slug.
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain("does-not-exist-9f3", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ping_requires_auth_so_it_cant_probe_tenants()
    {
        // Anonymous → 401 regardless of the tenant header (no 200 oracle).
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client().GetAsync("/api/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.ClientForTenant("does-not-exist-9f3").GetAsync("/api/ping")).StatusCode);
        // Authenticated → 200.
        var admin = await fx.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/ping")).StatusCode);
    }

    // ── Status lock is fail-closed: every content write on a Published revision is 409,
    //    while the read-equivalent writes (recompute, what-if) and the revert PUT stay open ──
    [Fact]
    public async Task Published_revision_locks_writes_but_allows_reads_and_revert()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "statuslock");
        var sid = await Api.AddSectionAsync(c, eid, "SL");
        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "LockZone", code = "LZ", kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json();
        int aid = area.GetProperty("id").GetInt32();

        // Freeze it.
        (await Api.PutMetaAsync(c, eid, new { title = (string?)null, status = "Published", secondaryCurrency = (string?)null })).EnsureSuccessStatusCode();

        // Every content-mutation route is 409 — including clone (which used to self-check)
        // and any route the old substring allowlist would have had to enumerate.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections", new { code = "X", title = "X", sortOrder = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync($"/api/estimates/{eid}/sections/{sid}", new { code = "Y", title = "Y", sortOrder = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/estimates/{eid}/sections/{sid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items",
            new { description = "i", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 1, sortOrder = 0, components = (object?)null, areaId = (int?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/estimates/{eid}/preliminaries", new { description = "p", kind = "Fixed", amount = 1, sortOrder = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/estimates/{eid}/markups", new { type = "Profit", label = (string?)null, percentage = 1, applyOrder = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/estimates/{eid}/areas/{aid}/clone", new { name = "x" })).StatusCode);

        // Read-equivalent writes stay available (marked .AllowWhenFinalised()).
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/estimates/{eid}/recompute", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/estimates/{eid}/whatif", new { markups = Array.Empty<object>() })).StatusCode);

        // The revert PUT is allowed; afterwards content edits work again.
        (await Api.PutMetaAsync(c, eid, new { title = (string?)null, status = "Draft", secondaryCurrency = (string?)null })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/estimates/{eid}/sections", new { code = "Z", title = "Z", sortOrder = 2 })).StatusCode);
    }

    // ── Auth: a deactivated user cannot log in (and the active one can) ──
    [Fact]
    public async Task Deactivated_user_cannot_log_in()
    {
        var admin = await fx.AdminClientAsync();
        const string email = "sec.inactive@bidbuilder.local";
        var u = await (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = "Inactive", email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() })).Json();
        int uid = u.GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.OK, (await fx.Client().PostAsJsonAsync("/api/auth/login", new { email, password = "Pw@123456" })).StatusCode);

        await admin.PutAsJsonAsync($"/api/admin/users/{uid}",
            new { name = "Inactive", email, role = "TenantUser", isActive = false, groupIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client().PostAsJsonAsync("/api/auth/login", new { email, password = "Pw@123456" })).StatusCode);
    }
}

[Collection("api")]
public class ImportTests(ApiFixture fx)
{
    static readonly string[] FullHeader = { "Section Code", "Section Title", "Item Code", "Description", "Unit", "Quantity", "Unit Rate", "Assembly Code" };

    static void Header(IXLWorksheet ws, params string[] cols)
    {
        for (int i = 0; i < cols.Length; i++) ws.Cell(1, i + 1).Value = cols[i];
    }

    [Fact]
    public async Task Import_template_downloads_as_xlsx()
    {
        var c = await fx.AdminClientAsync();
        var r = await c.GetAsync("/api/estimates/import-template.xlsx");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.Content.Headers.ContentType?.MediaType);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 2 && bytes[0] == 0x50 && bytes[1] == 0x4B); // "PK" zip/xlsx magic
    }

    [Fact]
    public async Task Import_appends_sections_and_items_and_reprices()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "import");

        // Two ad-hoc rows grouped into one section: 2×100 + 3×50 = 350 direct.
        var xlsx = Api.Xlsx(ws =>
        {
            Header(ws, FullHeader);
            ws.Cell(2, 1).Value = "S1"; ws.Cell(2, 2).Value = "Earthworks"; ws.Cell(2, 4).Value = "Excavation"; ws.Cell(2, 5).Value = "m3"; ws.Cell(2, 6).Value = 2; ws.Cell(2, 7).Value = 100;
            ws.Cell(3, 1).Value = "S1"; ws.Cell(3, 2).Value = "Earthworks"; ws.Cell(3, 4).Value = "Backfill";   ws.Cell(3, 5).Value = "m3"; ws.Cell(3, 6).Value = 3; ws.Cell(3, 7).Value = 50;
        });
        var res = await (await c.PostAsync($"/api/estimates/{eid}/import", Api.FileForm(xlsx))).Json();
        Assert.Equal(1, res.GetProperty("sectionsAdded").GetInt32());
        Assert.Equal(2, res.GetProperty("itemsAdded").GetInt32());
        Assert.Equal(350m, res.GetProperty("estimate").GetProperty("directCost").GetDecimal());
        Assert.Equal(350m, res.GetProperty("estimate").GetProperty("bidPrice").GetDecimal());
    }

    [Fact]
    public async Task Import_resolves_assembly_code()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "import-asm-ok");

        // Create an assembly (no components → computed rate 0) and reference it by code.
        const string code = "ASM-IMP-1";
        (await c.PostAsJsonAsync("/api/assemblies", new { code, name = "Import test", unit = "m3", isActive = true })).EnsureSuccessStatusCode();

        var xlsx = Api.Xlsx(ws =>
        {
            Header(ws, FullHeader);
            ws.Cell(2, 2).Value = "Concrete"; ws.Cell(2, 4).Value = "RC footing"; ws.Cell(2, 5).Value = "m3"; ws.Cell(2, 6).Value = 2; ws.Cell(2, 8).Value = code;
        });
        var res = await (await c.PostAsync($"/api/estimates/{eid}/import", Api.FileForm(xlsx))).Json();
        Assert.Equal(1, res.GetProperty("itemsAdded").GetInt32());
        var item = res.GetProperty("estimate").GetProperty("sections")[0].GetProperty("items")[0];
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("assemblyId").ValueKind);   // resolved to the assembly
        Assert.Equal(0m, item.GetProperty("unitRate").GetDecimal());                     // engine-derived, not the sheet's ad-hoc rate
    }

    [Fact]
    public async Task Import_rejects_missing_required_column()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "import-nocol");
        // Header has Description but no Quantity.
        var xlsx = Api.Xlsx(ws => { Header(ws, "Description"); ws.Cell(2, 1).Value = "x"; });
        var r = await c.PostAsync($"/api/estimates/{eid}/import", Api.FileForm(xlsx));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Quantity", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Import_rejects_unknown_assembly_code()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "import-badasm");
        var xlsx = Api.Xlsx(ws =>
        {
            Header(ws, "Section Title", "Description", "Quantity", "Assembly Code");
            ws.Cell(2, 1).Value = "S"; ws.Cell(2, 2).Value = "Item"; ws.Cell(2, 3).Value = 1; ws.Cell(2, 4).Value = "NOPE-123";
        });
        var r = await c.PostAsync($"/api/estimates/{eid}/import", Api.FileForm(xlsx));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("NOPE-123", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Import_rejects_non_xlsx_and_empty_upload()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "import-junk");

        // garbage bytes → not a valid workbook
        var junk = await c.PostAsync($"/api/estimates/{eid}/import", Api.FileForm(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);

        // no file part at all
        var none = await c.PostAsync($"/api/estimates/{eid}/import", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public async Task Import_into_published_estimate_is_locked_409()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "import-locked");
        (await Api.PutMetaAsync(c, eid, new { title = (string?)null, status = "Published", secondaryCurrency = (string?)null })).EnsureSuccessStatusCode();

        var xlsx = Api.Xlsx(ws =>
        {
            Header(ws, "Section Title", "Description", "Quantity", "Unit Rate");
            ws.Cell(2, 1).Value = "S"; ws.Cell(2, 2).Value = "Item"; ws.Cell(2, 3).Value = 1; ws.Cell(2, 4).Value = 10;
        });
        var r = await c.PostAsync($"/api/estimates/{eid}/import", Api.FileForm(xlsx));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }
}

[Collection("api")]
public class CopyToProjectTests(ApiFixture fx)
{
    [Fact]
    public async Task Copy_deep_copies_into_target_and_drops_area_tags()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        // Source: one area-tagged item (500) + a fixed prelim (200) + a 10% profit markup.
        var area = await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "CopyZone", code = "CZ", kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json();
        int aid = area.GetProperty("id").GetInt32();

        var eid = await Api.NewEstimateAsync(c, pid, "copysrc");
        var sid = await Api.AddSectionAsync(c, eid, "CP");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "CopyItem", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 500, sortOrder = 0,
            components = (object?)null, areaId = aid,
        });
        await c.PostAsJsonAsync($"/api/estimates/{eid}/preliminaries", new { description = "Mobilization", kind = "Fixed", amount = 200, sortOrder = 0 });
        await c.PostAsJsonAsync($"/api/estimates/{eid}/markups", new { type = "Profit", label = (string?)null, percentage = 10, applyOrder = 0 });

        // Target project.
        var tproj = await (await c.PostAsJsonAsync("/api/projects", new { name = "Copy Target" })).Json();
        int tpid = tproj.GetProperty("id").GetInt32();

        // Copy across.
        var summary = await (await c.PostAsJsonAsync($"/api/projects/{pid}/estimates/{eid}/copy",
            new { targetProjectId = tpid, title = "Copied" })).Json();
        int newId = summary.GetProperty("id").GetInt32();
        Assert.Equal("Draft", summary.GetProperty("status").GetString());
        Assert.Equal("Copied", summary.GetProperty("title").GetString());
        // (500 + 200) × 1.10 = 770 — a fixed prelim doesn't depend on the target's duration.
        Assert.Equal(770m, summary.GetProperty("bidPrice").GetDecimal());

        // The copy reproduces BOQ/prelim/markup, but the area tag is dropped (areas are project-scoped).
        var bd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{newId}");
        Assert.Equal(tpid, bd.GetProperty("projectId").GetInt32());
        var item = bd.GetProperty("sections")[0].GetProperty("items")[0];
        Assert.Equal("CopyItem", item.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("areaId").ValueKind);
        Assert.Single(bd.GetProperty("preliminaries").EnumerateArray());
        Assert.Single(bd.GetProperty("markups").EnumerateArray());

        // The source is untouched (still has its area tag).
        var srcBd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        Assert.Equal(aid, srcBd.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("areaId").GetInt32());
    }

    [Fact]
    public async Task Copy_to_same_project_is_400()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "copysame");
        var r = await c.PostAsJsonAsync($"/api/projects/{pid}/estimates/{eid}/copy", new { targetProjectId = pid, title = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Copy_to_inaccessible_target_is_404()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "copy404");
        var r = await c.PostAsJsonAsync($"/api/projects/{pid}/estimates/{eid}/copy", new { targetProjectId = 999999, title = (string?)null });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}

[Collection("api")]
public class FxFreezeTests(ApiFixture fx)
{
    static async Task<int> EstimateWithBidAsync(HttpClient c, int pid, string title, decimal rate)
    {
        var eid = await Api.NewEstimateAsync(c, pid, title);
        var sid = await Api.AddSectionAsync(c, eid, "FX");
        await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items", new
        {
            description = "x", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = rate, sortOrder = 0,
            components = (object?)null, areaId = (int?)null,
        });
        return eid;
    }

    [Fact]
    public async Task Publish_freezes_fx_then_revert_goes_live_again()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);

        // 1 USD = 4 AED → cross AED→USD = 0.25.
        (await c.PutAsJsonAsync("/api/settings/currencies/USD", new { rateToBase = 4m })).EnsureSuccessStatusCode();
        var eid = await EstimateWithBidAsync(c, pid, "fxfreeze", 1000m);   // bid 1000 AED

        // Draft + secondary USD → live (not frozen): 1000 × 0.25 = 250.
        (await Api.PutMetaAsync(c, eid, new { title = (string?)null, status = (string?)null, secondaryCurrency = "USD" })).EnsureSuccessStatusCode();
        var draft = (await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}")).GetProperty("fx");
        Assert.Equal("USD", draft.GetProperty("secondaryCurrency").GetString());
        Assert.False(draft.GetProperty("frozen").GetBoolean());
        Assert.Equal(250m, draft.GetProperty("convertedBidPrice").GetDecimal());

        // Publish → snapshot the rate.
        (await Api.PutMetaAsync(c, eid, new { title = (string?)null, status = "Published", secondaryCurrency = (string?)null })).EnsureSuccessStatusCode();
        var pub = (await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}")).GetProperty("fx");
        Assert.True(pub.GetProperty("frozen").GetBoolean());
        Assert.Equal(250m, pub.GetProperty("convertedBidPrice").GetDecimal());

        // Move the tenant rate to 1 USD = 5 AED — the frozen published bid must NOT drift.
        (await c.PutAsJsonAsync("/api/settings/currencies/USD", new { rateToBase = 5m })).EnsureSuccessStatusCode();
        var pub2 = (await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}")).GetProperty("fx");
        Assert.Equal(250m, pub2.GetProperty("convertedBidPrice").GetDecimal());

        // Revert to Draft → live again at the new rate: 1000 × 0.20 = 200.
        (await Api.PutMetaAsync(c, eid, new { title = (string?)null, status = "Draft", secondaryCurrency = (string?)null })).EnsureSuccessStatusCode();
        var draft2 = (await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}")).GetProperty("fx");
        Assert.False(draft2.GetProperty("frozen").GetBoolean());
        Assert.Equal(200m, draft2.GetProperty("convertedBidPrice").GetDecimal());

        // Clear the secondary currency → no fx view.
        (await Api.PutMetaAsync(c, eid, new { title = (string?)null, status = (string?)null, secondaryCurrency = "" })).EnsureSuccessStatusCode();
        var cleared = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("fx").ValueKind);

        await c.DeleteAsync("/api/settings/currencies/USD");   // cleanup scratch rate
    }

    [Fact]
    public async Task Secondary_currency_without_a_rate_yields_no_fx()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await EstimateWithBidAsync(c, pid, "fxnorate", 100m);

        // ZZZ has no rate and isn't the base → fx can't be built.
        (await Api.PutMetaAsync(c, eid, new { title = (string?)null, status = (string?)null, secondaryCurrency = "ZZZ" })).EnsureSuccessStatusCode();
        var bd = await c.GetFromJsonAsync<JsonElement>($"/api/estimates/{eid}");
        Assert.Equal(JsonValueKind.Null, bd.GetProperty("fx").ValueKind);
    }
}

// ── Platform SuperAdmin: tenant-less login + cross-tenant tenant management ──────
[Collection("api")]
public class PlatformTests(ApiFixture fx)
{
    static async Task CreateTenantAsync(HttpClient super, object body) =>
        Assert.Equal(HttpStatusCode.Created,
            (await super.PostAsJsonAsync("/api/platform/tenants", body)).StatusCode);

    // The SuperAdmin signs in with NO tenant header (dedicated platform-login) and can
    // enumerate every tenant — including the seeded "default".
    [Fact]
    public async Task SuperAdmin_logs_in_without_a_tenant_and_lists_tenants()
    {
        var super = await fx.PlatformAdminClientAsync();
        var tenants = await super.GetFromJsonAsync<JsonElement>("/api/platform/tenants");
        Assert.Contains(tenants.EnumerateArray(), t => t.GetProperty("slug").GetString() == "default");
        // The aggregate columns are present (default tenant has its seeded admin + sample project).
        var def = tenants.EnumerateArray().First(t => t.GetProperty("slug").GetString() == "default");
        Assert.True(def.GetProperty("userCount").GetInt32() >= 1);
    }

    // Creating a tenant provisions its defaults + first admin; that admin can then sign in
    // to their own workspace and use it normally.
    [Fact]
    public async Task SuperAdmin_creates_a_tenant_whose_admin_can_log_in()
    {
        var super = await fx.PlatformAdminClientAsync();
        await CreateTenantAsync(super, new
        {
            slug = "acme-build", name = "ACME Build", defaultLocale = "en",
            adminName = "ACME Admin", adminEmail = "admin@acme-build.local", adminPassword = "Pw@123456",
        });

        // The new admin logs into the new workspace and gets a working (empty) project list.
        var admin = await fx.AuthedClientAsync("admin@acme-build.local", "Pw@123456", "acme-build");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/projects")).StatusCode);
        // It's a clean tenant — none of the default tenant's sample project leaks across.
        var projects = await admin.GetFromJsonAsync<JsonElement>("/api/projects");
        Assert.DoesNotContain(projects.EnumerateArray(), p => p.GetProperty("code").GetString() == "PRJ-2026-001");
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected()
    {
        var super = await fx.PlatformAdminClientAsync();
        await CreateTenantAsync(super, new { slug = "dupe-co", name = "Dupe Co" });
        var again = await super.PostAsJsonAsync("/api/platform/tenants", new { slug = "dupe-co", name = "Dupe Co 2" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // Suspending a tenant is a hard stop: its members can't log in until it's re-activated.
    [Fact]
    public async Task Suspending_a_tenant_blocks_its_users_login()
    {
        var super = await fx.PlatformAdminClientAsync();
        await CreateTenantAsync(super, new
        {
            slug = "susp-co", name = "Suspend Co",
            adminName = "S Admin", adminEmail = "admin@susp-co.local", adminPassword = "Pw@123456",
        });
        var id = (await super.GetFromJsonAsync<JsonElement>("/api/platform/tenants"))
            .EnumerateArray().First(t => t.GetProperty("slug").GetString() == "susp-co").GetProperty("id").GetGuid();

        // Active → login works.
        var login = fx.ClientForTenant("susp-co");
        Assert.Equal(HttpStatusCode.OK,
            (await login.PostAsJsonAsync("/api/auth/login", new { email = "admin@susp-co.local", password = "Pw@123456" })).StatusCode);

        // Suspend → login is 403 (workspace suspended), not a credential 401.
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsync($"/api/platform/tenants/{id}/suspend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await fx.ClientForTenant("susp-co").PostAsJsonAsync("/api/auth/login", new { email = "admin@susp-co.local", password = "Pw@123456" })).StatusCode);

        // Re-activate → login works again.
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsync($"/api/platform/tenants/{id}/activate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await fx.ClientForTenant("susp-co").PostAsJsonAsync("/api/auth/login", new { email = "admin@susp-co.local", password = "Pw@123456" })).StatusCode);
    }

    // A TenantAdmin (even with full tenant rights) is NOT a platform operator.
    [Fact]
    public async Task TenantAdmin_cannot_reach_platform_endpoints()
    {
        var admin = await fx.AdminClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/platform/tenants")).StatusCode);
    }

    // A SuperAdmin token carries no tenant — tenant-scoped routes refuse it up front (400),
    // rather than faulting on an unresolved tenant.
    [Fact]
    public async Task SuperAdmin_token_is_refused_on_tenant_scoped_routes()
    {
        var super = await fx.PlatformAdminClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await super.GetAsync("/api/projects")).StatusCode);
    }

    // platform-login admits only a real SuperAdmin — a tenant admin's credentials are 401.
    [Fact]
    public async Task Platform_login_rejects_a_tenant_admin()
    {
        var resp = await fx.Client().PostAsJsonAsync("/api/auth/platform-login",
            new { email = "admin@bidbuilder.local", password = "Admin@12345" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // Anonymous callers can't touch platform administration.
    [Fact]
    public async Task Anonymous_cannot_reach_platform_endpoints()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client().GetAsync("/api/platform/tenants")).StatusCode);
    }
}

// ── Hardening: robustness fixes (suspension kill-switch, area scoping, input guards) ──
[Collection("api")]
public class HardeningTests(ApiFixture fx)
{
    // Suspending a tenant must kill ALREADY-ISSUED tokens immediately (not just new logins).
    [Fact]
    public async Task Suspended_tenant_blocks_an_already_issued_token()
    {
        var super = await fx.PlatformAdminClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await super.PostAsJsonAsync("/api/platform/tenants", new
        {
            slug = "harden-susp", name = "Harden Susp",
            adminName = "H Admin", adminEmail = "admin@harden-susp.local", adminPassword = "Pw@123456",
        })).StatusCode);

        // Get a live token BEFORE suspension and prove it works.
        var admin = await fx.AuthedClientAsync("admin@harden-susp.local", "Pw@123456", "harden-susp");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/projects")).StatusCode);

        var id = (await super.GetFromJsonAsync<JsonElement>("/api/platform/tenants"))
            .EnumerateArray().First(t => t.GetProperty("slug").GetString() == "harden-susp").GetProperty("id").GetGuid();
        await super.PostAsync($"/api/platform/tenants/{id}/suspend", null);

        // The SAME token (no re-login) is now rejected mid-session.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/projects")).StatusCode);

        // Re-activate → the same token works again.
        await super.PostAsync($"/api/platform/tenants/{id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/projects")).StatusCode);
    }

    // A BOQ item may only reference an area of its OWN project, not another project's.
    [Fact]
    public async Task BOQ_item_rejects_a_foreign_projects_area()
    {
        var c = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(c);
        var eid = await Api.NewEstimateAsync(c, pid, "areascope");
        var sid = await Api.AddSectionAsync(c, eid, "AS");

        // A second project with its own area.
        var p2 = (await (await c.PostAsJsonAsync("/api/projects", new { code = ("AS2-" + System.Guid.NewGuid().ToString("N"))[..12], name = "Other Proj", currency = "AED" })).Json()).GetProperty("id").GetInt32();
        var foreignArea = (await (await c.PostAsJsonAsync($"/api/projects/{p2}/areas",
            new { name = "Foreign", code = "FN", kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json()).GetProperty("id").GetInt32();

        // Tagging this estimate's item with the OTHER project's area is rejected.
        var bad = await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items",
            new { description = "x", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 1, sortOrder = 0, components = (object?)null, areaId = foreignArea });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // An area of THIS project is accepted.
        var ownArea = (await (await c.PostAsJsonAsync($"/api/projects/{pid}/areas",
            new { name = "Own", code = ("OWN" + System.Guid.NewGuid().ToString("N"))[..6], kind = "Area", parentAreaId = (int?)null, sortOrder = 0, quantity = 0m, unit = (string?)null })).Json()).GetProperty("id").GetInt32();
        var ok = await c.PostAsJsonAsync($"/api/estimates/{eid}/sections/{sid}/items",
            new { description = "y", unit = "no", quantity = 1, assemblyId = (int?)null, unitRate = 1, sortOrder = 1, components = (object?)null, areaId = ownArea });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // A mislabeled (fake content-type) logo upload is rejected, so it can't 500 exports later.
    [Fact]
    public async Task Logo_upload_rejects_non_image_bytes()
    {
        var c = await fx.AdminClientAsync();
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("this is definitely not a PNG"));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");   // lies about the type
        form.Add(content, "file", "evil.png");
        var resp = await c.PostAsync("/api/settings/logo", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // A duplicate code differing only by surrounding whitespace yields a clean 409, not a 500.
    [Fact]
    public async Task Duplicate_resource_code_with_whitespace_is_409_not_500()
    {
        var c = await fx.AdminClientAsync();
        var code = ("HRD" + System.Guid.NewGuid().ToString("N"))[..8];
        Assert.Equal(HttpStatusCode.Created,
            (await c.PostAsJsonAsync("/api/resources/labor", new { code, name = "A", unit = "hr", ratePerHour = 10m, isActive = true })).StatusCode);
        var dup = await c.PostAsJsonAsync("/api/resources/labor", new { code = code + "  ", name = "B", unit = "hr", ratePerHour = 10m, isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    // Settings input is validated → clean 400, never a varchar(3) 500 or negative-markup corruption.
    [Fact]
    public async Task Settings_rejects_bad_base_currency_and_negative_percentages()
    {
        var c = await fx.AdminClientAsync();
        var badCcy = await c.PutAsJsonAsync("/api/settings",
            new { baseCurrency = "DOLLAR", defaultOverheadPct = 8m, defaultProfitPct = 12m, defaultContingencyPct = 5m });
        Assert.Equal(HttpStatusCode.BadRequest, badCcy.StatusCode);

        var badPct = await c.PutAsJsonAsync("/api/settings",
            new { baseCurrency = "AED", defaultOverheadPct = -1m, defaultProfitPct = 12m, defaultContingencyPct = 5m });
        Assert.Equal(HttpStatusCode.BadRequest, badPct.StatusCode);
    }
}
