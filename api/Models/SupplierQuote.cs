namespace BidBuilder.Api.Models;

/// <summary>
/// A live supplier quote in the tenant's register. Independent of the resource
/// library's "current" rate — quotes capture "supplier X offered Y per unit of Z,
/// valid until D" so an estimator can compare, audit, and pick the right number
/// at tender time. A quote may reference a library resource (so the estimator can
/// "use this quote" to update the resource rate + history) or stand alone.
///
/// Optionally carries an attachment reference (the supplier's PDF/email), stored
/// outside the DB; <see cref="AttachmentUrl"/> is a free-text pointer (S3 key,
/// SharePoint URL, file path) — file uploading is not part of this model.
/// </summary>
public class SupplierQuote : IHasTenant
{
    public int          Id           { get; set; }
    public Guid         TenantId     { get; set; }
    public ResourceType ResourceType { get; set; }
    /// <summary>Optional link to a library resource (null = stand-alone quote).</summary>
    public int?         ResourceId   { get; set; }
    public string       Supplier     { get; set; } = "";
    /// <summary>The quoted price in <see cref="Currency"/>. numeric(18,4).</summary>
    public decimal      Price        { get; set; }
    /// <summary>ISO-4217. Cross-rated to base via CurrencyRates when needed.</summary>
    public string       Currency     { get; set; } = "AED";
    public string       Unit         { get; set; } = "";
    public DateOnly     QuotedOn     { get; set; }
    /// <summary>Quote validity expiry. After this date the quote is "expired"
    /// (the engine flags it; it remains in the register for history/audit).</summary>
    public DateOnly?    ValidUntil   { get; set; }
    public string?      Note         { get; set; }
    public string?      AttachmentUrl{ get; set; }
    public DateTime     CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime     UpdatedAt    { get; set; } = DateTime.UtcNow;
}
