namespace BidBuilder.Api.Services;

/// <summary>
/// Converts a money amount to its English words form for the bid letter and other
/// documents, e.g. 7065941.66 AED →
/// "Seven Million Sixty-Five Thousand Nine Hundred Forty-One AED and Sixty-Six Fils only".
/// The fractional unit is Fils for AED, otherwise Cents.
///
/// Extracted from ExportService as a pure, self-contained helper so the number-to-words
/// logic can be unit-tested in isolation (no Excel/PDF/DB dependencies).
/// </summary>
public static class MoneyInWords
{
    private static readonly string[] Ones =
        { "Zero","One","Two","Three","Four","Five","Six","Seven","Eight","Nine","Ten","Eleven","Twelve",
          "Thirteen","Fourteen","Fifteen","Sixteen","Seventeen","Eighteen","Nineteen" };
    private static readonly string[] TensWords =
        { "","","Twenty","Thirty","Forty","Fifty","Sixty","Seventy","Eighty","Ninety" };
    private static readonly string[] Scales = { "", " Thousand", " Million", " Billion", " Trillion" };

    /// <summary>Words for a value 0–999 (e.g. 142 → "One Hundred Forty-Two").</summary>
    public static string ThreeDigit(int n)
    {
        var s = "";
        if (n >= 100) { s += Ones[n / 100] + " Hundred"; n %= 100; if (n > 0) s += " "; }
        if (n >= 20) { s += TensWords[n / 10]; if (n % 10 > 0) s += "-" + Ones[n % 10]; }
        else if (n > 0) s += Ones[n];
        return s;
    }

    /// <summary>Words for any whole number (grouped in thousands).</summary>
    public static string Integer(long n)
    {
        if (n == 0) return "Zero";
        if (n < 0) return "Minus " + Integer(-n);
        var groups = new List<int>();
        while (n > 0) { groups.Add((int)(n % 1000)); n /= 1000; }
        var parts = new List<string>();
        for (int i = groups.Count - 1; i >= 0; i--)
            if (groups[i] > 0) parts.Add(ThreeDigit(groups[i]) + Scales[i]);
        return string.Join(" ", parts);
    }

    /// <summary>"7065941.66 AED" → "Seven Million … Forty-One AED and Sixty-Six Fils only".</summary>
    public static string Money(decimal amount, string currency)
    {
        var whole = (long)Math.Floor(amount);
        var frac = (int)Math.Round((amount - whole) * 100m, MidpointRounding.AwayFromZero);
        if (frac == 100) { whole++; frac = 0; }
        var cur = (currency ?? "").Trim().ToUpperInvariant();
        var sub = cur == "AED" ? "Fils" : "Cents";
        var s = $"{Integer(whole)} {cur}";
        if (frac > 0) s += $" and {Integer(frac)} {sub}";
        return s + " only";
    }
}
