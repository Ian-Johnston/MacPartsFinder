using System;
using System.Globalization;
using System.Text;

namespace MacPartsFinder;

public static class ParseHelpers
{
    /// <summary>Parses prices like "£1.23", "1,234.50", "0,52 €". Returns null if no number found.</summary>
    public static decimal? ParseDecimalLoose(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsDigit(c) || c == '.' || c == ',') sb.Append(c);
        }
        var t = sb.ToString().Trim('.', ',');
        if (t.Length == 0) return null;

        if (t.Contains('.') && t.Contains(','))
        {
            // Whichever separator comes last is the decimal point.
            if (t.LastIndexOf(',') > t.LastIndexOf('.'))
                t = t.Replace(".", "").Replace(",", ".");
            else
                t = t.Replace(",", "");
        }
        else if (t.Contains(','))
        {
            var idx = t.LastIndexOf(',');
            var looksLikeThousands = (t.Length - idx - 1 == 3) && !t.StartsWith("0,");
            t = looksLikeThousands ? t.Replace(",", "") : t.Replace(",", ".");
        }

        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    /// <summary>Pulls the first integer out of text like "1,234 In Stock". Returns null if none.</summary>
    public static long? ParseLongLoose(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsDigit(c))
                sb.Append(c);
            else if (c == ',' && sb.Length > 0)
                continue;
            else if (sb.Length > 0)
                break;
        }
        if (sb.Length > 0 && long.TryParse(sb.ToString(), out var n)) return n;
        return null;
    }

    public static string Truncate(string? s, int maxLen)
    {
        if (s == null) return "";
        return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "…";
    }

    // Math.Round matches VB's CInt/CLng (round half to even) in the Windows version.
    public static int? ToNullableInt(decimal? d)
    {
        if (!d.HasValue) return null;
        return (int)Math.Round(Math.Min(int.MaxValue, Math.Max(0m, d.Value)));
    }

    public static long? ToNullableLong(decimal? d)
    {
        if (!d.HasValue) return null;
        return (long)Math.Round(Math.Max(0m, d.Value));
    }
}
