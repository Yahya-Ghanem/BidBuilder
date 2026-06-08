/** Shapes returned by the BidBuilder API (mirrors the .NET DTOs). */

export interface AuthUser {
  id: number
  name: string
  email: string
  role: string
}

export interface LoginResponse {
  token: string
  expiresAt: string
  user: AuthUser
}

/** 20.8 — per-user TOTP two-factor state + enrolment payloads. */
export interface TwoFactorStatus {
  enabled: boolean
  pending: boolean
  recoveryCodesRemaining: number
}
export interface TotpSetupResponse {
  /** base32 shared secret (also embedded in otpauthUri). */
  secret: string
  /** otpauth:// URI an authenticator app scans. */
  otpauthUri: string
}
export interface RecoveryCodesResponse {
  recoveryCodes: string[]
}

export interface Project {
  id: number
  code: string
  name: string
  clientName: string | null
  location: string | null
  currency: string
  status: string
  durationMonths: number | null
  tenderDueAt: string | null
  teamCount: number
  estimateCount: number
  projectTypeId: number | null
  projectTypeName: string | null
}

/** 27.5 — the signed-in user's sidebar personalisation (pinned + recent projects). */
export interface UserPreferences {
  pinnedProjectIds: number[]
  recentProjectIds: number[]
}

export interface ModulePermission {
  code: string
  name: string
  canView: boolean
  canAdd: boolean
  canEdit: boolean
  canDelete: boolean
}

export interface MePermissions {
  role: string
  isAdmin: boolean
  modules: ModulePermission[]
}

export interface AuditEntry {
  id: number
  at: string
  actorEmail: string | null
  actorName: string | null
  actorRole: string | null
  action: string
  entity: string
  entityKey: string | null
  summary: string | null
}

export interface AuditPage {
  total: number
  take: number
  skip: number
  items: AuditEntry[]
}

export interface TenantSettings {
  companyName: string
  website: string | null
  contactEmail: string | null
  phone: string | null
  address: string | null
  city: string | null
  country: string | null
  timezone: string
  baseCurrency: string
  defaultOverheadPct: number
  defaultProfitPct: number
  defaultContingencyPct: number
  defaultTaxRatePct: number
  hasLogo: boolean
  /** 20.2 — sign-offs required before Draft→Published. 0 = no workflow. */
  requiredApprovalsToPublish: number
  /** 20.11 — the tenant's vanity host, or null if none is registered. */
  customDomain: string | null
  /** 21.1 — whether this tenant emails notification copies (admin-toggleable). */
  notificationEmailsEnabled: boolean
  /** 21.1 — read-only: whether the platform's SMTP transport is configured. */
  emailConfigured: boolean
  /** 24.5 — multi-paragraph branding text rendered on bid letters. Null when unset. */
  brandHeaderText: string | null
  brandFooterText: string | null
  brandSignatureText: string | null
}

/** 22.1 — A user's email-digest opt-in. Frequency is "Off" | "Daily" | "Weekly".
 *  23.1 — `dayOfWeek` (Sunday…Saturday) gates the weekly send to a chosen weekday. */
export interface DigestPreference {
  frequency: string
  dayOfWeek: string
  lastSentAt: string | null
  /** Read-only: whether the platform SMTP transport is configured (else digests can't send). */
  emailConfigured: boolean
}

/** 23.1 — Preview body the next digest would carry, without sending or
 *  advancing the user's watermark. `itemCount` is the number of pending notifications. */
export interface DigestPreviewDto {
  frequency: string
  itemCount: number
  subject: string
  body: string
}

/** 23.4 — One monthly bucket of the rate-trend series. */
export interface RateTrendPoint { month: string; rate: number }
/** 23.4 — 24-month rate trend + summary stats for one library resource.
 *  `volatilityIndex` is stddev / mean over the series (coefficient of variation);
 *  `min12m` / `max12m` are over the trailing 12 months. Either may be null when
 *  the series is too thin. */
