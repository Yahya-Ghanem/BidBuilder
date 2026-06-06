using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using BidBuilder.Api.Auth;
using BidBuilder.Api.Models;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.8b — SAML 2.0 SSO. Two layers of proof:
///   • Unit tests against <see cref="SamlService.ValidateResponse"/> with a real
///     self-signed cert + a genuinely XML-DSIG-signed assertion, covering the
///     happy path AND the security rejections (tamper, wrong key, wrong audience,
///     expired, unsigned).
///   • Integration tests driving the live ACS endpoint: admin config, JIT user
///     provisioning + session hand-off, tamper rejection, and single-use replay.
/// </summary>
[Collection("api")]
public class SamlSsoTests(ApiFixture fx)
{
    private const string Idp = "https://idp.bidbuilder.test/entity";
    private const string AssertNs = "urn:oasis:names:tc:SAML:2.0:assertion";

    // ── SamlService unit tests (the security core) ──────────────────────────────

    [Fact]
    public void Accepts_a_well_formed_signed_assertion()
    {
        using var cert = SelfSigned();
        var email = "alice@example.com";
        var resp = SignedResponse(cert, Idp, "sp-entity", "https://sp/acs", email, "Alice Example");

        var cfg = Config(cert);
        var r = SamlService.ValidateResponse(resp, cfg, "sp-entity", "https://sp/acs", DateTime.UtcNow);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(email, r.Email);
        Assert.Equal("Alice Example", r.DisplayName);
        Assert.False(string.IsNullOrEmpty(r.AssertionId));
    }

