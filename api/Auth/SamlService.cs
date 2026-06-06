using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using BidBuilder.Api.Models;

namespace BidBuilder.Api.Auth;

/// <summary>
/// 20.8b — hand-rolled, deliberately STRICT SAML 2.0 Web-Browser-SSO support for
/// the SP (service-provider) side. We use .NET's first-party
/// <see cref="SignedXml"/> for signature verification rather than a heavyweight
/// third-party stack, and harden every well-known SAML pitfall:
///
///   • XXE — XML is parsed with DTD processing prohibited and no external resolver.
///   • Signature wrapping — we require exactly ONE &lt;Assertion&gt; and exactly ONE
///     &lt;Signature&gt;, the signature must be enveloped directly in that assertion,
///     its single Reference URI must point at that assertion's ID, and we read the
///     identity claims ONLY from that signed element.
///   • Key confusion — the signature is verified against the tenant-configured X.509
///     public key ONLY; the &lt;KeyInfo&gt; embedded in the response is ignored.
///   • Weak crypto — SHA-1 signature/digest methods are rejected.
///   • Transform abuse — only enveloped-signature + c14n transforms are allowed
///     (no XSLT/XPath transforms).
///   • Stale/forged audience — Conditions NotBefore/NotOnOrAfter, AudienceRestriction,
///     SubjectConfirmationData Recipient/NotOnOrAfter and the IdP Issuer are all checked.
///
/// Replay protection (single-use assertion IDs) is enforced by the caller, which has
/// access to a shared cache; this service returns the assertion ID + expiry for that.
/// </summary>
public static class SamlService
{
    private const string ProtoNs  = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string AssertNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string StatusSuccess = "urn:oasis:names:tc:SAML:2.0:status:Success";

    /// <summary>Tolerance for IdP/SP clock drift when checking time windows.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(3);

    // Common attribute Names IdPs use for email / display name, tried when the
    // tenant hasn't configured an explicit mapping.
    private static readonly string[] EmailAttrNames =
    {
        "urn:oid:0.9.2342.19200300.100.1.3",                                  // mail (LDAP OID)
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", // WS-* / ADFS
        "email", "Email", "mail", "EmailAddress",
    };
    private static readonly string[] NameAttrNames =
    {
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
        "urn:oid:2.16.840.1.113730.3.1.241",   // displayName
        "urn:oid:2.5.4.3",                      // cn
        "displayName", "name", "Name", "DisplayName", "cn",
    };

    /// <summary>The outcome of validating a SAML Response. On success, carries the
    /// authenticated email + optional display name and the assertion ID/expiry the
    /// caller uses for single-use replay protection.</summary>
    public record AssertionResult(
        bool Ok, string? Error,
        string? AssertionId = null, DateTime? NotOnOrAfter = null,
        string? Email = null, string? DisplayName = null, string? NameId = null);

    private static AssertionResult Fail(string error) => new(false, error);

    // ── SP-initiated: build the redirect to the IdP (HTTP-Redirect binding) ─────