export interface RateTrendDto {
  resourceType: string
  resourceId: number
  currentRate: number
  min12m: number | null
  max12m: number | null
  volatilityIndex: number | null
  points: RateTrendPoint[]
}

/** 23.5 — One BOQ line flagged as anomalous in a tenant cost-anomaly scan. */
export interface AnomalyItem {
  itemId: number
  sectionId: number
  itemCode: string
  description: string
  unit: string
  quantity: number
  unitRate: number
  lineTotal: number
  historicalMean: number
  historicalMedian: number
  zScore: number
  medianMultiple: number
  severity: "high" | "medium"
  reason: string
  sampleSize: number
}

/** 23.5 — Cost-anomaly scan summary for an estimate. */
export interface AnomalyReport {
  estimateId: number
  itemsScanned: number
  itemsFlagged: number
  items: AnomalyItem[]
}

/** 21.2 — A programmatic API key (never carries the secret after creation).
 *  22.2 adds scopes, an optional per-minute rate limit, and live usage.
 *  23.2 adds an optional CIDR allowlist (empty array = any IP). */
export interface ApiKey {
  id: number
  name: string
  prefix: string
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
  revoked: boolean
  /** 22.2 — granted scopes, e.g. ["read","write"]. */
  scopes: string[]
  /** 22.2 — per-minute request cap, or null when unlimited. */
  rateLimitPerMinute: number | null
  /** 22.2 — requests counted against this key in the current minute (live). */
  usageThisMinute: number
  /** 23.2 — CIDRs the key may be presented from. Empty = any IP. */
  ipAllowlist: string[]
}

/** 21.2 — The create response — the only time the raw secret is returned. */
export interface ApiKeyCreated {
  key: ApiKey
  secret: string
}

/** 21.3 — A reusable estimate template (structure snapshot; counts for the list).
 *  22.3 adds library metadata: category, tags, a featured pin, and the author. */
export interface EstimateTemplate {
  id: number
  name: string
  description: string | null
  sectionCount: number
  itemCount: number
  createdAt: string
  /** 22.3 — optional grouping label. */
  category: string | null
  /** 22.3 — free-form tags. */
  tags: string[]
  /** 22.3 — pinned to the top of the picker by a tenant admin. */
  isFeatured: boolean
  /** 22.3 — name of the user who created it. */
  createdByName: string | null
}

/** 20.2 — One recorded sign-off on an estimate revision. */
export interface ApprovalRow {
  id: number; approverUserId: number; approverEmail: string; approverName: string
  approvedAt: string; note: string | null
}
/** 20.2 — The full approvals view for an estimate. */
export interface ApprovalsView {
  requiredApprovals: number
  currentApprovals: number
  approvals: ApprovalRow[]
}

/** 20.5 — one estimate column in a side-by-side bid comparison. */
export interface CompareColumn {
  estimateId: number; projectId: number; projectCode: string; projectName: string
  revision: number; title: string; status: string; currency: string
  directCost: number; indirectCost: number; markupCost: number; bidPrice: number
  taxRatePct: number | null; taxAmount: number; bidPriceInclTax: number
  alternatesTotal: number; marginOnPricePct: number; commercialAdjustment: number
}
/** 20.5 — one section total aligned across the compared estimates (null = absent). */
export interface CompareSectionRow { key: string; code: string; title: string; totals: (number | null)[] }
/** 20.5 — full side-by-side comparison. */
export interface CompareView { mixedCurrency: boolean; columns: CompareColumn[]; sections: CompareSectionRow[] }

/** 27.1 — one comment on a BOQ line. */
export interface BoqLineComment {
  id: number
  body: string
  authorUserId: number
  authorName: string
  authorEmail: string
  parentCommentId: number | null
  createdAt: string
  /** Null = open; ISO timestamp = resolved (drops out of the row's open-count badge). */
  resolvedAt: string | null
}
/** 27.1 — open-comments count per BOQ item for the row-level icon badge. */
export interface BoqCommentCount { itemId: number; openCount: number }

