using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Data;
using BidBuilder.Api.Models;
using BidBuilder.Api.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 18.4 coverage. The cascade now flows through <see cref="ICascadeQueue"/>:
///
///   • In the test host Hangfire is disabled (<c>Hangfire__Enabled=false</c>), so
///     <see cref="InlineCascadeQueue"/> is registered. We prove the inline path still
///     fans out — a resource rate change must produce an updated assembly
///     <c>ComputedRate</c> in the SAME HTTP round-trip, otherwise the existing 126
///     tests' "edit rate → see cascaded effect" assertions would race the worker.
///
///   • In a production-shaped configuration (<see cref="HangfireCascadeQueue"/>),
///     a rate change must produce exactly one enqueued job whose target is
///     <see cref="RateCascadeJob.RunAsync"/> with the expected (tenant, type, id)
///     payload. We exercise that by hand-constructing the queue against a
///     mock <see cref="IBackgroundJobClient"/> — no real Hangfire infra needed.
///
/// These together prove the abstraction is real: the request path doesn't know
/// (or care) which side it's wired to.
/// </summary>
[Collection("api")]
public class CascadeQueueTests(ApiFixture fx)
{
    [Fact]
    public async Task InlineQueue_propagates_resource_rate_change_to_dependent_assembly()
    {
        var admin = await fx.AdminClientAsync();

        // Seed a labor at 100, an assembly with 1 hour of that labor, → ComputedRate ought to be 100.
        var lab = await (await admin.PostAsJsonAsync("/api/resources/labor", new
        {
            code = $"LAB-CSC-{System.Guid.NewGuid():N}"[..14], name = "Cascade smoke", unit = "hr",
            ratePerHour = 100m, isActive = true,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var laborId = lab.GetProperty("id").GetInt32();

        var asm = await (await admin.PostAsJsonAsync("/api/assemblies", new
        {
            code = $"ASM-CSC-{System.Guid.NewGuid():N}"[..14], name = "Cascade asm", unit = "m", isActive = true,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var asmId = asm.GetProperty("id").GetInt32();
        (await admin.PostAsJsonAsync($"/api/assemblies/{asmId}/components", new
        {
            resourceType = "Labor", resourceId = laborId, factor = 1m, sortOrder = 1,
        })).EnsureSuccessStatusCode();

        // Sanity: asm rate now reflects labor = 100.
        var before = await admin.GetFromJsonAsync<JsonElement>($"/api/assemblies/{asmId}");
        Assert.Equal(100m, before.GetProperty("computedRate").GetDecimal());

        // PUT labor rate up to 250. With the inline queue this MUST update the assembly
        // in the same round-trip — if Hangfire had been in play, the GET below would race.
        (await admin.PutAsJsonAsync($"/api/resources/labor/{laborId}", new
        {
            code = "ignored", name = "Cascade smoke", unit = "hr", ratePerHour = 250m, isActive = true,
        })).EnsureSuccessStatusCode();

        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/assemblies/{asmId}");
        Assert.Equal(250m, after.GetProperty("computedRate").GetDecimal());
    }

    [Fact]
    public async Task HangfireQueue_enqueues_a_job_targeting_RateCascadeJob_with_the_resource_payload()
    {
        // Hand-construct the production queue against a recording IBackgroundJobClient.
        // No real Hangfire server is spun — we only verify the call our request path makes.
        var client = new RecordingJobClient();
        var queue = new HangfireCascadeQueue(client);
        var tenantId = System.Guid.NewGuid();

        await queue.EnqueueResourceChangedAsync(tenantId, ResourceType.Material, 42);
        await queue.EnqueueResourceChangedAsync(tenantId, ResourceType.Labor, 7);

        Assert.Equal(2, client.Created.Count);

        var first = client.Created[0];
        Assert.Equal(typeof(RateCascadeJob), first.Job.Type);
        Assert.Equal(nameof(RateCascadeJob.RunAsync), first.Job.Method.Name);
        // Args are serialized as their CLR values pre-Hangfire — the recorder captured raw boxes.
        Assert.Equal(tenantId,            first.Job.Args[0]);
        Assert.Equal(ResourceType.Material, first.Job.Args[1]);
        Assert.Equal(42,                  first.Job.Args[2]);

        var second = client.Created[1];
        Assert.Equal(ResourceType.Labor, second.Job.Args[1]);
        Assert.Equal(7,                  second.Job.Args[2]);

        // Default queue + Enqueued initial state — i.e. ready to be picked up immediately.
        Assert.IsType<EnqueuedState>(first.State);
    }

    /// <summary>Captures every Create call so the test can assert what was enqueued
    /// without needing a real Hangfire server / Postgres.</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<(Job Job, IState State)> Created { get; } = new();

        public string Create(Job job, IState state)
        {
            Created.Add((job, state));
            return System.Guid.NewGuid().ToString();
        }

        // The other interface members aren't needed by Enqueue<T>(), but must compile.
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
        public bool Delete(string jobId) => true;
        public bool Delete(string jobId, string fromState) => true;
        public bool Requeue(string jobId) => true;
        public bool Requeue(string jobId, string fromState) => true;
    }
}
