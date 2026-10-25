using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MacPartsFinder.WebHelper;

namespace MacPartsFinder;

/// <summary>
/// Exchange rates for showing every price in one currency: the European Central Bank's daily reference rates,
/// via the free Frankfurter service (no key needed). Rates are "per euro" and saved in settings.json, so they're
/// only fetched again after 12 hours - and the last good rates still work if the Mac is offline.
/// The ECB publishes once each working day (about 16:00 CET); it has no Taiwan dollar (TWD) rate.
/// </summary>
public class ExchangeRates
{
    public const string SourceName = "ECB";
    private static readonly string[] Urls = { "https://api.frankfurter.dev/v1/latest", "https://api.frankfurter.app/latest" };

    /// <summary>How long fetched rates are used before asking for new ones.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(12);

    /// <summary>Currencies offered in Settings - all have ECB rates.</summary>
    public static readonly string[] Currencies =
    {
        "GBP", "EUR", "USD", "CAD", "AUD", "NZD", "CHF", "SEK", "NOK", "DKK", "PLN", "CZK", "HUF", "RON",
        "ILS", "ZAR", "JPY", "KRW", "CNY", "HKD", "SGD", "MYR", "THB", "PHP", "INR"
    };

    /// <summary>The ECB date the rates are for, e.g. "2026-10-23".</summary>
    public string RatesDate { get; set; } = "";
    /// <summary>When the app last fetched them (UTC).</summary>
    public DateTime? FetchedUtc { get; set; }
    /// <summary>Units of each currency per 1 euro (EUR itself = 1).</summary>
    public Dictionary<string, decimal> PerEuro { get; set; } = new();

    [JsonIgnore] public bool HasRates => PerEuro != null && PerEuro.Count > 0;

    /// <summary>True when there are no rates yet, or they were fetched 12 hours ago or more.</summary>
    [JsonIgnore] public bool NeedsRefresh => !HasRates || !FetchedUtc.HasValue || DateTime.UtcNow - FetchedUtc.Value >= RefreshEvery;

    /// <summary>How many days old the ECB rates are (a weekend makes them 2-3 days old, which is normal).</summary>
    [JsonIgnore]
    public int AgeDays =>
        DateTime.TryParseExact(RatesDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? Math.Max(0, (DateTime.Today - d).Days)
            : 0;

    /// <summary>e.g. "ECB rates of 23 Oct 2026".</summary>
    public string Describe()
    {
        if (DateTime.TryParseExact(RatesDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return $"{SourceName} rates of {d:d MMM yyyy}";
        return $"{SourceName} rates";
    }

    /// <summary>How many <paramref name="toCur"/> one <paramref name="fromCur"/> buys, or null if either rate is missing.</summary>
    public decimal? Rate(string? fromCur, string? toCur)
    {
        if (string.IsNullOrEmpty(fromCur) || string.IsNullOrEmpty(toCur)) return null;
        if (string.Equals(fromCur, toCur, StringComparison.OrdinalIgnoreCase)) return 1m;
        if (!PerEuro.TryGetValue(fromCur.ToUpperInvariant(), out var f) || f <= 0) return null;
        if (!PerEuro.TryGetValue(toCur.ToUpperInvariant(), out var t)) return null;
        return t / f;
    }

    /// <summary>Fetches today's rates. Tries a second address if the first fails; throws if both do.</summary>
    public static async Task<ExchangeRates> DownloadAsync(CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var url in Urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var j = await SendForJsonAsync(req, ct).ConfigureAwait(false);
                var baseCur = j.GetStr("base");
                var r = new ExchangeRates { RatesDate = j.GetStr("date"), FetchedUtc = DateTime.UtcNow };
                if (j.GetPath("rates") is not JObject rates || !string.Equals(baseCur, "EUR", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SupplierException("Unexpected exchange-rate reply.");
                }
                foreach (var prop in rates.Properties())
                {
                    var v = rates.GetNum(prop.Name);
                    if (v.HasValue && v.Value > 0) r.PerEuro[prop.Name.ToUpperInvariant()] = v.Value;
                }
                r.PerEuro["EUR"] = 1m;
                return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new SupplierException("Couldn't get exchange rates: " + lastError?.Message);
    }

    /// <summary>The currency for this Mac's region, if it's one we have rates for; else GBP.</summary>
    public static string DefaultCurrency()
    {
        try
        {
            var cur = new RegionInfo(CultureInfo.CurrentCulture.Name).ISOCurrencySymbol;
            if (Currencies.Contains(cur)) return cur;
        }
        catch
        {
            // e.g. a neutral culture with no region
        }
        return "GBP";
    }
}
