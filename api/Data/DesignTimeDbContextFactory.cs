using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Data;

/// <summary>
/// Design-time factory used by the <c>dotnet ef</c> CLI (migrations add/script,
/// dbcontext info). Having one here stops the tooling from booting the full app
/// host (which would connect to Postgres and run seeds) for a simple migration.
///
/// The stub tenant is fine because design-time operations only inspect the model;
/// the HasQueryFilter lambdas read <c>_tenant.TenantId</c> lazily at query time.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("DESIGN_TIME_CONNECTION")
                   ?? "Host=localhost;Database=bidbuilder;Username=postgres;Password=postgres";

        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(conn)
            .Options;

        return new AppDbContext(opts, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public string TenantSlug => "design-time";
        public Guid   TenantId   => Guid.Empty;
        public bool   IsResolved => false;
        public void Set(Guid tenantId, string tenantSlug) { /* no-op */ }
    }
}
