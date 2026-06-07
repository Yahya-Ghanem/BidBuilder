using System.Net;
using System.Net.Sockets;

namespace BidBuilder.Api.Auth;

/// <summary>
/// 23.2 — Pure CIDR-membership utility used by <see cref="ApiKeyGuard"/> to gate API-key
/// requests by source IP. Accepts IPv4 and IPv6 ranges; a bare address is treated as a
/// host route (<c>/32</c> for v4, <c>/128</c> for v6). Matching is byte-wise after
/// normalizing the candidate IP to the family of the CIDR (an IPv4-mapped IPv6 input
/// is reduced to its embedded IPv4, so "1.2.3.4" matches "0:0:0:0:0:ffff:0102:0304/128").
///
/// Designed so the policy can be evaluated from a claim CSV without re-reading the DB.
/// </summary>
public static class CidrMatcher
{
    /// <summary>Empty/null allowlist → "any IP", returns true. Otherwise true iff
    /// <paramref name="candidate"/> is in any of the comma-separated <paramref name="cidrCsv"/>
    /// ranges. Malformed entries are skipped (NOT treated as "match all").</summary>
    public static bool IsAllowed(IPAddress? candidate, string? cidrCsv)
    {
        if (string.IsNullOrWhiteSpace(cidrCsv)) return true;   // unrestricted
        if (candidate is null) return false;
        foreach (var raw in cidrCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Match(candidate, raw)) return true;
        return false;
    }

    /// <summary>Validate a single CIDR (or bare host) and return its canonical form.
    /// Falsy malformed input → returns false; the caller surfaces the error.</summary>
    public static bool TryParse(string entry, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(entry)) return false;
        if (!SplitCidr(entry.Trim(), out var address, out var prefix)) return false;
        if (!IPAddress.TryParse(address, out var ip)) return false;
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > max) return false;
        canonical = $"{Normalize(ip)}/{prefix}";
        return true;
    }

    // ── internals ────────────────────────────────────────────────────────────────

    private static bool Match(IPAddress candidate, string raw)
    {
        if (!SplitCidr(raw, out var address, out var prefix)) return false;
        if (!IPAddress.TryParse(address, out var network)) return false;

        // Normalize a v4-mapped v6 candidate to v4 (then we compare against a v4 CIDR
        // correctly; otherwise mismatched families never match — which is also fine).
        var c = Normalize(candidate);
        var n = Normalize(network);
        if (c.AddressFamily != n.AddressFamily) return false;

        var max = n.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > max) return false;

        var cb = c.GetAddressBytes();
        var nb = n.GetAddressBytes();
        var bits = prefix;
        for (var i = 0; i < cb.Length; i++)
        {
            if (bits >= 8)
            {
                if (cb[i] != nb[i]) return false;
                bits -= 8;
            }
            else if (bits > 0)
            {
                var mask = (byte)(0xFF << (8 - bits));
                if ((cb[i] & mask) != (nb[i] & mask)) return false;
                bits = 0;
            }
            else break;
        }
        return true;
    }

    /// <summary>Split "a.b.c.d/n" into ("a.b.c.d", n). A bare address with no '/' is
    /// treated as a host route (/32 or /128) — the caller resolves the family.</summary>
    private static bool SplitCidr(string raw, out string address, out int prefix)
    {
        var slash = raw.IndexOf('/');
        if (slash < 0)
        {
            address = raw;
            prefix  = IPAddress.TryParse(raw, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            return true;
        }
        address = raw[..slash];
        return int.TryParse(raw[(slash + 1)..], out prefix);
    }

    /// <summary>Reduce an IPv4-mapped IPv6 address (::ffff:a.b.c.d) to its IPv4 form so
    /// comparisons against an IPv4 CIDR work even when the platform stamps the remote IP
    /// as the mapped form (Kestrel does this on dual-stack sockets).</summary>
    private static IPAddress Normalize(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
