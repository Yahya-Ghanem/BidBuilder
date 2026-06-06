using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// Contract-drift gate for 19.5. The checked-in <c>api-spec/openapi.json</c>
/// snapshot is the SOURCE the generated TypeScript client (<c>lib/api-generated.d.ts</c>)
/// is produced from. If the live API's spec drifts from that snapshot, the
/// generated client is stale — and a frontend that thinks an endpoint returns
/// shape A will break the moment shape B lands.
///
/// This test fetches the live spec from the in-process test host, normalises it
/// the same way the snapshot was captured (sort keys, 2-space indent), and
/// compares verbatim. On a fail it tells the developer exactly what to do:
///   1. Rebuild the spec from a running API.
///   2. Re-run <c>npm run gen:api</c> to refresh the TS client.
///   3. Commit both.
///
/// This catches:
///   • A new endpoint added without updating the spec.
///   • A DTO field renamed or removed.
///   • A response shape changed (when the endpoint declares one via .Produces&lt;T&gt;).
///   • A swagger title/version bump that wasn't reflected in the snapshot.
/// </summary>
[Collection("api")]
public class OpenApiContractTests(ApiFixture fx)
{
    [Fact]
    public async Task Live_openapi_spec_matches_checked_in_snapshot()
    {
        var c = fx.Client();
        // Swagger isn't auth-protected — the JSON document is meant for tooling.
        var live = await c.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        var normalisedLive = Canonicalise(live);

        // Locate api-spec/openapi.json by walking up from the test bin dir until we hit
        // a sibling api-spec/ folder. Independent of the working directory CI / dotnet uses.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "api-spec", "openapi.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var snapshotPath = Path.Combine(dir!.FullName, "api-spec", "openapi.json");
        var snapshotJson = await File.ReadAllTextAsync(snapshotPath);
        using var snapshotDoc = JsonDocument.Parse(snapshotJson);
        var normalisedSnapshot = Canonicalise(snapshotDoc.RootElement);

        if (normalisedLive == normalisedSnapshot) return;

        // Helpful failure message so the developer knows the exact next steps.
        var msg =
            "OpenAPI contract drift detected — the live spec differs from api-spec/openapi.json.\n" +
            "To resolve:\n" +
            "  1) Start the API locally (docker compose up -d --build api).\n" +
            "  2) curl -s http://localhost:8081/swagger/v1/swagger.json | python -c \"import sys,json;" +
            " d=json.load(sys.stdin); open(r'api-spec/openapi.json','w',newline='\\n').write(json.dumps(d, indent=2, sort_keys=True))\"\n" +
            "  3) npm run gen:api\n" +
            "  4) Commit both api-spec/openapi.json and lib/api-generated.d.ts.\n" +
            $"Snapshot path: {snapshotPath}\n" +
            $"First-difference index (live): {FirstDiffIndex(normalisedLive, normalisedSnapshot)}";
        Assert.Fail(msg);
    }

    /// <summary>Match the canonicalisation rule the snapshot was captured with:
    /// sort all object keys recursively, 2-space indent, no trailing whitespace.
    /// Lists keep their order (OpenAPI's path lists are semantically meaningful).</summary>
    private static string Canonicalise(JsonElement el)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            WriteSorted(el, writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSorted(JsonElement el, Utf8JsonWriter writer)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(p.Name);
                    WriteSorted(p.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteSorted(item, writer);
                writer.WriteEndArray();
                break;
            default:
                el.WriteTo(writer);
                break;
        }
    }

    private static int FirstDiffIndex(string a, string b)
    {
        var min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++) if (a[i] != b[i]) return i;
        return min;
    }
}