    [Fact]
    public void Rejects_a_tampered_assertion()
    {
        using var cert = SelfSigned();
        // Swap the NameID/email AFTER signing → the digest no longer matches.
        var resp = SignedResponse(cert, Idp, "sp-entity", "https://sp/acs", "good@example.com", null,
            tamperAfterSign: a =>
            {
                var nameId = a.GetElementsByTagName("NameID", AssertNs)[0]!;
                nameId.InnerText = "attacker@evil.com";
            });

        var r = SamlService.ValidateResponse(resp, Config(cert), "sp-entity", "https://sp/acs", DateTime.UtcNow);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rejects_an_unsigned_assertion()
    {
        using var cert = SelfSigned();
        var resp = SignedResponse(cert, Idp, "sp-entity", "https://sp/acs", "a@example.com", null, sign: false);
        var r = SamlService.ValidateResponse(resp, Config(cert), "sp-entity", "https://sp/acs", DateTime.UtcNow);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rejects_a_signature_from_a_different_key()
    {
        using var signer = SelfSigned();
        using var other = SelfSigned();   // config trusts THIS cert, but the response is signed by `signer`
        var resp = SignedResponse(signer, Idp, "sp-entity", "https://sp/acs", "a@example.com", null);
        var r = SamlService.ValidateResponse(resp, Config(other), "sp-entity", "https://sp/acs", DateTime.UtcNow);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rejects_a_wrong_audience()
    {
        using var cert = SelfSigned();
        var resp = SignedResponse(cert, Idp, "some-other-sp", "https://sp/acs", "a@example.com", null);
        var r = SamlService.ValidateResponse(resp, Config(cert), "sp-entity", "https://sp/acs", DateTime.UtcNow);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rejects_a_wrong_issuer()
    {
        using var cert = SelfSigned();
        var resp = SignedResponse(cert, "https://imposter.idp/entity", "sp-entity", "https://sp/acs", "a@example.com", null);
        var r = SamlService.ValidateResponse(resp, Config(cert), "sp-entity", "https://sp/acs", DateTime.UtcNow);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rejects_an_expired_assertion()
    {
        using var cert = SelfSigned();
        var past = DateTime.UtcNow.AddMinutes(-30);
        var resp = SignedResponse(cert, Idp, "sp-entity", "https://sp/acs", "a@example.com", null, notOnOrAfter: past);
        var r = SamlService.ValidateResponse(resp, Config(cert), "sp-entity", "https://sp/acs", DateTime.UtcNow);
        Assert.False(r.Ok);
    }

    // ── ACS endpoint + admin config integration ──────────────────────────────────

    [Fact]
    public async Task Admin_can_configure_sso_and_certificate_is_never_returned()
    {
        var admin = await fx.AdminClientAsync();
        using var cert = SelfSigned();

        var put = await admin.PutAsJsonAsync("/api/sso/config", new
        {
            enabled = true,
            idpEntityId = Idp,
            idpSsoUrl = "https://idp.bidbuilder.test/sso",
            idpCertificatePem = cert.ExportCertificatePem(),
            emailAttribute = (string?)null,
            nameAttribute = (string?)null,
            allowJitProvisioning = true,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var dto = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.GetProperty("enabled").GetBoolean());
        Assert.True(dto.GetProperty("hasCertificate").GetBoolean());
        Assert.False(dto.TryGetProperty("idpCertificatePem", out _));   // cert never echoed back
        Assert.EndsWith("/acs", dto.GetProperty("acsUrl").GetString());

        // GET round-trips without the secret.
        var get = await admin.GetFromJsonAsync<JsonElement>("/api/sso/config");
        Assert.True(get.GetProperty("hasCertificate").GetBoolean());
        Assert.Equal(Idp, get.GetProperty("idpEntityId").GetString());
    }

    [Fact]
    public async Task Enabling_without_a_certificate_is_rejected()
    {
        // Use a fresh second tenant that has no SAML config yet — the default tenant's
        // config may already carry a cert from another test in this sequential class,
        // and a stored cert is intentionally retained when the payload omits it.
        var admin = await fx.SecondTenantAdminClientAsync(slug: "sso-nocert", email: "admin@sso-nocert.local");
        var put = await admin.PutAsJsonAsync("/api/sso/config", new
        {
            enabled = true, idpEntityId = Idp, idpSsoUrl = "https://idp/sso",
            idpCertificatePem = (string?)null, allowJitProvisioning = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Config_is_admin_only()
    {
        var email = $"sso-nonadmin-{Guid.NewGuid():N}@bidbuilder.local";
        var admin = await fx.AdminClientAsync();
        (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = email, email, password = "Pw@123456", role = "TenantUser", groupIds = Array.Empty<int>() }))
            .EnsureSuccessStatusCode();
        var user = await fx.AuthedClientAsync(email, "Pw@123456");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/sso/config")).StatusCode);
        var put = await user.PutAsJsonAsync("/api/sso/config", new { enabled = false, allowJitProvisioning = true });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Acs_provisions_a_user_and_hands_back_a_working_session()
    {
        using var cert = SelfSigned();
        var (spEntityId, acsUrl) = await ConfigureSsoAsync(cert);

        var email = $"sso-{Guid.NewGuid():N}@example.com";
        var resp = SignedResponse(cert, Idp, spEntityId, acsUrl, email, "Jit Provisioned");

        var c = fx.NoRedirectClient();
        var post = await c.PostAsync("/api/auth/sso/default/acs", Form(resp));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var location = post.Headers.Location!.ToString();
        Assert.Contains("/login/sso#token=", location);

        // The handed-back JWT is a real, working BidBuilder session.
        var token = ExtractFragmentParam(location, "token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        var authed = fx.AnonymousClient();
        authed.DefaultRequestHeaders.Add("X-Tenant-Id", "default");
        authed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var ping = await authed.GetFromJsonAsync<JsonElement>("/api/ping");
        Assert.Equal("default", ping.GetProperty("tenant").GetString());

        // The JIT user now exists in the tenant with the IdP-supplied name.
        await fx.WithTenantDbAsync("default", async (db, _) =>
        {
            var u = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.Users, x => x.Email == email);
            Assert.NotNull(u);
            Assert.Equal("Jit Provisioned", u!.Name);
            Assert.Equal(UserRole.TenantUser, u.Role);
        });
    }

    [Fact]
    public async Task Acs_rejects_a_tampered_response()
    {
        using var cert = SelfSigned();
        var (spEntityId, acsUrl) = await ConfigureSsoAsync(cert);

        var resp = SignedResponse(cert, Idp, spEntityId, acsUrl, "victim@example.com", null,
            tamperAfterSign: a => a.GetElementsByTagName("NameID", AssertNs)[0]!.InnerText = "attacker@example.com");

        var c = fx.NoRedirectClient();
        var post = await c.PostAsync("/api/auth/sso/default/acs", Form(resp));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("/login?ssoError=", post.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Acs_rejects_a_replayed_response()
    {
        using var cert = SelfSigned();
        var (spEntityId, acsUrl) = await ConfigureSsoAsync(cert);

        var email = $"sso-replay-{Guid.NewGuid():N}@example.com";
        var resp = SignedResponse(cert, Idp, spEntityId, acsUrl, email, null);

        var c = fx.NoRedirectClient();
        var first = await c.PostAsync("/api/auth/sso/default/acs", Form(resp));
        Assert.Contains("/login/sso#token=", first.Headers.Location!.ToString());

        var second = await c.PostAsync("/api/auth/sso/default/acs", Form(resp));
        Assert.Contains("/login?ssoError=", second.Headers.Location!.ToString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Configure SSO on the default tenant and return its SP entityID + ACS URL
    /// (as the server computes them), so the test signs assertions the server will accept.</summary>
    private async Task<(string SpEntityId, string AcsUrl)> ConfigureSsoAsync(X509Certificate2 cert)
    {
        var admin = await fx.AdminClientAsync();
        var put = await admin.PutAsJsonAsync("/api/sso/config", new
        {
            enabled = true,
            idpEntityId = Idp,
            idpSsoUrl = "https://idp.bidbuilder.test/sso",
            idpCertificatePem = cert.ExportCertificatePem(),
            allowJitProvisioning = true,
        });
        put.EnsureSuccessStatusCode();
        var dto = await put.Content.ReadFromJsonAsync<JsonElement>();
        return (dto.GetProperty("spEntityId").GetString()!, dto.GetProperty("acsUrl").GetString()!);
    }

    private static TenantSamlConfig Config(X509Certificate2 cert) => new()
    {
        Enabled = true, IdpEntityId = Idp, IdpSsoUrl = "https://idp/sso",
        IdpCertificatePem = cert.ExportCertificatePem(), AllowJitProvisioning = true,
    };

    private static FormUrlEncodedContent Form(string samlResponse) =>
        new(new[] { new KeyValuePair<string, string>("SAMLResponse", samlResponse) });

    private static string? ExtractFragmentParam(string url, string key)
    {
        var hash = url.IndexOf('#');
        if (hash < 0) return null;
        foreach (var part in url[(hash + 1)..].Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == key) return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=BidBuilder Test IdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>Build a SAML Response whose single Assertion is genuinely XML-DSIG signed
    /// (RSA-SHA256, enveloped + exclusive-c14n) with the given cert's private key. Optionally
    /// skip signing or mutate the assertion AFTER signing (to simulate tampering).</summary>
    private static string SignedResponse(
        X509Certificate2 cert, string issuer, string audience, string recipient,
        string email, string? name,
        bool sign = true, DateTime? notOnOrAfter = null, Action<XmlElement>? tamperAfterSign = null)
    {
        string Inst(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        var nb = now.AddMinutes(-5);
        var na = notOnOrAfter ?? now.AddMinutes(10);
        var assertionId = "_" + Guid.NewGuid().ToString("N");
        var respId = "_" + Guid.NewGuid().ToString("N");

        var nameAttr = name is null ? "" :
            "<saml:Attribute Name=\"http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name\">" +
            $"<saml:AttributeValue>{System.Security.SecurityElement.Escape(name)}</saml:AttributeValue></saml:Attribute>";

        var xml =
            "<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" " +
            $"xmlns:saml=\"{AssertNs}\" ID=\"{respId}\" Version=\"2.0\" IssueInstant=\"{Inst(now)}\" Destination=\"{recipient}\">" +
            $"<saml:Issuer>{issuer}</saml:Issuer>" +
            "<samlp:Status><samlp:StatusCode Value=\"urn:oasis:names:tc:SAML:2.0:status:Success\"/></samlp:Status>" +
            $"<saml:Assertion ID=\"{assertionId}\" Version=\"2.0\" IssueInstant=\"{Inst(now)}\">" +
            $"<saml:Issuer>{issuer}</saml:Issuer>" +
            "<saml:Subject>" +
            $"<saml:NameID Format=\"urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress\">{email}</saml:NameID>" +
            "<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\">" +
            $"<saml:SubjectConfirmationData Recipient=\"{recipient}\" NotOnOrAfter=\"{Inst(na)}\"/>" +
            "</saml:SubjectConfirmation></saml:Subject>" +
            $"<saml:Conditions NotBefore=\"{Inst(nb)}\" NotOnOrAfter=\"{Inst(na)}\">" +
            $"<saml:AudienceRestriction><saml:Audience>{audience}</saml:Audience></saml:AudienceRestriction></saml:Conditions>" +
            $"<saml:AuthnStatement AuthnInstant=\"{Inst(now)}\"><saml:AuthnContext>" +
            "<saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:Password</saml:AuthnContextClassRef>" +
            "</saml:AuthnContext></saml:AuthnStatement>" +
            "<saml:AttributeStatement>" +
            "<saml:Attribute Name=\"http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress\">" +
            $"<saml:AttributeValue>{email}</saml:AttributeValue></saml:Attribute>{nameAttr}" +
            "</saml:AttributeStatement>" +
            "</saml:Assertion></samlp:Response>";

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        var assertion = (XmlElement)doc.GetElementsByTagName("Assertion", AssertNs)[0]!;

        if (sign)
        {
            using var rsa = cert.GetRSAPrivateKey()!;
            var signedXml = new IdSignedXml(doc) { SigningKey = rsa };
            signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
            signedXml.SignedInfo.SignatureMethod = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
            var reference = new Reference("#" + assertionId) { DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256" };
            reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
            reference.AddTransform(new XmlDsigExcC14NTransform());
            signedXml.AddReference(reference);
            var keyInfo = new KeyInfo();
            keyInfo.AddClause(new KeyInfoX509Data(cert));
            signedXml.KeyInfo = keyInfo;
            signedXml.ComputeSignature();
            var sigElem = (XmlElement)doc.ImportNode(signedXml.GetXml(), true);
            // SAML schema: Signature follows Issuer inside the assertion.
            var issuerEl = assertion["Issuer", AssertNs]!;
            assertion.InsertAfter(sigElem, issuerEl);
        }

        tamperAfterSign?.Invoke(assertion);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));
    }

    /// <summary>SignedXml that resolves SAML's <c>ID</c> attribute when signing the test
    /// assertion (the base class only recognises <c>Id</c>).</summary>
    private sealed class IdSignedXml(XmlDocument doc) : SignedXml(doc)
    {
        private readonly XmlDocument _doc = doc;
        public override XmlElement? GetIdElement(XmlDocument? document, string idValue)
        {
            foreach (XmlElement e in _doc.GetElementsByTagName("Assertion", AssertNs))
                if (e.GetAttribute("ID") == idValue) return e;
            return base.GetIdElement(document, idValue);
        }
    }
}