/** 20.3 — one in-app notification in the bell dropdown. */
export interface NotificationItem {
  id: number; type: string; title: string; body: string | null; link: string | null
  entityType: string | null; entityKey: string | null; isRead: boolean; createdAt: string
  // 27.4 — set when the recipient marked it read (null while unread).
  readAt: string | null
}
/** 20.3 — the signed-in user's inbox page + unread total. */
export interface NotificationList { unreadCount: number; items: NotificationItem[] }

/** 20.4 — one cross-project search match. */
export interface SearchHit { title: string; subtitle: string | null; link: string }
/** 20.4 — a typed cluster of search hits. */
export interface SearchGroup { type: string; label: string; hits: SearchHit[] }
/** 20.4 — the full search result set. */
export interface SearchResults { query: string; groups: SearchGroup[] }

/** 20.6 — a subcontractor RFQ + its returned quote, as the estimator sees it
 * (includes the portal token/link, which the estimator re-shares). */
export interface SubcontractorQuote {
  id: number; projectId: number; projectCode: string; projectName: string
  trade: string; scope: string; currency: string
  contractorName: string; contractorEmail: string | null
  expiresAt: string; expired: boolean; status: string
  quotedAmount: number | null; submissionNotes: string | null; respondentName: string | null
  submittedAt: string | null; decidedAt: string | null
  token: string; portalPath: string; createdAt: string
}
/** 20.6 — the safe view an anonymous subcontractor sees at /portal/[token]. */
export interface SubcontractorPortalView {
  companyName: string; trade: string; scope: string; currency: string
  contractorName: string; expiresAt: string; expired: boolean; status: string
  quotedAmount: number | null; submissionNotes: string | null; respondentName: string | null
  submittedAt: string | null
}

/** 20.9 — an outbound webhook subscription (secret never returned after create). */
export interface WebhookSubscription {
  id: number; url: string; events: string; isActive: boolean; createdAt: string
  lastStatus: string | null; lastAttemptAt: string | null; failureCount: number
}
/** 20.9 — create response, which includes the signing secret exactly once. */
export interface WebhookCreated extends WebhookSubscription { secret: string }

export interface ProjectTeam {
  groupId: number
  groupCode: string
  groupName: string
  isLead: boolean
}

export interface GroupOption {
  id: number
  code: string
  name: string
  memberCount: number
  isBuiltIn: boolean
}

export interface EstimateSummary {
  id: number
  revision: number
  title: string
  status: string
  currency: string
  bidPrice: number
  updatedAt: string
}

export interface ItemCostComponentBreakdown {
  typeId: number; code: string; name: string; calcKind: string; value: number; amount: number
  quantity: number | null; rate: number | null
}
export interface ItemBreakdown {
  id: number; itemCode: string; description: string; unit: string
  quantity: number; assemblyId: number | null; unitRate: number; lineTotal: number; sortOrder: number
  components: ItemCostComponentBreakdown[]
  areaId: number | null
  kind: string
}

export interface Area { id: number; parentAreaId: number | null; name: string; code: string | null; kind: string; sortOrder: number; quantity: number; unit: string | null }
export interface AreaRollupRow { id: number; parentAreaId: number | null; name: string; kind: string; directTotal: number; rollupTotal: number; itemCount: number; quantity: number; unit: string | null; costPerUnit: number | null }
export interface AreaRollup { currency: string; areas: AreaRollupRow[]; assignedTotal: number; unassignedTotal: number }