    /// <summary>
    /// Build a SAML AuthnRequest and return the full IdP SSO URL to 302-redirect the
    /// browser to. The request is DEFLATE-compressed + base64 + URL-encoded into the
    /// <c>SAMLRequest</c> query parameter per the HTTP-Redirect binding. We do not sign
    /// the AuthnRequest — the security of the flow rests entirely on verifying the
    /// SIGNED assertion that comes back, which is the part that authenticates the user.
    /// </summary>
    public static string BuildRedirectUrl(
        TenantSamlConfig cfg, string spEntityId, string acsUrl,
        string requestId, DateTime nowUtc, string? relayState)
    {
        var instant = nowUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var xml =
            $"<samlp:AuthnRequest xmlns:samlp=\"{ProtoNs}\" xmlns:saml=\"{AssertNs}\" " +
            $"ID=\"{Esc(requestId)}\" Version=\"2.0\" IssueInstant=\"{instant}\" " +
            $"Destination=\"{Esc(cfg.IdpSsoUrl)}\" " +
            "ProtocolBinding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST\" " +
            $"AssertionConsumerServiceURL=\"{Esc(acsUrl)}\">" +
            $"<saml:Issuer>{Esc(spEntityId)}</saml:Issuer>" +
            "<samlp:NameIDPolicy Format=\"urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress\" AllowCreate=\"true\"/>" +
            "</samlp:AuthnRequest>";

        var encoded = Convert.ToBase64String(DeflateRaw(Encoding.UTF8.GetBytes(xml)));
        var sep = cfg.IdpSsoUrl.Contains('?') ? "&" : "?";
        var url = $"{cfg.IdpSsoUrl}{sep}SAMLRequest={Uri.EscapeDataString(encoded)}";
        if (!string.IsNullOrEmpty(relayState))
            url += $"&RelayState={Uri.EscapeDataString(relayState)}";
        return url;
    }

    /// <summary>SP metadata XML (EntityDescriptor) an admin can hand to their IdP to
    /// register this service provider — declares our EntityID and the ACS endpoint.</summary>
    public static string BuildSpMetadata(string spEntityId, string acsUrl) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<EntityDescriptor xmlns=\"urn:oasis:names:tc:SAML:2.0:metadata\" entityID=\"{Esc(spEntityId)}\">" +
        "<SPSSODescriptor protocolSupportEnumeration=\"urn:oasis:names:tc:SAML:2.0:protocol\" " +
        "AuthnRequestsSigned=\"false\" WantAssertionsSigned=\"true\">" +
        "<NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</NameIDFormat>" +
        "<AssertionConsumerService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST\" " +
        $"Location=\"{Esc(acsUrl)}\" index=\"0\" isDefault=\"true\"/>" +
        "</SPSSODescriptor></EntityDescriptor>";

    // ── ACS: validate the SAML Response the IdP POSTs back ─────────────────────

    /// <summary>
    /// Validate a base64-encoded SAML Response (HTTP-POST binding) against the tenant's
    /// configured IdP. Returns a populated <see cref="AssertionResult"/> on success, or
    /// one carrying <c>Ok=false</c> and an error message on any validation failure. Never
    /// throws on malformed input.
    /// </summary>
    public static AssertionResult ValidateResponse(
        string base64Response, TenantSamlConfig cfg, string spEntityId, string acsUrl, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(base64Response)) return Fail("Empty SAMLResponse.");

