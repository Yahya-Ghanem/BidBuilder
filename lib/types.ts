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

/** 20.3 — one in-app notification in the bell dropdown. */
export interface NotificationItem {
  id: number; type: string; title: string; body: string | null; link: string | null
  entityType: string | null; entityKey: string | null; isRead: boolean; createdAt: string
}
/** 20.3 — the signed-in user's inbox page + unread total. */
export interface NotificationList { unreadCount: number; items: NotificationItem[] }

/** 20.4 — one cross-project search match. */
export interface SearchHit { title: string; subtitle: string | null; link: string }
/** 20.4 — a typed cluster of search hits. */
export interface SearchGroup { type: string; label: string; hits: SearchHit[] }
/** 20.4 — the full search result set. */
export interface SearchResults { query: string; groups: SearchGroup[] }

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
