using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 28.3 coverage: `?preview=1` flag on the existing export endpoints. The
/// acceptance bar is "user sees the figures before downloading and can close
/// without saving anything", and the data-access crux is that the preview path
/// goes through the same authorization gate as the download path — a user who
/// can't access an estimate can't read its figures via preview either.
///
/// Coverage:
///   1. PDF preview returns application/pdf with an INLINE disposition (no
///      filename) — that's what lets the browser render it in an iframe.
///   2. PDF download (no preview flag) still returns the attachment form.
///   3. xlsx preview returns text/html — same data the deliverable carries,
///      but a form the browser can preview natively.
///   4. csv preview also returns text/html so the iframe can render it.
///   5. xlsx download (no preview flag) returns the application/vnd...sheet
///      mime — the preview switch did not regress the deliverable path.
///   6. The cross-tenant authz gate fires on the preview path too: a foreign
///      estimate id returns 404 even with preview=1.
/// </summary>
[Collection("api")]
public class ExportPreviewTests(ApiFixture fx)
{
    [Fact]
    public async Task Pdf_preview_returns_inline_pdf_without_attachment_filename()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var resp = await admin.GetAsync($"/api/estimates/{eid}/export.pdf?preview=1");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", resp.Content.Headers.ContentType?.MediaType);
        // Inline disposition: either no Content-Disposition header at all, or
        // one without `attachment` and without a filename — that's what lets
        // <iframe src="…?preview=1"> render the document instead of triggering
        // the browser download prompt.
        var disp = resp.Content.Headers.ContentDisposition;
        Assert.True(disp is null || disp.DispositionType != "attachment", "preview should not be attachment");
        Assert.True(disp?.FileName is null && disp?.FileNameStar is null, "preview should not carry a download filename");

        // And the body is actually a PDF — first four bytes are the %PDF magic.
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 4);
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
    }

    [Fact]
    public async Task Pdf_download_still_attaches_filename_when_preview_flag_absent()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var resp = await admin.GetAsync($"/api/estimates/{eid}/export.pdf");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", resp.Content.Headers.ContentType?.MediaType);
        // The deliverable path keeps the attachment behaviour so a click in a
        // raw browser still saves a file with a friendly name.
        var disp = resp.Content.Headers.ContentDisposition;
        Assert.NotNull(disp);
        Assert.Equal("attachment", disp!.DispositionType);
        Assert.NotNull(disp.FileName ?? disp.FileNameStar);
    }

    [Fact]
    public async Task Xlsx_preview_returns_html_rendering_of_the_same_model()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var resp = await admin.GetAsync($"/api/estimates/{eid}/export.xlsx?preview=1");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);

        var html = await resp.Content.ReadAsStringAsync();
        // Must be a real HTML document (not just text dumped through), and must
        // carry the project code so the user can confirm they're previewing the
        // right revision.
        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Priced BOQ", html);
        Assert.Contains("PRJ-2026-001", html); // seeded project code from the test fixture
    }

    [Fact]
    public async Task Csv_preview_also_returns_html_rendering()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var resp = await admin.GetAsync($"/api/estimates/{eid}/export.csv?preview=1");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Xlsx_download_unchanged_when_preview_flag_absent()
    {
        var admin = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin);
        var eid = await Api.FirstEstimateIdAsync(admin, pid);

        var resp = await admin.GetAsync($"/api/estimates/{eid}/export.xlsx");
        resp.EnsureSuccessStatusCode();
        // The Office Open XML spreadsheet mime — the bytes that ship to the client.
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", resp.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Cross_tenant_estimate_is_404_on_preview_path_too()
    {
        // The first tenant creates a fresh estimate.
        var admin1 = await fx.AdminClientAsync();
        var pid = await Api.ProjectIdAsync(admin1);
        var eid = await Api.NewEstimateAsync(admin1, pid, "preview-authz");

        // A second-tenant admin must NOT be able to read it via the preview path.
        // Same hardening principle as the bulk endpoint's per-line authz check —
        // the preview lane reuses the same Export() gate, so a 404 here proves
        // it. (`SecondTenantAdminClientAsync` is the standard cross-tenant
        // helper on the test fixture.)
        var admin2 = await fx.SecondTenantAdminClientAsync();
        var resp = await admin2.GetAsync($"/api/estimates/{eid}/export.pdf?preview=1");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
