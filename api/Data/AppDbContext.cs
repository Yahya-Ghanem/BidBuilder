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
    // In-app notifications (20.3) — one row per recipient per event.
    public DbSet<Notification>   Notifications  => Set<Notification>();
    // Outbound webhooks (20.9) — tenant-configured event subscriptions.
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    // Programmatic API keys (21.2) — hashed, tenant-scoped, act-as-creator credentials.
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // ── Project scoping layer (BidBuilder) ────────────────────────────────────
    public DbSet<Project>     Projects     => Set<Project>();
    public DbSet<ProjectTeam> ProjectTeams => Set<ProjectTeam>();
    public DbSet<Area>        Areas        => Set<Area>();
    public DbSet<ActivityType> ActivityTypes => Set<ActivityType>();
    public DbSet<ProjectType> ProjectTypes => Set<ProjectType>();

    // ── Estimate aggregate ────────────────────────────────────────────────────
    public DbSet<Estimate>    Estimates     => Set<Estimate>();
    public DbSet<BoqSection>  BoqSections   => Set<BoqSection>();
    public DbSet<BoqItem>     BoqItems      => Set<BoqItem>();
    public DbSet<Preliminary> Preliminaries => Set<Preliminary>();
    public DbSet<Markup>      Markups       => Set<Markup>();
    // Risk register (19.2) — per-estimate rows whose Expected Value sums to a
    // suggested contingency the estimator can apply to the Contingency markup.
    public DbSet<RiskItem>    RiskItems     => Set<RiskItem>();
    // Approval workflow (20.2) — sign-offs accumulated against an estimate;
    // gate the Draft/UnderReview → Published transition.
    public DbSet<EstimateApproval> EstimateApprovals => Set<EstimateApproval>();

    // ── Resource library + assemblies ─────────────────────────────────────────
    public DbSet<LaborResource>     LaborResources     => Set<LaborResource>();
    public DbSet<MaterialResource>  MaterialResources  => Set<MaterialResource>();
    public DbSet<EquipmentResource> EquipmentResources => Set<EquipmentResource>();
    public DbSet<Subcontractor>     Subcontractors     => Set<Subcontractor>();
    public DbSet<Assembly>          Assemblies         => Set<Assembly>();
    public DbSet<AssemblyComponent> AssemblyComponents => Set<AssemblyComponent>();

    // ── FX (manual currency rates) ────────────────────────────────────────────
    public DbSet<CurrencyRate>      CurrencyRates      => Set<CurrencyRate>();

    // ── Dated resource rates + supplier quotes (18.3) ─────────────────────────
    public DbSet<ResourceRateHistory> ResourceRateHistory => Set<ResourceRateHistory>();
    public DbSet<SupplierQuote>       SupplierQuotes      => Set<SupplierQuote>();

    // ── Subcontractor quote portal (20.6) ─────────────────────────────────────
    public DbSet<SubcontractorQuote>  SubcontractorQuotes => Set<SubcontractorQuote>();

    // ── SAML SSO config (20.8b) — 1:1 with Tenant ─────────────────────────────
    public DbSet<TenantSamlConfig>    SamlConfigs         => Set<TenantSamlConfig>();

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
            b.Property(t => t.CustomDomain).HasMaxLength(253);   // max DNS hostname length
            // Unique only among rows that set it — many tenants have NULL.
            b.HasIndex(t => t.CustomDomain).IsUnique().HasFilter("\"CustomDomain\" IS NOT NULL");
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
            // Bid outcome register (19.1) — money fields stored to 2dp; note is bounded.
            b.Property(p => p.SubmittedBidValue).HasColumnType("numeric(18,2)");
            b.Property(p => p.AwardedValue).HasColumnType("numeric(18,2)");
            b.Property(p => p.FinalCost).HasColumnType("numeric(18,2)");
            b.Property(p => p.WinLossNote).HasMaxLength(1000);
            // DecisionAt drives the period axis on the dashboard; index for fast aggregation.
            b.HasIndex(p => new { p.TenantId, p.DecisionAt });
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

        // ── RiskItem (19.2 risk register) ─────────────────────────────────────
        mb.Entity<RiskItem>(b =>
        {
            b.HasIndex(r => r.EstimateId);
            b.HasIndex(r => r.TenantId);
            b.Property(r => r.Title).HasMaxLength(200).IsRequired();
            b.Property(r => r.Category).HasConversion<string>().HasMaxLength(16);
            b.Property(r => r.ProbabilityPct).HasColumnType("numeric(9,4)");
            b.Property(r => r.ImpactAmount).HasColumnType("numeric(18,2)");
            b.Property(r => r.Note).HasMaxLength(1000);
            // Risks cascade with the estimate — when an estimate is deleted, its
            // register goes with it (just like preliminaries and markups).
            b.HasOne<Estimate>().WithMany(e => e.Risks).HasForeignKey(r => r.EstimateId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(r => r.TenantId == _tenant.TenantId);
        });

        // ── EstimateApproval (20.2 sign-off ledger) ──────────────────────────
        mb.Entity<EstimateApproval>(b =>
        {
            b.HasIndex(a => a.EstimateId);
            b.HasIndex(a => a.TenantId);
            // Idempotent: a single user may have at most one active approval per
            // estimate. A re-approve is a no-op (the handler short-circuits).
            b.HasIndex(a => new { a.EstimateId, a.ApproverUserId }).IsUnique();
            b.Property(a => a.ApproverEmail).HasMaxLength(254).IsRequired();
            b.Property(a => a.ApproverName).HasMaxLength(120).IsRequired();
            b.Property(a => a.Note).HasMaxLength(1000);
            // Cascade with the estimate (same lifetime as risks / preliminaries).
            b.HasOne(a => a.Estimate).WithMany(e => e.Approvals).HasForeignKey(a => a.EstimateId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(a => a.TenantId == _tenant.TenantId);
        });

        // ── Notification (20.3 in-app inbox) ─────────────────────────────────
        mb.Entity<Notification>(b =>
        {
            // The unread-badge poll filters by recipient + IsRead; index it.
            b.HasIndex(n => new { n.RecipientUserId, n.IsRead });
            b.HasIndex(n => n.TenantId);
            b.Property(n => n.Type).HasMaxLength(64).IsRequired();
            b.Property(n => n.Title).HasMaxLength(200).IsRequired();
            b.Property(n => n.Body).HasMaxLength(1000);
            b.Property(n => n.Link).HasMaxLength(400);
            b.Property(n => n.EntityType).HasMaxLength(64);
            b.Property(n => n.EntityKey).HasMaxLength(64);
            b.HasQueryFilter(n => n.TenantId == _tenant.TenantId);
        });

        // ── WebhookSubscription (20.9 outbound API) ──────────────────────────
        mb.Entity<WebhookSubscription>(b =>
        {
            b.HasIndex(w => w.TenantId);
            b.Property(w => w.Url).HasMaxLength(2048).IsRequired();
            b.Property(w => w.Secret).HasMaxLength(128).IsRequired();
            b.Property(w => w.Events).HasMaxLength(1024).IsRequired();
            b.Property(w => w.LastStatus).HasMaxLength(120);
            b.HasQueryFilter(w => w.TenantId == _tenant.TenantId);
        });

        // ── ApiKey (21.2 programmatic access) ─────────────────────────────────
        mb.Entity<ApiKey>(b =>
        {
            // Globally-unique hash so the auth handler can resolve a presented key in one
            // indexed lookup (across tenants, before the tenant context is resolved).
            b.HasIndex(k => k.KeyHash).IsUnique();
            b.HasIndex(k => k.TenantId);
            b.Property(k => k.Name).HasMaxLength(120).IsRequired();
            b.Property(k => k.Prefix).HasMaxLength(24).IsRequired();
            b.Property(k => k.KeyHash).HasMaxLength(64).IsRequired();
            b.HasQueryFilter(k => k.TenantId == _tenant.TenantId);
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

        // ── ResourceRateHistory (dated rate points per library resource) ───────
        mb.Entity<ResourceRateHistory>(b =>
        {
            // Pricing-date lookups filter by (type, id) and order by EffectiveFrom desc,
            // so a composite index makes them index-only seeks.
            b.HasIndex(h => new { h.TenantId, h.ResourceType, h.ResourceId, h.EffectiveFrom });
            b.HasIndex(h => h.TenantId);
            b.Property(h => h.ResourceType).HasConversion<string>().HasMaxLength(16);
            b.Property(h => h.Rate).HasColumnType("numeric(18,4)");
            b.Property(h => h.Source).HasMaxLength(200);
            b.HasQueryFilter(h => h.TenantId == _tenant.TenantId);
        });

        // ── SupplierQuote (independent register, may reference a library resource) ──
        mb.Entity<SupplierQuote>(b =>
        {
            b.HasIndex(q => new { q.TenantId, q.ResourceType, q.ResourceId });
            b.HasIndex(q => q.TenantId);
            b.Property(q => q.ResourceType).HasConversion<string>().HasMaxLength(16);
            b.Property(q => q.Supplier).HasMaxLength(200).IsRequired();
            b.Property(q => q.Price).HasColumnType("numeric(18,4)");
            b.Property(q => q.Currency).HasMaxLength(3).IsRequired();
            b.Property(q => q.Unit).HasMaxLength(16);
            b.Property(q => q.Note).HasMaxLength(400);
            b.Property(q => q.AttachmentUrl).HasMaxLength(500);
            b.HasQueryFilter(q => q.TenantId == _tenant.TenantId);
        });

        // ── SubcontractorQuote (20.6 RFQ + external portal submission) ─────────
        mb.Entity<SubcontractorQuote>(b =>
        {
            // Token is the public capability credential — it must be unique ACROSS
            // tenants (no tenant prefix) so the anonymous portal endpoint can resolve
            // the owning tenant from the token alone.
            b.HasIndex(q => q.Token).IsUnique();
            b.HasIndex(q => new { q.TenantId, q.ProjectId });
            b.HasOne(q => q.Project).WithMany()
             .HasForeignKey(q => q.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.Property(q => q.Token).HasMaxLength(64).IsRequired();
            b.Property(q => q.Trade).HasMaxLength(120).IsRequired();
            b.Property(q => q.Scope).HasMaxLength(4000).IsRequired();
            b.Property(q => q.Currency).HasMaxLength(3).IsRequired();
            b.Property(q => q.ContractorName).HasMaxLength(200).IsRequired();
            b.Property(q => q.ContractorEmail).HasMaxLength(254);
            b.Property(q => q.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(q => q.QuotedAmount).HasColumnType("numeric(18,2)");
            b.Property(q => q.SubmissionNotes).HasMaxLength(2000);
            b.Property(q => q.RespondentName).HasMaxLength(200);
            b.HasQueryFilter(q => q.TenantId == _tenant.TenantId);
        });

        // ── TenantSamlConfig (20.8b SAML SSO) — 1:1 with Tenant ───────────────
        mb.Entity<TenantSamlConfig>(b =>
        {
            b.HasIndex(c => c.TenantId).IsUnique();   // one IdP config per tenant
            b.Property(c => c.IdpEntityId).HasMaxLength(1024);
            b.Property(c => c.IdpSsoUrl).HasMaxLength(2048);
            b.Property(c => c.IdpCertificatePem).HasColumnType("text");
            b.Property(c => c.EmailAttribute).HasMaxLength(256);
            b.Property(c => c.NameAttribute).HasMaxLength(256);
            b.HasQueryFilter(c => c.TenantId == _tenant.TenantId);
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

        mb.Entity<ProjectType>(b =>
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

        // ── DB-level tenant foreign keys (defense in depth) ──────────────────────
        // Application code already scopes every IHasTenant entity (global query filter
        // + TenantId auto-stamp in SaveChangesAsync), but that is the ONLY guard. Add
        // real FK constraints so the database itself refuses an orphaned or cross-tenant
        // TenantId — the last line of defense if a future IgnoreQueryFilters() slip or
        // raw SQL ever bypasses the application layer. RESTRICT because tenants are never
        // hard-deleted (suspend/activate only); a stray tenant delete must fail loudly
        // rather than silently cascade-wipe a workspace.
        //   • TenantSettings already owns an explicit 1:1 FK to Tenant — skip (no double FK).
        //   • AuditEvent may be written pre-auth with an empty tenant (e.g. failed logins),
        //     so it stays FK-free by design — see SaveChangesAsync.
        foreach (var et in mb.Model.GetEntityTypes())
        {
            var clr = et.ClrType;
            if (!typeof(IHasTenant).IsAssignableFrom(clr)) continue;
            if (clr == typeof(TenantSettings) || clr == typeof(AuditEvent)) continue;
            mb.Entity(clr)
              .HasOne(typeof(Tenant))
              .WithMany()
              .HasForeignKey(nameof(IHasTenant.TenantId))
              .OnDelete(DeleteBehavior.Restrict);
        }

        // User is not IHasTenant (TenantId is nullable — SuperAdmins are tenant-less),
        // so it needs its own nullable FK. A null TenantId (SuperAdmin) is exempt from the
        // constraint; any non-null value must point at a real tenant.
        mb.Entity<User>()
          .HasOne<Tenant>()
          .WithMany()
          .HasForeignKey(u => u.TenantId)
          .OnDelete(DeleteBehavior.Restrict);
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
    ///
    /// Also (20.2) invalidates pending approvals whenever an estimate's bid
    /// content changes — a sign-off must be on the bid the approver SAW. See
    /// <see cref="CollectEstimatesWithChangedContent"/> for the trigger set.
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

        // 20.2 — wipe approvals on any tracked bid-content change BEFORE the save.
        // Doing it on the same SaveChanges as the content keeps both atomic: if the
        // content write rolls back, the approvals also stay.
        var affected = CollectEstimatesWithChangedContent();
        if (affected.Count > 0)
        {
            // Bypass the query filter so a content-write that runs WITHOUT a tenant
            // resolved (only AuditEvent does that, and it never touches estimates)
            // would still scope correctly. Approvals carry TenantId.
            await EstimateApprovals
                .Where(a => affected.Contains(a.EstimateId))
                .ExecuteDeleteAsync(ct);
        }

        return await base.SaveChangesAsync(ct);
    }

    /// <summary>20.2 — Walk the ChangeTracker for any Added/Modified/Deleted
    /// entity belonging to an estimate's BID CONTENT (BOQ tree, preliminaries,
    /// markups, risks). Returns the distinct EstimateIds whose approvals
    /// therefore need to be wiped. Does NOT trigger on:
    ///   • Approvals themselves (they're the thing being invalidated)
    ///   • The Estimate row (title / status / FX snapshot are meta, not bid content)
    ///   • Project / Tenant / catalog rows
    /// Keeps the trigger set narrow so a no-op recompute doesn't churn approvals.
    ///
    /// BoqItem and ItemCostComponent don't carry EstimateId directly — they hang
    /// off BoqSection / BoqItem respectively. We resolve through the tracked
    /// graph first (the handler usually loads the parent on the same scope), then
    /// fall back to a single DB lookup per orphan.</summary>
    private HashSet<int> CollectEstimatesWithChangedContent()
    {
        var ids = new HashSet<int>();
        int? SectionEstimateId(int sectionId) =>
            ChangeTracker.Entries<BoqSection>().FirstOrDefault(e => e.Entity.Id == sectionId)?.Entity.EstimateId
            ?? BoqSections.IgnoreQueryFilters().Where(s => s.Id == sectionId).Select(s => (int?)s.EstimateId).FirstOrDefault();
        int? ItemEstimateId(int itemId)
        {
            var item = ChangeTracker.Entries<BoqItem>().FirstOrDefault(e => e.Entity.Id == itemId)?.Entity;
            var sid = item?.SectionId ?? BoqItems.IgnoreQueryFilters().Where(x => x.Id == itemId).Select(x => (int?)x.SectionId).FirstOrDefault();
            return sid is { } s ? SectionEstimateId(s) : null;
        }
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            switch (entry.Entity)
            {
                case BoqSection s:           ids.Add(s.EstimateId); break;
                case BoqItem i:              if (SectionEstimateId(i.SectionId) is { } eid) ids.Add(eid); break;
                case ItemCostComponent c:    if (ItemEstimateId(c.BoqItemId) is { } eid2) ids.Add(eid2); break;
                case Preliminary p:          ids.Add(p.EstimateId); break;
                case Markup m:               ids.Add(m.EstimateId); break;
                case RiskItem r:             ids.Add(r.EstimateId); break;
            }
        }
        return ids;
    }
}
