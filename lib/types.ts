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
  hasLogo: boolean
}

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
}
export interface ItemBreakdown {
  id: number; itemCode: string; description: string; unit: string
  quantity: number; assemblyId: number | null; unitRate: number; lineTotal: number; sortOrder: number
  components: ItemCostComponentBreakdown[]
  areaId: number | null
}

export interface Area { id: number; parentAreaId: number | null; name: string; code: string | null; kind: string; sortOrder: number }
export interface AreaRollupRow { id: number; parentAreaId: number | null; name: string; kind: string; directTotal: number; rollupTotal: number; itemCount: number }
export interface AreaRollup { currency: string; areas: AreaRollupRow[]; assignedTotal: number; unassignedTotal: number }

export interface CostComponentType {
  id: number; code: string; name: string; calcKind: string; sortOrder: number; isActive: boolean; builtin: boolean
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
export interface EstimateBreakdown {
  id: number; projectId: number; revision: number; title: string; status: string; currency: string
  directCost: number; indirectCost: number; markupCost: number; bidPrice: number
  sections: SectionBreakdown[]; preliminaries: PrelimBreakdown[]; markups: MarkupBreakdown[]
  rowVersion: string
  fx: FxView | null
}

export interface CurrencyRate { code: string; rateToBase: number; updatedAt: string }
export interface CurrencyRates { baseCurrency: string; rates: CurrencyRate[] }

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