export interface CostComponentType {
  id: number; code: string; name: string; calcKind: string; sortOrder: number; isActive: boolean; builtin: boolean
}
export interface ActivityType {
  id: number; name: string; sortOrder: number; isActive: boolean; builtin: boolean
}
export interface ProjectType {
  id: number; name: string; sortOrder: number; isActive: boolean; builtin: boolean
}
export interface SectionBreakdown {
  id: number; code: string; title: string; sortOrder: number; sectionTotal: number; items: ItemBreakdown[]
}
export interface PrelimBreakdown {
  id: number; description: string; kind: string; amount: number; computedTotal: number; sortOrder: number
}
export interface MarkupBreakdown {
  id: number; type: string; label: string | null; percentage: number; applyOrder: number; computedAmount: number
}
export interface FxView {
  secondaryCurrency: string
  rate: number
  convertedBidPrice: number
  frozen: boolean
  frozenAt: string | null
}
export interface RiskBreakdown {
  id: number; title: string; category: string; probabilityPct: number; impactAmount: number
  expectedValue: number; note: string | null; sortOrder: number
}
export interface CashFlowMonth { month: number; spend: number; cumulative: number }
export interface CashFlowProjection { durationMonths: number; total: number; monthly: CashFlowMonth[] }

export interface EstimateBreakdown {
  id: number; projectId: number; revision: number; title: string; status: string; currency: string
  directCost: number; indirectCost: number; markupCost: number; bidPrice: number
  taxRatePct: number | null; taxAmount: number; bidPriceInclTax: number; alternatesTotal: number; marginOnPricePct: number; commercialAdjustment: number
  /** ISO date (yyyy-mm-dd). When set, the engine resolved rates via ResourceRateHistory. */
  pricingDate: string | null
  sections: SectionBreakdown[]; preliminaries: PrelimBreakdown[]; markups: MarkupBreakdown[]
  /** 19.2 — risk register; sum of EVs feeds suggestedContingency*. */
  risks: RiskBreakdown[]
  suggestedContingencyAmount: number
  suggestedContingencyPct: number
  cashFlow: CashFlowProjection
  rowVersion: string
  fx: FxView | null
  /** 25.5 — Percent change of each cached total against revision N-1 in the
   *  same project. Null when this IS revision 1 (no previous to compare to).
   *  Each per-card percent is null when the previous value was zero
   *  (div-by-0 guard) — the frontend reads null as "no badge for this card". */
  previousDelta: PreviousRevisionDelta | null
}

/** 25.5 — Per-card percent change vs. the immediately-prior revision.
 *  Positive = current is HIGHER than previous (frontend decides the color
 *  based on whether that direction is "better" for that particular card). */
export interface PreviousRevisionDelta {
  fromRevision: number
  directCostPct: number | null
  indirectCostPct: number | null
  markupCostPct: number | null
  bidPricePct: number | null
  bidPriceInclTaxPct: number | null
}

// ── Dated resource rates + supplier quotes (18.3) ────────────────────────────
export interface RateHistoryRow {
  id: number; resourceType: string; resourceId: number
  effectiveFrom: string  // ISO date
  rate: number; source: string | null; createdAt: string
}
// ── Bid analytics + outcome register (19.1) ─────────────────────────────────
export interface BidRegisterRow {
  projectId: number
  code: string
  name: string
  clientName: string | null
  currency: string
  projectTypeId: number | null
  projectTypeName: string | null
  status: string
  tenderDueAt: string | null
  decisionAt: string | null
  submittedBidValue: number | null
  awardedValue: number | null
  finalCost: number | null
  /** (award − bid) / bid × 100, 2dp. Null when either side is missing. */
  bidVsAwardPct: number | null
  winLossNote: string | null
}
export interface BidAnalyticsBucket {
  dimension: "project-type" | "client" | "period" | "overall"
  key: string
  total: number; won: number; lost: number
  hitRatePct: number
  avgBidVsAwardPct: number | null
  awardedValueSum: number
  mixedCurrency: boolean
}
export interface BidAnalyticsResult {
  overall: BidAnalyticsBucket
  byProjectType: BidAnalyticsBucket[]
  byClient: BidAnalyticsBucket[]
  byPeriod: BidAnalyticsBucket[]
  register: BidRegisterRow[]
}

export interface SupplierQuote {
  id: number; resourceType: string; resourceId: number | null
  supplier: string; price: number; currency: string; unit: string
  quotedOn: string; validUntil: string | null; isExpired: boolean
  note: string | null; attachmentUrl: string | null; updatedAt: string
}

export interface CurrencyRate { code: string; rateToBase: number; updatedAt: string }
export interface CurrencyRates { baseCurrency: string; rates: CurrencyRate[] }

