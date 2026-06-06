using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BidBuilder.Api.Auth;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>
/// 20.8 — TOTP two-factor auth. Proves the RFC 6238 primitives, the enrol → confirm
/// → enforce-at-login lifecycle, single-use recovery codes, and that disabling needs
/// proof. Each test enrols a FRESH dedicated user (never the shared seed admin) so the
/// 2FA gate can't lock other tests out of the sequential host.
/// </summary>
[Collection("api")]
public class TwoFactorAuthTests(ApiFixture fx)
{
    private const string Pw = "Pw@123456";

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Create a fresh TenantUser and return (email, an authed client for them).</summary>
    private async Task<(string email, HttpClient client)> NewUserAsync()
    {
        var admin = await fx.AdminClientAsync();
        var email = $"mfa-{Guid.NewGuid():N}@bidbuilder.local";
        (await admin.PostAsJsonAsync("/api/admin/users",
            new { name = email, email, password = Pw, role = "TenantUser", groupIds = Array.Empty<int>() }))
            .EnsureSuccessStatusCode();
        return (email, await fx.AuthedClientAsync(email, Pw));
    }

    /// <summary>POST /api/auth/login with default tenant header (raw, so we can read mfaRequired).</summary>
    private async Task<(HttpStatusCode status, JsonElement body)> LoginAsync(object payload)
    {
        var c = fx.ClientForTenant("default");
        var r = await c.PostAsJsonAsync("/api/auth/login", payload);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (r.StatusCode, body);
    }

    /// <summary>Enrol the client's user in 2FA; return (base32 secret, recovery codes).</summary>
    private async Task<(string secret, string[] recovery)> EnrollAsync(HttpClient client)
    {
        var setup = await (await client.PostAsync("/api/auth/2fa/setup", null)).Json();
        var secret = setup.GetProperty("secret").GetString()!;
        var code = TotpService.ComputeCode(TotpService.Base32Decode(secret), Now);
        var enable = await client.PostAsJsonAsync("/api/auth/2fa/enable", new { code });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var codes = (await enable.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        return (secret, codes);
    }

    [Fact]
    public void Totp_primitives_roundtrip()
    {
        var secret = TotpService.NewSecret();
        var t = 1_700_000_000L;
        var code = TotpService.ComputeCode(secret, t);
        Assert.Equal(6, code.Length);
        Assert.True(code.All(char.IsAsciiDigit));
        Assert.True(TotpService.Verify(secret, code, t));
        Assert.True(TotpService.Verify(secret, code, t + 29));          // same 30s step
        Assert.True(TotpService.Verify(secret, code, t - TotpService.PeriodSeconds)); // ±1 window
        Assert.False(TotpService.Verify(secret, code, t + 5 * TotpService.PeriodSeconds)); // far away
        Assert.False(TotpService.Verify(secret, "000000", t));
        Assert.False(TotpService.Verify(secret, "12345", t));            // wrong length
        // base32 is reversible
        Assert.Equal(secret, TotpService.Base32Decode(TotpService.Base32Encode(secret)));
    }

    [Fact]
    public async Task Setup_then_enable_then_login_requires_a_code()
    {
        var (email, client) = await NewUserAsync();

        // Before enrolment, a plain password login works.
        var (preStatus, preBody) = await LoginAsync(new { email, password = Pw });
        Assert.Equal(HttpStatusCode.OK, preStatus);
        Assert.True(preBody.TryGetProperty("token", out _));

        var (secret, _) = await EnrollAsync(client);

        var status = await (await client.GetAsync("/api/auth/2fa/status")).Json();
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(10, status.GetProperty("recoveryCodesRemaining").GetInt32());

        // Password alone now yields a challenge, not a token.
        var (s1, b1) = await LoginAsync(new { email, password = Pw });
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.False(b1.TryGetProperty("token", out _));
        Assert.True(b1.GetProperty("mfaRequired").GetBoolean());

        // Wrong code → 401.
        var (s2, _) = await LoginAsync(new { email, password = Pw, totpCode = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, s2);

        // Correct code → token.
        var good = TotpService.ComputeCode(TotpService.Base32Decode(secret), Now);
        var (s3, b3) = await LoginAsync(new { email, password = Pw, totpCode = good });
        Assert.Equal(HttpStatusCode.OK, s3);
        Assert.True(b3.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task Recovery_code_logs_in_and_is_single_use()
    {
        var (email, client) = await NewUserAsync();
        var (_, recovery) = await EnrollAsync(client);
        var code = recovery[0];

        var (s1, b1) = await LoginAsync(new { email, password = Pw, recoveryCode = code });
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.True(b1.TryGetProperty("token", out _));

        // The same code cannot be reused.
        var (s2, _) = await LoginAsync(new { email, password = Pw, recoveryCode = code });
        Assert.Equal(HttpStatusCode.Unauthorized, s2);

        // One code consumed → nine remain.
        var status = await (await client.GetAsync("/api/auth/2fa/status")).Json();
        Assert.Equal(9, status.GetProperty("recoveryCodesRemaining").GetInt32());
    }

    [Fact]
    public async Task Disable_requires_proof_then_turns_off()
    {
        var (email, client) = await NewUserAsync();
        var (secret, _) = await EnrollAsync(client);

        // Wrong code can't disable.
        var bad = await client.PostAsJsonAsync("/api/auth/2fa/disable", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Correct code disables.
        var good = TotpService.ComputeCode(TotpService.Base32Decode(secret), Now);
        var off = await client.PostAsJsonAsync("/api/auth/2fa/disable", new { code = good });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);

        // Password-only login works again (fresh login — disable bumped TokenVersion).
        var (s, b) = await LoginAsync(new { email, password = Pw });
        Assert.Equal(HttpStatusCode.OK, s);
        Assert.True(b.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task Setup_is_refused_once_enabled()
    {
        var (_, client) = await NewUserAsync();
        await EnrollAsync(client);
        var again = await client.PostAsync("/api/auth/2fa/setup", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }
}
