using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BidBuilder.Api.Models;
using BidBuilder.Api.Tenancy;

namespace BidBuilder.Api.Data;

/// <summary>
/// Applies pending migrations and seeds idempotent demo data on startup:
/// a "default" tenant, a TenantAdmin, the eight RBAC modules, a built-in
/// "Admins" team with full permissions, and one sample project assigned to it.
///
/// Runs OUTSIDE any HTTP request, so it resolves a tenant context manually
/// before touching tenant-scoped entities.
/// </summary>
public static class DbInitializer
{
    // The RBAC module registry for BidBuilder (Code, Name, sort).
    private static readonly (string Code, string Name)[] Modules =
    [
        ("projects",        "Projects"),
        ("resource-library","Resource Library"),
        ("assemblies",      "Assemblies"),
        ("boq",             "Bill of Quantities"),
        ("rate-analysis",   "Rate Analysis"),
        ("prelims-markups", "Preliminaries & Markups"),
        ("reports",         "Reports"),
        ("estimate-admin",  "Estimate Admin"),
    ];

    // Default cost-component types seeded per tenant (built-in, undeletable).
    private static readonly (string Code, string Name, CostCalcKind Kind)[] DefaultCostTypes =
    [
        ("MAT", "Material",  CostCalcKind.Amount),
        ("LAB", "Labor",     CostCalcKind.Amount),
        ("EQP", "Equipment", CostCalcKind.Amount),
        ("WST", "Waste",     CostCalcKind.Percent),
        ("OVH", "Overheads", CostCalcKind.Percent),
    ];

    // Common construction activities seeded per tenant (built-in, undeletable);
    // offered in the "add activity under a unit" dropdown. Tenants add their own.
    private static readonly string[] DefaultActivities =
    [
        "Site clearance", "Excavation", "Backfilling", "Anti-termite treatment",
        "Blinding (PCC)", "Reinforcement", "Formwork", "Concrete works",
        "Block work", "Plastering", "Screeding", "Waterproofing",
        "Floor tiling", "Wall tiling", "Painting", "False ceiling",
        "Gypsum partition", "Doors installation", "Aluminium & glazing", "Joinery & carpentry",
        "Electrical first fix", "Electrical second fix", "Plumbing first fix", "Plumbing second fix",
        "HVAC works", "Fire fighting", "Sanitary fixtures", "Cleaning & handover",
    ];

    // Common project types seeded as built-ins; offered when creating a project.
    // Tenants add their own and the list can grow.
    private static readonly string[] DefaultProjectTypes =
    [
        "Civil", "Structural", "Architectural", "Mechanical", "Electrical",
        "Plumbing", "HVAC", "Fire Fighting", "Infrastructure", "Fit-out",
        "Landscaping", "MEP",
    ];

    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var cfg    = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var env    = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var log    = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("DbInitializer");
        var isDev  = env.IsDevelopment();

        await db.Database.MigrateAsync();

        // ── Feature-flag catalogue (29.B.1) — platform tier, NOT IHasTenant ────
        // Default rollout 100 / Enabled true so the new toggle infrastructure is
        // a no-op on existing tenants; ops can dial back per-tenant or globally
        // through the admin API. Each flag describes one gateable surface.
        await SeedFeatureFlagsAsync(db);