        byte[] raw;
        try { raw = Convert.FromBase64String(base64Response.Trim()); }
        catch { return Fail("SAMLResponse is not valid base64."); }

        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var sr = new StringReader(Encoding.UTF8.GetString(raw));
            using var xr = XmlReader.Create(sr, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,   // ← XXE / billion-laughs defense
                XmlResolver = null,
                MaxCharactersFromEntities = 1024,
            });
            doc.Load(xr);
        }
        catch { return Fail("SAMLResponse is not well-formed XML."); }

        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("samlp", ProtoNs);
        ns.AddNamespace("saml", AssertNs);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

        // 1. Top-level Status must be Success.
        var status = doc.SelectSingleNode("/samlp:Response/samlp:Status/samlp:StatusCode/@Value", ns)?.Value;
        if (status is null) return Fail("SAMLResponse has no Status.");
        if (!string.Equals(status, StatusSuccess, StringComparison.Ordinal))
            return Fail($"IdP returned a non-success status ({status}).");

        // 2. Exactly one Assertion — multiple is a wrapping attempt.
        var assertions = doc.SelectNodes("//saml:Assertion", ns);
        if (assertions is null || assertions.Count == 0) return Fail("SAMLResponse carries no assertion.");
        if (assertions.Count != 1) return Fail("SAMLResponse must carry exactly one assertion.");
        var assertion = (XmlElement)assertions[0]!;
        var assertionId = assertion.GetAttribute("ID");
        if (string.IsNullOrEmpty(assertionId)) return Fail("Assertion has no ID.");

        // 3. Exactly one Signature, enveloped in THE assertion.
        var sigs = doc.SelectNodes("//ds:Signature", ns);
        if (sigs is null || sigs.Count == 0) return Fail("SAMLResponse is not signed (assertion signature required).");
        if (sigs.Count != 1) return Fail("SAMLResponse carries more than one signature.");
        var sig = (XmlElement)sigs[0]!;
        if (!ReferenceEquals(sig.ParentNode, assertion))
            return Fail("The signature must be enveloped directly within the assertion.");

        // 4. Load + structurally vet the signature before any crypto.
        X509Certificate2 cert;
        try { cert = LoadCertificate(cfg.IdpCertificatePem); }
        catch { return Fail("The configured IdP certificate could not be parsed."); }

        using (cert)
        {
            var signedXml = new AssertionSignedXml(assertion);
            try { signedXml.LoadXml(sig); }
            catch { return Fail("The signature element is malformed."); }

            if (signedXml.SignedInfo?.References is not { Count: 1 })
                return Fail("The signature must cover exactly one reference.");
            var reference = (Reference)signedXml.SignedInfo.References[0]!;

            // The signed reference MUST be the assertion we will read claims from.
            if (reference.Uri != "#" + assertionId)
                return Fail("The signature does not cover the assertion.");

            // Transform allowlist — no XSLT/XPath transforms (a classic bypass vector).
            foreach (Transform t in reference.TransformChain)
            {
                if (t.Algorithm is null || !AllowedTransforms.Contains(t.Algorithm))
                    return Fail($"Disallowed signature transform: {t.Algorithm}");
            }
            // Reject SHA-1 signature/digest.
            if (IsWeak(signedXml.SignatureMethod) || IsWeak(reference.DigestMethod))
                return Fail("Weak signature algorithm (SHA-1) is not accepted.");

            // 5. Verify against the CONFIGURED key only (ignore embedded KeyInfo).
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is null) return Fail("The configured certificate has no RSA public key.");
            bool valid;
            try { valid = signedXml.CheckSignature(rsa); }
            catch { return Fail("Signature verification failed."); }
            if (!valid) return Fail("The assertion signature is invalid.");
        }

        // 6. From here we trust the assertion's contents (it is signed + verified).
        var issuer = assertion.SelectSingleNode("saml:Issuer", ns)?.InnerText?.Trim();
        if (!string.Equals(issuer, cfg.IdpEntityId, StringComparison.Ordinal))
            return Fail("The assertion issuer does not match the configured IdP.");

        DateTime? expiry = null;
        if (assertion.SelectSingleNode("saml:Conditions", ns) is XmlElement cond)
        {
            if (TryInstant(cond.GetAttribute("NotBefore"), out var nb) && nowUtc + ClockSkew < nb)
                return Fail("The assertion is not yet valid.");
            if (TryInstant(cond.GetAttribute("NotOnOrAfter"), out var na))
            {
                expiry = na;
                if (nowUtc - ClockSkew >= na) return Fail("The assertion has expired.");
            }
            var auds = cond.SelectNodes(".//saml:AudienceRestriction/saml:Audience", ns);
            if (auds is { Count: > 0 } &&
                !auds.Cast<XmlNode>().Any(a => string.Equals(a.InnerText.Trim(), spEntityId, StringComparison.Ordinal)))
                return Fail("The assertion audience does not include this service provider.");
        }

        if (assertion.SelectSingleNode(
                "saml:Subject/saml:SubjectConfirmation/saml:SubjectConfirmationData", ns) is XmlElement scd)
        {
            var recipient = scd.GetAttribute("Recipient");
            if (!string.IsNullOrEmpty(recipient) && !string.Equals(recipient.Trim(), acsUrl, StringComparison.Ordinal))
                return Fail("The assertion recipient does not match this ACS URL.");
            if (TryInstant(scd.GetAttribute("NotOnOrAfter"), out var sna))
            {
                if (nowUtc - ClockSkew >= sna) return Fail("The subject confirmation has expired.");
                expiry ??= sna;
            }
        }

        // 7. Extract identity.
        var nameId = assertion.SelectSingleNode("saml:Subject/saml:NameID", ns)?.InnerText?.Trim();
        string? email = null, name = null;

        var emailAttr = Norm(cfg.EmailAttribute);
        var nameAttr  = Norm(cfg.NameAttribute);
        var attrNodes = assertion.SelectNodes("saml:AttributeStatement/saml:Attribute", ns);
        if (attrNodes is not null)
        {
            foreach (XmlElement attr in attrNodes.OfType<XmlElement>())
            {
                var attrName = attr.GetAttribute("Name");
                var val = attr.SelectSingleNode("saml:AttributeValue", ns)?.InnerText?.Trim();
                if (string.IsNullOrEmpty(val)) continue;

                if (emailAttr is not null && string.Equals(attrName, emailAttr, StringComparison.Ordinal)) email = val;
                if (nameAttr  is not null && string.Equals(attrName, nameAttr,  StringComparison.Ordinal)) name  = val;
                if (email is null && emailAttr is null && EmailAttrNames.Contains(attrName)) email = val;
                if (name  is null && nameAttr  is null && NameAttrNames.Contains(attrName))  name  = val;
            }
        }

        // Fall back to NameID when it is an email address.
        if (string.IsNullOrWhiteSpace(email) && LooksLikeEmail(nameId)) email = nameId;
        if (string.IsNullOrWhiteSpace(email)) return Fail("The assertion did not carry an email address.");

        return new AssertionResult(true, null, assertionId, expiry,
            email!.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(name) ? null : name!.Trim(), nameId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> AllowedTransforms = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigEnvelopedSignatureTransformUrl,
        SignedXml.XmlDsigC14NTransformUrl,
        SignedXml.XmlDsigC14NWithCommentsTransformUrl,
        SignedXml.XmlDsigExcC14NTransformUrl,
        SignedXml.XmlDsigExcC14NWithCommentsTransformUrl,
    };

    private static bool IsWeak(string? algorithm) =>
        algorithm is not null && algorithm.Contains("sha1", StringComparison.OrdinalIgnoreCase);

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool LooksLikeEmail(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Contains('@') && s.IndexOf('@') < s.LastIndexOf('.');

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    private static bool TryInstant(string? value, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        utc = parsed.ToUniversalTime();
        return true;
    }

    private static X509Certificate2 LoadCertificate(string pem)
    {
        var s = (pem ?? "").Trim();
        if (s.Length == 0) throw new CryptographicException("Empty certificate.");
        if (s.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            return X509Certificate2.CreateFromPem(s);
        // Bare base64 DER (no PEM armor) — accept it too.
        return new X509Certificate2(Convert.FromBase64String(s));
    }

    /// <summary>Raw DEFLATE (RFC 1951, no zlib header) as required by the
    /// SAML HTTP-Redirect binding.</summary>
    private static byte[] DeflateRaw(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// <see cref="SignedXml"/> subclass that resolves SAML's <c>ID</c> attribute. The
    /// base implementation only recognises <c>Id</c>/<c>xml:id</c>, so an assertion
    /// referenced by <c>#&lt;ID&gt;</c> would otherwise not be found. We resolve ONLY to
    /// the assertion element we were constructed with — there is no dynamic lookup, so
    /// there is no XPath-injection or wrapping surface here.
    /// </summary>
    private sealed class AssertionSignedXml(XmlElement assertion) : SignedXml(assertion)
    {
        private readonly XmlElement _assertion = assertion;

        public override XmlElement? GetIdElement(XmlDocument? document, string idValue)
            => string.Equals(_assertion.GetAttribute("ID"), idValue, StringComparison.Ordinal)
                ? _assertion
                : base.GetIdElement(document, idValue);
    }
}
