namespace BidBuilder.Api.Models;

/// <summary>
/// A manually-maintained FX rate for the tenant: the value of one unit of
/// <see cref="Code"/> in the tenant's base currency (<c>TenantSettings.BaseCurrency</c>)
/// — i.e. base-currency units per 1 unit of Code (the way exchange rates are normally
/// quoted, e.g. 1 USD = 3.6725 AED). The base currency itself is implicitly 1 and is
/// never stored here. A cross-rate is derived via the base: factor(S→T) = R(S) / R(T).
/// </summary>
public class CurrencyRate : IHasTenant
{
    public int      Id         { get; set; }
    public Guid     TenantId   { get; set; }
    public string   Code       { get; set; } = "";   // ISO-4217, upper-case
    public decimal  RateToBase { get; set; }          // base-currency units per 1 unit of Code
    public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;
}