        // ── Tenant (not IHasTenant — safe to write before tenant resolution) ──
        const string slug = "default";
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Slug == slug);
        if (t is null)
        {
            t = new Tenant {
                Id = Guid.NewGuid(), Slug = slug, Name = "Demo Contractor", DefaultLocale = "en",
                // 23.3 — portal signing key minted up-front so the first signed link doesn't lazy-write.
                PortalSigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
        }

        // Resolve the tenant for the rest of the seed so query filters + the
        // auto-stamp in SaveChanges target this tenant.
        if (!tenant.IsResolved) tenant.Set(t.Id, t.Slug);

        // Seed this tenant's standard defaults (settings, modules, catalogs, Admins
        // team). Shared with the platform "create tenant" flow so a tenant minted by a
        // SuperAdmin gets the exact same baseline.
        var adminGroup = await SeedTenantDefaultsAsync(db, t);

        // ── Admin user ─────────────────────────────────────────────────────────
        // Credentials are configurable (Seed:AdminEmail / Seed:AdminPassword). In
        // Development a well-known demo login is fine; outside Development we refuse
        // to seed a default credential — a password MUST be supplied explicitly, or
        // the initial admin is skipped (create it out-of-band). This keeps the famous
        // "Admin@12345" out of any real deployment.
        var adminEmail    = cfg["Seed:AdminEmail"]    ?? "admin@bidbuilder.local";
        var adminPassword = cfg["Seed:AdminPassword"] ?? (isDev ? "Admin@12345" : null);
        if (!await db.Users.AnyAsync(u => u.Email == adminEmail))
        {
            if (adminPassword is null)
            {
                log?.LogWarning(
                    "Skipping initial admin seed outside Development: set Seed__AdminPassword " +
                    "(and optionally Seed__AdminEmail) to create the first admin.");
            }
            else
            {
                var admin = new User
                {
                    TenantId = t.Id, Email = adminEmail,
                    Name = "Demo Admin", Role = UserRole.TenantAdmin, IsActive = true,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                };
                db.Users.Add(admin);
                await db.SaveChangesAsync();
                db.UserGroups.Add(new UserGroup { TenantId = t.Id, UserId = admin.Id, GroupId = adminGroup.Id });
                await db.SaveChangesAsync();
            }
        }

        // ── Sample project assigned to the Admins team ───────────────────────
        // Demo content is seeded in Development, or anywhere Seed:DemoData=true.
        var seedDemo = isDev || cfg.GetValue<bool>("Seed:DemoData");
        if (seedDemo && !await db.Projects.AnyAsync())
        {
            var project = new Project
            {
                TenantId = t.Id, Code = "PRJ-2026-001", Name = "Sample Tender — Warehouse Block A",
                ClientName = "ACME Industries", Location = "Abu Dhabi", Currency = "AED",
                Status = ProjectStatus.Bidding, DurationMonths = 9,
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            db.ProjectTeams.Add(new ProjectTeam
            {
                TenantId = t.Id, ProjectId = project.Id, GroupId = adminGroup.Id, IsLead = true,
            });
            db.Estimates.Add(new Estimate
            {
                TenantId = t.Id, ProjectId = project.Id, Revision = 1,
                Title = "Base Estimate", Status = EstimateStatus.Draft, Currency = "AED",
            });
            await db.SaveChangesAsync();
        }

        // ── Platform SuperAdmin ───────────────────────────────────────────────
        // A tenant-less platform operator (manages tenants via /api/platform). Like the
        // tenant admin, a default credential is only used in Development; elsewhere a
        // password MUST be supplied via Seed:SuperAdminPassword or the account is skipped.
        var superEmail    = cfg["Seed:SuperAdminEmail"]    ?? "superadmin@bidbuilder.local";
        var superPassword = cfg["Seed:SuperAdminPassword"] ?? (isDev ? "Super@12345" : null);
        // User isn't IHasTenant, so it's never tenant-stamped; the filter would hide a
        // null-tenant row, so look it up with filters off.
        if (superPassword is not null &&
            !await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == superEmail && u.TenantId == null))
        {
            db.Users.Add(new User
            {
                TenantId = null, Email = superEmail, Name = "Platform Admin",
                Role = UserRole.SuperAdmin, IsActive = true,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(superPassword),
            });
            await db.SaveChangesAsync();
        }
        else if (superPassword is null)
        {
            log?.LogWarning(
                "Skipping SuperAdmin seed outside Development: set Seed__SuperAdminPassword " +
                "(and optionally Seed__SuperAdminEmail) to create the platform operator.");
        }
    }

    /// <summary>
    /// Seed a tenant's standard baseline — settings, RBAC modules, the default
    /// cost-component / activity / project-type catalogs, and the built-in "Admins"
    /// team with full permissions on every module. Idempotent, so it also backfills
    /// older tenants. The caller MUST have resolved the tenant context to
    /// <paramref name="t"/> (so query filters + the SaveChanges auto-stamp target it).
    /// Returns the Admins group so the caller can attach the tenant's first admin.
    /// Shared by startup seeding and the platform "create tenant" endpoint.
    /// </summary>
    public static async Task<Group> SeedTenantDefaultsAsync(AppDbContext db, Tenant t)
    {
        // ── Tenant settings ──────────────────────────────────────────────────
        if (!await db.TenantSettings.AnyAsync())
        {
            db.TenantSettings.Add(new TenantSettings
            {
                TenantId = t.Id, BaseCurrency = "AED", Timezone = "Asia/Dubai",
                DefaultOverheadPct = 8m, DefaultProfitPct = 12m, DefaultContingencyPct = 5m,
            });
        }

        // ── Modules ────────────────────────────────────────────────────────────
        var existingCodes = await db.Modules.Select(m => m.Code).ToListAsync();
        for (int i = 0; i < Modules.Length; i++)
        {
            var (code, name) = Modules[i];
            if (existingCodes.Contains(code)) continue;
            db.Modules.Add(new Module { TenantId = t.Id, Code = code, Name = name, SortOrder = i, IsActive = true });
        }
        await db.SaveChangesAsync();

        // ── Default cost-component types (idempotent — also backfills existing tenants) ──
        var costCodes = await db.CostComponentTypes.Select(c => c.Code).ToListAsync();
        for (int i = 0; i < DefaultCostTypes.Length; i++)
        {
            var (code, name, kind) = DefaultCostTypes[i];
            if (costCodes.Contains(code)) continue;
            db.CostComponentTypes.Add(new CostComponentType
            {
                TenantId = t.Id, Code = code, Name = name, CalcKind = kind,
                SortOrder = i, IsActive = true, Builtin = true,
            });
        }
        await db.SaveChangesAsync();

        // ── Default activities (idempotent — also backfills existing tenants) ──
        var activityNames = await db.ActivityTypes.Select(a => a.Name).ToListAsync();
        for (int i = 0; i < DefaultActivities.Length; i++)
        {
            var name = DefaultActivities[i];
            if (activityNames.Contains(name)) continue;
            db.ActivityTypes.Add(new ActivityType { TenantId = t.Id, Name = name, SortOrder = i, IsActive = true, Builtin = true });
        }
        await db.SaveChangesAsync();

        // ── Default project types (idempotent — also backfills existing tenants) ──
        var projectTypeNames = await db.ProjectTypes.Select(a => a.Name).ToListAsync();
        for (int i = 0; i < DefaultProjectTypes.Length; i++)
        {
            var name = DefaultProjectTypes[i];
            if (projectTypeNames.Contains(name)) continue;
            db.ProjectTypes.Add(new ProjectType { TenantId = t.Id, Name = name, SortOrder = i, IsActive = true, Builtin = true });
        }
        await db.SaveChangesAsync();

        // ── Built-in "Admins" team with full permissions on every module ──────
        var adminGroup = await db.Groups.FirstOrDefaultAsync(g => g.Code == "ADMINS");
        if (adminGroup is null)
        {
            adminGroup = new Group
            {
                TenantId = t.Id, Code = "ADMINS", Name = "Administrators",
                Description = "Full access", IsBuiltIn = true,
            };
            db.Groups.Add(adminGroup);
            await db.SaveChangesAsync();

            var allModules = await db.Modules.ToListAsync();
            foreach (var m in allModules)
            {
                db.GroupModules.Add(new GroupModule
                {
                    TenantId = t.Id, GroupId = adminGroup.Id, ModuleId = m.Id,
                    CanView = true, CanAdd = true, CanEdit = true, CanDelete = true,
                });
            }
            await db.SaveChangesAsync();
        }

        return adminGroup;
    }

    // 29.B.1 — Initial flag catalogue. Each entry is idempotent on Key (we only
    // add missing rows; never overwrite an existing flag's Enabled / Rollout /
    // Overrides — those are operator decisions made through the admin UI).
    private static readonly (string Key, string Description)[] DefaultFlags =
    [
        ("bid-letter-templates",  "28.4 — Multi-style bid-letter picker on the export modal."),
        ("anomaly-panel",         "23.5 — Cost-anomaly side panel on the estimate editor."),
        ("export-preview",        "28.3 — Inline preview before downloading a BOQ export."),
    ];

    private static async Task SeedFeatureFlagsAsync(AppDbContext db)
    {
        var existing = await db.FeatureFlags.Select(f => f.Key).ToListAsync();
        foreach (var (key, description) in DefaultFlags)
        {
            if (existing.Contains(key)) continue;
            db.FeatureFlags.Add(new Models.FeatureFlag
            {
                Key = key,
                Description = description,
                Enabled = true,
                RolloutPercentage = 100,
                Overrides = new(),
            });
        }
        await db.SaveChangesAsync();
    }
}
