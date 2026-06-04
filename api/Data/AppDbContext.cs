using Microsoft.EntityFrameworkCore;
using BidBuilder.Api.Models;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Data;

/// <summary>
/// Single DbContext for the API. Multi-tenant via global query filter: every
/// <see cref="IHasTenant"/> entity is automatically scoped to the current
/// request's tenant — forgetting <c>WHERE tenant_id = X</c> is impossible.
///
/// To add a new tenant-owned aggregate:
///   1. Implement IHasTenant   2. Add a DbSet here   3. Add a query filter below.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    private readonly ITenantContext _tenant = tenant;

    // ── Tenancy + admin / RBAC core (reused from SmartShipping) ───────────────
    public DbSet<Tenant>         Tenants        => Set<Tenant>();
    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();
    public DbSet<User>           Users          => Set<User>();
    public DbSet<Group>          Groups         => Set<Group>();
    public DbSet<Module>         Modules        => Set<Module>();
    public DbSet<GroupModule>    GroupModules   => Set<GroupModule>();
    public DbSet<UserGroup>      UserGroups     => Set<UserGroup>();
    public DbSet<AuditEvent>     AuditEvents    => Set<AuditEvent>();

    // ── Project scoping layer (BidBuilder) ────────────────────────────────────
    public DbSet<Project>     Projects     => Set<Project>();
    public DbSet<ProjectTeam> ProjectTeams => Set<ProjectTeam>();
    public DbSet<Area>        Areas        => Set<Area>();
    public DbSet<ActivityType> ActivityTypes => Set<ActivityType>();

    // ── Estimate aggregate ────────────────────────────────────────────────────
    public DbSet<Estimate>    Estimates     => Set<Estimate>();
    public DbSet<BoqSection>  BoqSections   => Set<BoqSection>();
    public DbSet<BoqItem>     BoqItems      => Set<BoqItem>();
    public DbSet<Preliminary> Preliminaries => Set<Preliminary>();
    public DbSet<Markup>      Markups       => Set<Markup>();

    // ── Resource library + assemblies ─────────────────────────────────────────
    public DbSet<LaborResource>     LaborResources     => Set<LaborResource>();
    public DbSet<MaterialResource>  MaterialResources  => Set<MaterialResource>();
    public DbSet<EquipmentResource> EquipmentResources => Set<EquipmentResource>();
    public DbSet<Subcontractor>     Subcontractors     => Set<Subcontractor>();
    public DbSet<Assembly>          Assemblies         => Set<Assembly>();
    public DbSet<AssemblyComponent> AssemblyComponents => Set<AssemblyComponent>();

    // ── FX (manual currency rates) ────────────────────────────────────────────
    public DbSet<CurrencyRate>      CurrencyRates      => Set<CurrencyRate>();

    // ── Cost-component build-up (extensible item pricing) ──────────────────────
    public DbSet<CostComponentType> CostComponentTypes => Set<CostComponentType>();
    public DbSet<ItemCostComponent> ItemCostComponents => Set<ItemCostComponent>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Tenant ─────────────────────────────────────────────────────────────
        mb.Entity<Tenant>(b =>
        {
            b.HasIndex(t => t.Slug).IsUnique();
            b.Property(t => t.Slug).HasMaxLength(64).IsRequired();
            b.Property(t => t.Name).HasMaxLength(120).IsRequired();
            b.Property(t => t.BrandColor).HasMaxLength(7);
            b.Property(t => t.DefaultLocale).HasMaxLength(8).IsRequired();
        });

        // ── TenantSettings (1:1 with Tenant) ──────────────────────────────────
        mb.Entity<TenantSettings>(b =>
        {
            b.HasIndex(ts => ts.TenantId).IsUnique();
            b.HasOne(ts => ts.Tenant).WithOne().HasForeignKey<TenantSettings>(ts => ts.TenantId);
            b.Property(ts => ts.BaseCurrency).HasMaxLength(3).IsRequired();
            b.Property(ts => ts.Timezone).HasMaxLength(60);
            b.Property(ts => ts.Country).HasMaxLength(2);
            b.Property(ts => ts.DefaultOverheadPct).HasColumnType("numeric(9,4)");
            b.Property(ts => ts.DefaultProfitPct).HasColumnType("numeric(9,4)");
            b.Property(ts => ts.DefaultContingencyPct).HasColumnType("numeric(9,4)");
            b.HasQueryFilter(ts => ts.TenantId == _tenant.TenantId);
        });

        // ── User ───────────────────────────────────────────────────────────────
        mb.Entity<User>(b =>
        {
            b.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            b.Property(u => u.Email).HasMaxLength(254).IsRequired();
            b.Property(u => u.PasswordHash).HasMaxLength(120).IsRequired();
            b.Property(u => u.Name).HasMaxLength(120);
            b.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            // SuperAdmin (TenantId null) excluded automatically: null == X is false.
            b.HasQueryFilter(u => u.TenantId == _tenant.TenantId);
        });

        // ── Group (team) ─────────────────────────────────────────────────────
        mb.Entity<Group>(b =>
        {
            b.HasIndex(g => new { g.TenantId, g.Code }).IsUnique();
            b.HasIndex(g => g.TenantId);
            b.Property(g => g.Code).HasMaxLength(32).IsRequired();
            b.Property(g => g.Name).HasMaxLength(120).IsRequired();
            b.Property(g => g.Description).HasMaxLength(400);
            b.HasQueryFilter(g => g.TenantId == _tenant.TenantId);
        });

        // ── Module ───────────────────────────────────────────────────────────
        mb.Entity<Module>(b =>
        {
            b.HasIndex(m => new { m.TenantId, m.Code }).IsUnique();
            b.HasIndex(m => m.TenantId);
            b.Property(m => m.Code).HasMaxLength(32).IsRequired();
            b.Property(m => m.Name).HasMaxLength(120).IsRequired();
            b.Property(m => m.Description).HasMaxLength(400);
            b.HasQueryFilter(m => m.TenantId == _tenant.TenantId);
        });

        // ── GroupModule (Group ↔ Module) ──────────────────────────────────────
        mb.Entity<GroupModule>(b =>
        {
            b.HasKey(gm => new { gm.GroupId, gm.ModuleId });
            b.HasIndex(gm => gm.TenantId);
            b.HasOne(gm => gm.Group).WithMany(g => g.GroupModules)
             .HasForeignKey(gm => gm.GroupId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(gm => gm.Module).WithMany(m => m.GroupModules)
             .HasForeignKey(gm => gm.ModuleId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(gm => gm.TenantId == _tenant.TenantId);
        });

        // ── UserGroup (User ↔ Group) ──────────────────────────────────────────
        mb.Entity<UserGroup>(b =>
        {
            b.HasKey(ug => new { ug.UserId, ug.GroupId });
            b.HasIndex(ug => ug.TenantId);
            b.HasOne(ug => ug.User).WithMany(u => u.UserGroups)
             .HasForeignKey(ug => ug.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(ug => ug.Group).WithMany(g => g.UserGroups)
             .HasForeignKey(ug => ug.GroupId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(ug => ug.TenantId == _tenant.TenantId);
        });

        // ── AuditEvent ─────────────────────────────────────────────────────────
        mb.Entity<AuditEvent>(b =>
        {
            b.HasIndex(a => new { a.TenantId, a.At });
            b.Property(a => a.ActorEmail).HasMaxLength(254);
            b.Property(a => a.ActorName).HasMaxLength(120);
            b.Property(a => a.ActorRole).HasMaxLength(32);
            b.Property(a => a.Action).HasMaxLength(64);
            b.Property(a => a.Entity).HasMaxLength(64);
            b.Property(a => a.EntityKey).HasMaxLength(128);
            b.Property(a => a.Summary).HasMaxLength(400);
            b.HasQueryFilter(a => a.TenantId == _tenant.TenantId);
        });

        // ── Project ─────────────────────────────────────────────────────────────
        mb.Entity<Project>(b =>
        {
            b.HasIndex(p => new { p.TenantId, p.Code }).IsUnique();
            b.HasIndex(p => p.TenantId);
            b.Property(p => p.Code).HasMaxLength(40).IsRequired();
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.Property(p => p.ClientName).HasMaxLength(200);
            b.Property(p => p.Location).HasMaxLength(200);
            b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            b.HasQueryFilter(p => p.TenantId == _tenant.TenantId);
        });

        // ── ProjectTeam (Project ↔ Group) ─────────────────────────────────────
        mb.Entity<ProjectTeam>(b =>
        {
            b.HasKey(pt => new { pt.ProjectId, pt.GroupId });
            b.HasIndex(pt => pt.TenantId);
            b.HasIndex(pt => pt.GroupId);
            b.HasOne(pt => pt.Project).WithMany(p => p.ProjectTeams)
             .HasForeignKey(pt => pt.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(pt => pt.Group).WithMany(g => g.ProjectTeams)
             .HasForeignKey(pt => pt.GroupId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(pt => pt.TenantId == _tenant.TenantId);
        });

        // ── Estimate (optimistic concurrency via xmin) ────────────────────────
        mb.Entity<Estimate>(b =>
        {
            b.HasIndex(e => new { e.ProjectId, e.Revision }).IsUnique();
            b.HasIndex(e => e.TenantId);
            b.Property(e => e.Title).HasMaxLength(200);
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.SecondaryCurrency).HasMaxLength(3);
            b.Property(e => e.FxRate).HasColumnType("numeric(18,6)");
            b.Property(e => e.DefaultLaborRate).HasColumnType("numeric(18,4)");
            b.Property(e => e.DirectCost).HasColumnType("numeric(18,2)");
            b.Property(e => e.IndirectCost).HasColumnType("numeric(18,2)");
            b.Property(e => e.MarkupCost).HasColumnType("numeric(18,2)");
            b.Property(e => e.BidPrice).HasColumnType("numeric(18,2)");
            b.HasOne(e => e.Project).WithMany(p => p.Estimates)
             .HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // Optimistic concurrency on the Postgres system column xmin — protects
            // concurrent team edits without a model-level rowversion property.
            b.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");
            b.HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        });

        // ── BoqSection (self-nesting) ──────────────────────────────────────────
        mb.Entity<BoqSection>(b =>
        {
            b.HasIndex(s => s.EstimateId);
            b.HasIndex(s => s.TenantId);
            b.Property(s => s.Code).HasMaxLength(40);
            b.Property(s => s.Title).HasMaxLength(200).IsRequired();
            b.Property(s => s.SectionTotal).HasColumnType("numeric(18,2)");
            b.HasOne(s => s.Estimate).WithMany(e => e.Sections)
             .HasForeignKey(s => s.EstimateId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<BoqSection>().WithMany()
             .HasForeignKey(s => s.ParentSectionId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(s => s.TenantId == _tenant.TenantId);
        });

        // ── Area (project-level location breakdown, self-nesting) ──────────────
        mb.Entity<Area>(b =>
        {
            b.HasIndex(a => a.ProjectId);
            b.HasIndex(a => a.TenantId);
            b.Property(a => a.Name).HasMaxLength(160).IsRequired();
            b.Property(a => a.Code).HasMaxLength(40);
            b.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(a => a.Quantity).HasColumnType("numeric(18,4)");
            b.Property(a => a.Unit).HasMaxLength(16);
            b.HasOne(a => a.Project).WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Area>().WithMany().HasForeignKey(a => a.ParentAreaId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(a => a.TenantId == _tenant.TenantId);
        });

        // ── BoqItem ─────────────────────────────────────────────────────────────
        mb.Entity<BoqItem>(b =>
        {
            b.HasIndex(i => i.SectionId);
            b.HasIndex(i => i.TenantId);
            b.HasOne<Area>().WithMany().HasForeignKey(i => i.AreaId).OnDelete(DeleteBehavior.SetNull);
            b.Property(i => i.ItemCode).HasMaxLength(40);
            b.Property(i => i.Description).HasMaxLength(400).IsRequired();
            b.Property(i => i.Unit).HasMaxLength(16);
            b.Property(i => i.Quantity).HasColumnType("numeric(18,4)");
            b.Property(i => i.UnitRate).HasColumnType("numeric(18,4)");
            b.Property(i => i.LineTotal).HasColumnType("numeric(18,2)");
            b.HasOne(i => i.Section).WithMany(s => s.Items)
             .HasForeignKey(i => i.SectionId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Assembly).WithMany()
             .HasForeignKey(i => i.AssemblyId).OnDelete(DeleteBehavior.SetNull);
            b.HasQueryFilter(i => i.TenantId == _tenant.TenantId);
        });

        // ── Preliminary ─────────────────────────────────────────────────────────
        mb.Entity<Preliminary>(b =>
        {
            b.HasIndex(p => p.EstimateId);
            b.HasIndex(p => p.TenantId);
            b.Property(p => p.Description).HasMaxLength(300).IsRequired();
            b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.Amount).HasColumnType("numeric(18,2)");
            b.Property(p => p.ComputedTotal).HasColumnType("numeric(18,2)");
            b.HasOne(p => p.Estimate).WithMany(e => e.Preliminaries)
             .HasForeignKey(p => p.EstimateId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(p => p.TenantId == _tenant.TenantId);
        });

        // ── Markup ───────────────────────────────────────────────────────────────
        mb.Entity<Markup>(b =>
        {
            b.HasIndex(m => m.EstimateId);
            b.HasIndex(m => m.TenantId);
            b.Property(m => m.Type).HasConversion<string>().HasMaxLength(16);
            b.Property(m => m.Label).HasMaxLength(120);
            b.Property(m => m.Percentage).HasColumnType("numeric(9,4)");
            b.Property(m => m.ComputedAmount).HasColumnType("numeric(18,2)");
            b.HasOne(m => m.Estimate).WithMany(e => e.Markups)
             .HasForeignKey(m => m.EstimateId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(m => m.TenantId == _tenant.TenantId);
        });

        // ── Resource library ──────────────────────────────────────────────────
        ConfigureResource<LaborResource>(mb, b =>
        {
            b.Property(r => r.Unit).HasMaxLength(16);
            b.Property(r => r.RatePerHour).HasColumnType("numeric(18,4)");
        });
        ConfigureResource<MaterialResource>(mb, b =>
        {
            b.Property(r => r.Unit).HasMaxLength(16);
            b.Property(r => r.UnitPrice).HasColumnType("numeric(18,4)");
            b.Property(r => r.WastagePct).HasColumnType("numeric(9,4)");
            b.Property(r => r.Supplier).HasMaxLength(160);
        });
        ConfigureResource<EquipmentResource>(mb, b =>
        {
            b.Property(r => r.Unit).HasMaxLength(16);
            b.Property(r => r.RatePerHour).HasColumnType("numeric(18,4)");
        });
        ConfigureResource<Subcontractor>(mb, b =>
        {
            b.Property(r => r.Unit).HasMaxLength(16);
            b.Property(r => r.UnitRate).HasColumnType("numeric(18,4)");
        });

        // ── Assembly + components ──────────────────────────────────────────────
        mb.Entity<Assembly>(b =>
        {
            b.HasIndex(a => new { a.TenantId, a.Code }).IsUnique();
            b.HasIndex(a => a.TenantId);
            b.Property(a => a.Code).HasMaxLength(40).IsRequired();
            b.Property(a => a.Name).HasMaxLength(200).IsRequired();
            b.Property(a => a.Unit).HasMaxLength(16);
            b.Property(a => a.ComputedRate).HasColumnType("numeric(18,4)");
            b.HasQueryFilter(a => a.TenantId == _tenant.TenantId);
        });
        // ── CurrencyRate (manual FX, base-currency units per 1 unit of Code) ───
        mb.Entity<CurrencyRate>(b =>
        {
            b.HasIndex(r => new { r.TenantId, r.Code }).IsUnique();
            b.HasIndex(r => r.TenantId);
            b.Property(r => r.Code).HasMaxLength(3).IsRequired();
            b.Property(r => r.RateToBase).HasColumnType("numeric(18,6)");
            b.HasQueryFilter(r => r.TenantId == _tenant.TenantId);
        });

        // ── CostComponentType (tenant catalog) + ItemCostComponent (item line) ──
        mb.Entity<CostComponentType>(b =>
        {
            b.HasIndex(c => new { c.TenantId, c.Code }).IsUnique();
            b.HasIndex(c => c.TenantId);
            b.Property(c => c.Code).HasMaxLength(16).IsRequired();
            b.Property(c => c.Name).HasMaxLength(80).IsRequired();
            b.Property(c => c.CalcKind).HasConversion<string>().HasMaxLength(16);
            b.HasQueryFilter(c => c.TenantId == _tenant.TenantId);
        });
        mb.Entity<ItemCostComponent>(b =>
        {
            b.HasIndex(c => new { c.BoqItemId, c.CostComponentTypeId }).IsUnique();
            b.HasIndex(c => c.TenantId);
            b.Property(c => c.Value).HasColumnType("numeric(18,4)");
            b.Property(c => c.Quantity).HasColumnType("numeric(18,4)");
            b.Property(c => c.Rate).HasColumnType("numeric(18,4)");
            b.HasOne(c => c.BoqItem).WithMany(i => i.CostComponents)
             .HasForeignKey(c => c.BoqItemId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.Type).WithMany()
             .HasForeignKey(c => c.CostComponentTypeId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(c => c.TenantId == _tenant.TenantId);
        });

        mb.Entity<ActivityType>(b =>
        {
            b.HasIndex(a => new { a.TenantId, a.Name }).IsUnique();
            b.HasIndex(a => a.TenantId);
            b.Property(a => a.Name).HasMaxLength(120).IsRequired();
            b.HasQueryFilter(a => a.TenantId == _tenant.TenantId);
        });

        mb.Entity<AssemblyComponent>(b =>
        {
            b.HasIndex(c => c.AssemblyId);
            b.HasIndex(c => c.TenantId);
            b.Property(c => c.ResourceType).HasConversion<string>().HasMaxLength(16);
            b.Property(c => c.Factor).HasColumnType("numeric(18,4)");
            b.Property(c => c.Note).HasMaxLength(300);
            b.HasOne(c => c.Assembly).WithMany(a => a.Components)
             .HasForeignKey(c => c.AssemblyId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(c => c.TenantId == _tenant.TenantId);
        });
    }

    /// <summary>Shared config for the four simple resource tables: code unique
    /// within tenant, name required, tenant query filter, plus per-type extras.</summary>
    private void ConfigureResource<T>(ModelBuilder mb, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T>> extra)
        where T : class, IHasTenant
    {
        mb.Entity<T>(b =>
        {
            b.HasIndex("TenantId", "Code").IsUnique();
            b.HasIndex(r => r.TenantId);
            b.Property("Code").HasMaxLength(40).IsRequired();
            b.Property("Name").HasMaxLength(200).IsRequired();
            b.HasQueryFilter(r => r.TenantId == _tenant.TenantId);
            extra(b);
        });
    }

    /// <summary>
    /// Auto-stamp TenantId on insert (never trust the client). AuditEvent may
    /// land with an empty tenant for pre-auth flows; everything else requires a
    /// resolved tenant.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<IHasTenant>())
        {
            if (entry.State != EntityState.Added) continue;
            var scoped = entry.Entity;
            if (scoped.TenantId != Guid.Empty) continue;

            if (_tenant.IsResolved)
                scoped.TenantId = _tenant.TenantId;
            else if (entry.Entity is not AuditEvent)
                throw new InvalidOperationException(
                    "Tenant not resolved — cannot insert tenant-scoped entity. " +
                    "Was the request routed through TenantResolutionMiddleware?");
        }

        return await base.SaveChangesAsync(ct);
    }
}
