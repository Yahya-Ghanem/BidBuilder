using System.Security.Cryptography;
using System.Text;

namespace BidBuilder.Api.Auth;

/// <summary>
/// 20.8 — RFC 6238 time-based one-time passwords (TOTP) + RFC 4226 HOTP, plus
/// recovery-code helpers. Authenticator-app compatible (HMAC-SHA1, 30-second
/// step, 6 digits) so Google Authenticator / 1Password / Authy all work. No
/// external dependency — the primitives are in the BCL — which keeps the auth
/// surface free of third-party supply-chain risk and makes every step unit-testable.
/// </summary>
public static class TotpService
{
    public const int    Digits = 6;
    public const int    PeriodSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A fresh 160-bit shared secret (the authenticator-app standard width).</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>The 6-digit code for a given secret at a given Unix time (seconds).</summary>
    public static string ComputeCode(byte[] secret, long unixSeconds)
    {
        var counter = unixSeconds / PeriodSeconds;
        return ComputeHotp(secret, counter);
    }

    /// <summary>
    /// True when <paramref name="code"/> matches the secret within ±<paramref name="window"/>
    /// steps of <paramref name="unixSeconds"/> (one step of leeway absorbs clock skew and the
    /// user typing across a boundary). Comparison is length- and value-safe.
    /// </summary>
    public static bool Verify(byte[] secret, string? code, long unixSeconds, int window = 1)
    {
        if (secret is null || secret.Length == 0) return false;
        var trimmed = code?.Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length != Digits || !trimmed.All(char.IsAsciiDigit))
            return false;

        var counter = unixSeconds / PeriodSeconds;
        for (var i = -window; i <= window; i++)
        {
            var candidate = ComputeHotp(secret, counter + i);
            if (FixedTimeEquals(candidate, trimmed)) return true;
        }
        return false;
    }

    private static string ComputeHotp(byte[] secret, long counter)
    {
        Span<byte> ctr = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(ctr, counter);
        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(ctr.ToArray());
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset]     & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8)  |
             (hash[offset + 3] & 0xFF);
        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString().PadLeft(Digits, '0');
    }

    // ── Base32 (RFC 4648, no padding) — the encoding authenticator apps expect ──
    public static string Base32Encode(byte[] data)
    {
        if (data.Length == 0) return "";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = data[0], next = 1, bitsLeft = 8;
        while (bitsLeft > 0 || next < data.Length)
        {
            if (bitsLeft < 5)
            {
                if (next < data.Length) { buffer = (buffer << 8) | (data[next++] & 0xFF); bitsLeft += 8; }
                else { buffer <<= 5 - bitsLeft; bitsLeft = 5; }
            }
            var index = (buffer >> (bitsLeft - 5)) & 0x1F;
            bitsLeft -= 5;
            sb.Append(Base32Alphabet[index]);
        }
        return sb.ToString();
    }

    public static byte[] Base32Decode(string input)
    {
        var cleaned = input.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        if (cleaned.Length == 0) return [];
        var bytes = new List<byte>(cleaned.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in cleaned)
        {
            var val = Base32Alphabet.IndexOf(c);
            if (val < 0) throw new FormatException($"Invalid base32 character '{c}'.");
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return [.. bytes];
    }

    /// <summary>The otpauth:// provisioning URI an authenticator app scans as a QR code.</summary>
    public static string OtpAuthUri(string issuer, string account, byte[] secret)
    {
        var iss = Uri.EscapeDataString(issuer);
        var acc = Uri.EscapeDataString(account);
        var b32 = Base32Encode(secret);
        return $"otpauth://totp/{iss}:{acc}?secret={b32}&issuer={iss}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    // ── Recovery codes ──────────────────────────────────────────────────────────
    /// <summary>Generate <paramref name="count"/> human-friendly single-use recovery
    /// codes (format "abcde-fghij"). Return the PLAINTEXT — the caller stores only
    /// bcrypt hashes and shows these to the user exactly once.</summary>
    public static List<string> NewRecoveryCodes(int count = 10)
    {
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var raw = Base32Encode(RandomNumberGenerator.GetBytes(7)).ToLowerInvariant()[..10];
            codes.Add($"{raw[..5]}-{raw[5..]}");
        }
        return codes;
    }

    /// <summary>Normalise a recovery code for hashing/comparison (strip spaces/hyphens, lowercase).</summary>
    public static string NormalizeRecoveryCode(string code) =>
        new string(code.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