// ── User & team administration ─────────────────────────────────────────────
export interface GroupRef { id: number; code: string; name: string }
export interface AdminUser {
  id: number; name: string; email: string; role: string; isActive: boolean
  createdAt: string; lastLoginAt: string | null; groups: GroupRef[]
}
export interface ModuleRef { id: number; code: string; name: string }
export interface GroupPerm {
  moduleId: number; canView: boolean; canAdd: boolean; canEdit: boolean; canDelete: boolean
}
export interface AdminGroup {
  id: number; code: string; name: string; description: string | null
  isBuiltIn: boolean; memberCount: number; permissions: GroupPerm[]
}

// ── Cross-project benchmarking ──────────────────────────────────────────────
export interface BenchmarkPoint {
  projectCode: string; projectName: string; areaName: string; kind: string; unit: string
  quantity: number; total: number; costPerUnit: number; currency: string
  costPerUnitBase: number | null   // cost/unit normalized to the tenant base currency (null = no rate)
}
export interface BenchmarkUnitGroup {
  unit: string; count: number; convertibleCount: number; baseCurrency: string
  min: number | null; avg: number | null; max: number | null   // in baseCurrency, over convertible points
  currencies: string[]; points: BenchmarkPoint[]
}
export interface ProjectBenchmark {
  projectId: number; code: string; name: string; currency: string
  estimateTitle: string | null; revision: number | null; status: string | null; bidPrice: number
}
export interface BenchmarkResult { baseCurrency: string; projects: ProjectBenchmark[]; units: BenchmarkUnitGroup[] }

export interface WhatIfMarkupLine {
  type: string
  label: string | null
  percentage: number
  applyOrder: number
  computedAmount: number
}
export interface WhatIfResult {
  directCost: number
  indirectCost: number
  markupCost: number
  bidPrice: number
  baselineBidPrice: number
  markups: WhatIfMarkupLine[]
}

export interface ImportResult {
  sectionsAdded: number
  itemsAdded: number
  estimate: EstimateBreakdown
}

export interface ResourceRow {
  id: number; code: string; name: string; unit: string; isActive: boolean
  ratePerHour?: number; unitPrice?: number; wastagePct?: number; supplier?: string | null; unitRate?: number
}
/** 20.12 — one resource a bulk action could not apply to. */
export interface BulkSkip { id: number; reason: string }
/** 20.12 — outcome of a bulk activate/deactivate/delete over the resource library. */
export interface BulkResourceResult { action: string; updated: number; deleted: number; skipped: BulkSkip[] }
export interface AssemblyRow {
  id: number; code: string; name: string; unit: string; computedRate: number; isActive: boolean; componentCount: number
}
export interface ComponentRow {
  id: number; resourceType: string; resourceId: number; resourceCode: string; resourceName: string
  factor: number; resourceRate: number; cost: number; note: string | null; sortOrder: number
}
export interface AssemblyDetail {
  id: number; code: string; name: string; unit: string; computedRate: number; isActive: boolean; components: ComponentRow[]
}

/** 20.10 — AI-assisted rate suggestion returned by GET /api/ai/rate-suggestion. */
export interface ComparableRate {
  name: string
  unit: string
  rate: number
  source: string
}
export interface RateSuggestion {
  suggestedRate: number
  confidence: "high" | "medium" | "low"
  basis: "historical" | "benchmark"
  comparables: ComparableRate[]
}

/** 20.8b — tenant SAML SSO configuration (admin view; the certificate is write-only,
 * never returned, so only its presence is exposed). The SP-side URLs are computed by
 * the API for the admin to register with their IdP. */
export interface SamlConfig {
  enabled: boolean
  idpEntityId: string
  idpSsoUrl: string
  hasCertificate: boolean
  emailAttribute: string | null
  nameAttribute: string | null
  allowJitProvisioning: boolean
  spEntityId: string
  acsUrl: string
  metadataUrl: string
  loginUrl: string
}
