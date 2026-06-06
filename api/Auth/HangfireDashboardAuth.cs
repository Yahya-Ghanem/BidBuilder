using System.Text;
using Hangfire.Dashboard;

namespace BidBuilder.Api.Auth;

/// <summary>
/// Authorization filter for the Hangfire dashboard at <c>/hangfire</c>.
///
/// The dashboard is a separate browser-targeted UI (cookies, HTML pages) that
/// doesn't fit our JWT-Bearer auth scheme. We protect it with HTTP Basic Auth
/// instead: credentials come from config (<c>Hangfire:DashboardUser</c> /
/// <c>Hangfire:DashboardPassword</c>); if either is missing, the dashboard is
/// closed in non-Development environments to avoid an accidental open admin
/// surface. Localhost requests in Development are allowed through.
///
/// In production, set the credentials via secrets (env vars), and never reuse
/// the tenant admin's password — this is a dedicated operator credential.
/// </summary>
public sealed class HangfireDashboardAuth(IConfiguration cfg, IWebHostEnvironment env) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();

        // Dev convenience: in Development the dashboard is reachable without basic-auth so
        // the operator UI is usable during local debugging. Inside a Docker bridge the
        // host's request doesn't appear as loopback, so an IP check would lock you out
        // of the dashboard you just deployed. Production never takes this path.
        if (env.IsDevelopment()) return true;

        var configuredUser = cfg["Hangfire:DashboardUser"];
        var configuredPwd  = cfg["Hangfire:DashboardPassword"];
        // No credentials configured outside Development → dashboard is closed. Fail safe.
        if (string.IsNullOrWhiteSpace(configuredUser) || string.IsNullOrWhiteSpace(configuredPwd))
            return false;

        var header = http.Request.Headers["Authorization"].ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            // Prompt the browser for credentials by emitting the 401 + WWW-Authenticate.
            http.Response.Headers["WWW-Authenticate"] = "Basic realm=\"BidBuilder Hangfire\"";
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = raw.IndexOf(':');
            if (colon <= 0) return false;
            var user = raw[..colon];
            var pwd  = raw[(colon + 1)..];
            // Constant-time compare for the password to avoid a tiny timing-based oracle.
            return string.Equals(user, configuredUser, StringComparison.Ordinal)
                && CryptographicEquals(pwd, configuredPwd!);
        }
        catch
        {
            return false;
        }
    }

    private static bool CryptographicEquals(string a, string b)
    {
        var aa = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aa, bb);
    }
}

internal static class IpAddressExtensions
{
    public static bool IsLoopback(this System.Net.IPAddress ip) => System.Net.IPAddress.IsLoopback(ip);
}
