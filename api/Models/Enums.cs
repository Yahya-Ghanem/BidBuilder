namespace BidBuilder.Api.Models;

/// <summary>Lifecycle of a bid opportunity.</summary>
public enum ProjectStatus
{
    Draft     = 0,
    Bidding   = 1,
    Submitted = 2,
    Won       = 3,
    Lost      = 4,
    Archived  = 5,
}

/// <summary>Lifecycle of a single estimate revision under a project.</summary>
public enum EstimateStatus
{
    Draft       = 0,
    UnderReview = 1,
    Published   = 2,
    Superseded  = 3,
}

/// <summary>Kind of resource that an assembly component draws on.</summary>
public enum ResourceType
{
    Labor        = 0,
    Material     = 1,
    Equipment    = 2,
    Subcontractor = 3,
}

/// <summary>The markup categories applied on top of direct + indirect cost.</summary>
public enum MarkupType
{
    Overhead    = 0,
    Profit      = 1,
    Contingency = 2,
    Escalation  = 3,
}

/// <summary>How a preliminary/site-overhead cost behaves over the project.</summary>
public enum PreliminaryKind
{
    Fixed       = 0,  // one-off (mobilization, insurance)
    TimeRelated = 1,  // per-month over project duration (site staff, facilities)
}

/// <summary>How a cost-component type contributes to a BOQ item's unit rate.</summary>
public enum CostCalcKind
{
    Amount  = 0,  // absolute money per unit (Material, Labor, Equipment …)
    Percent = 1,  // a percentage of the Amount-kind subtotal (Waste, Overheads …)
}

/// <summary>
/// How a BOQ line participates in the bid build-up. <see cref="Normal"/> and
/// <see cref="Daywork"/> are priced work that markups apply to. <see cref="ProvisionalSum"/>
/// and <see cref="PcSum"/> are carried in the bid at net cost but EXCLUDED from markup
/// (marking them up is a classic rejected-tender error). <see cref="Alternate"/> lines are
/// carried for the client's option but EXCLUDED from the base tender total.
/// </summary>
public enum BoqItemKind
{
    Normal         = 0,
    ProvisionalSum = 1,   // in the bid, not marked up
    PcSum          = 2,   // prime-cost sum — in the bid, not marked up
    Daywork        = 3,   // priced work — marked up like Normal
    Alternate      = 4,   // excluded from the base tender (carried separately)
}

/// <summary>The level a project Area node represents (a display label only;
/// the tree itself nests arbitrarily via ParentAreaId).</summary>
public enum AreaKind
{
    Area    = 0,
    SubArea = 1,
    Unit    = 2,
}
