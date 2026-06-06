namespace BidBuilder.Api.Models;

/// <summary>
/// 20.8b — per-tenant SAML 2.0 single-sign-on configuration (1:1 with Tenant).
///
/// A tenant admin registers their identity provider (Okta, Azure AD, Google
/// Workspace, ADFS …) here. When <see cref="Enabled"/> is true, members can sign
/// in via the IdP instead of (or in addition to) a local password: the SP-initiated
/// flow sends a SAML AuthnRequest to <see cref="IdpSsoUrl"/>, and the signed
/// assertion that comes back to the ACS endpoint is verified against
/// <see cref="IdpCertificatePem"/> before a BidBuilder session is issued.
///
/// There is exactly one row per tenant (unique index on TenantId), so this carries
/// no surrogate identity beyond <see cref="Id"/>.
/// </summary>
public class TenantSamlConfig : IHasTenant
{
    public Guid Id       { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Master switch. When false the SSO endpoints refuse the tenant and
    /// the "Sign in with SSO" affordance is hidden — password login is unaffected.</summary>
    public bool Enabled { get; set; }

    /// <summary>The IdP's EntityID (issuer). Every assertion's &lt;Issuer&gt; must equal
    /// this exactly, or it is rejected.</summary>
    public string IdpEntityId { get; set; } = "";

    /// <summary>The IdP's SSO service URL (HTTP-Redirect binding) we send the
    /// AuthnRequest to.</summary>
    public string IdpSsoUrl { get; set; } = "";

    /// <summary>The IdP's X.509 signing certificate, PEM-encoded (the
    /// "-----BEGIN CERTIFICATE-----" block). The assertion signature is verified
    /// against THIS key only — never the key embedded in the response's KeyInfo.</summary>
    public string IdpCertificatePem { get; set; } = "";

    /// <summary>SAML attribute name carrying the user's email (NameID is used when
    /// blank). Defaults to the common URI claim.</summary>
    public string? EmailAttribute { get; set; }

    /// <summary>SAML attribute name carrying the display name (optional).</summary>
    public string? NameAttribute { get; set; }

    /// <summary>When true, an assertion for an unknown email auto-provisions a new
    /// TenantUser (JIT). When false, only already-existing users may sign in via SSO.</summary>
    public bool AllowJitProvisioning { get; set; } = true;

    public DateTime  CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
