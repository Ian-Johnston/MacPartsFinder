using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MacPartsFinder.ParseHelpers;
using static MacPartsFinder.WebHelper;

namespace MacPartsFinder;

/// <summary>
/// DigiKey Product Information API v4 (OAuth2 client-credentials / "2-legged").
/// Create an app at https://developer.digikey.com/ and subscribe it to "ProductInformation V4".
/// </summary>
public class DigiKeySupplier : ApiSupplier
{
    /// <summary>DigiKey country sites for Settings - every one tested live (Oct 2026). Any other code can still be typed in.
    /// Taiwan is left out: there's no ECB exchange rate for its dollar, so Prices in GBP couldn't convert it.</summary>
    public static readonly string[] Sites =
    {
        "UK", "IE", "US", "CA", "AU", "NZ", "DE", "FR", "IT", "ES", "NL", "BE", "AT", "LU", "PT", "FI", "GR",
        "EE", "LV", "LT", "SK", "SI", "BG", "CH", "SE", "NO", "DK", "PL", "CZ", "HU", "RO", "IL", "ZA",
        "JP", "KR", "CN", "HK", "SG", "MY", "TH", "PH", "IN", "MX"
    };

    /// <summary>Currencies DigiKey prices in - every one tested live.</summary>
    public static readonly string[] Currencies =
    {
        "GBP", "EUR", "USD", "CAD", "AUD", "NZD", "CHF", "SEK", "NOK", "DKK", "PLN", "CZK", "HUF", "RON",
        "ILS", "ZAR", "JPY", "KRW", "CNY", "HKD", "SGD", "MYR", "THB", "PHP", "INR"
    };

    private const string TokenUrl = "https://api.digikey.com/v1/oauth2/token";
    private const string SearchUrl = "https://api.digikey.com/products/v4/search/keyword";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _site;
    private readonly string _language;
    private readonly string _currency;

    private string? _token;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public DigiKeySupplier(string? clientId, string? clientSecret, string? site, string? language,
                           string? currency, string searchUrlTemplate) : base(AppSettings.DigiKey, searchUrlTemplate)
    {
        _clientId = (clientId ?? "").Trim();
        _clientSecret = (clientSecret ?? "").Trim();
        _site = string.IsNullOrWhiteSpace(site) ? "UK" : site.Trim();
        _language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        _currency = string.IsNullOrWhiteSpace(currency) ? "GBP" : currency.Trim();
    }

    public override bool IsConfigured => _clientId.Length > 0 && _clientSecret.Length > 0;

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token != null && DateTime.UtcNow < _tokenExpiresUtc) return _token;

            var form = new Dictionary<string, string>
            {
                { "client_id", _clientId },
                { "client_secret", _clientSecret },
                { "grant_type", "client_credentials" }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(form)
            };
            var root = await SendForJsonAsync(req, ct).ConfigureAwait(false);
            _token = root.GetStr("access_token");
            if (_token.Length == 0) throw new SupplierException("DigiKey did not return an access token.");
            var expires = root.GetNum("expires_in").GetValueOrDefault(600m);
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds((double)expires - 60);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public override async Task<SearchPage> SearchAsync(string query, SearchMode mode, int maxResults, int offset,
                                                       SupplierFilters? filters, CancellationToken ct)
    {
        filters ??= SupplierFilters.None;
        var token = await GetTokenAsync(ct).ConfigureAwait(false);

        // DigiKey filters on its side via FilterOptionsRequest.SearchOptions.
        var searchOptions = new List<string>();
        if (filters.InStockOnly) searchOptions.Add("InStock");
        if (filters.RohsOnly) searchOptions.Add("RohsCompliant");
        object payload;
        if (searchOptions.Count > 0)
        {
            payload = new { Keywords = query, Limit = Math.Min(maxResults, 50), Offset = offset,
                            FilterOptionsRequest = new { SearchOptions = searchOptions } };
        }
        else
        {
            payload = new { Keywords = query, Limit = Math.Min(maxResults, 50), Offset = offset };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, SearchUrl)
        {
            Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-DIGIKEY-Client-Id", _clientId);
        req.Headers.Add("X-DIGIKEY-Locale-Site", _site);
        req.Headers.Add("X-DIGIKEY-Locale-Language", _language);
        req.Headers.Add("X-DIGIKEY-Locale-Currency", _currency);

        try
        {
            var root = await SendForJsonAsync(req, ct).ConfigureAwait(false);
            var parts = Parse(root, mode, query, includeExact: offset == 0);
            var total = ToNullableInt(root.GetNum("ProductsCount"));
            if (mode == SearchMode.ExactPartNumber || mode == SearchMode.SupplierCode) total = parts.Count;
            if (filters.InStockOnly) parts = parts.Where(p => p.Stock.GetValueOrDefault() > 0).ToList(); // belt and braces
            return new SearchPage(parts, total);
        }
        catch (SupplierException ex) when (ex.Message.StartsWith("HTTP 401"))
        {
            _token = null; // force a fresh token next time
            throw;
        }
    }

    private List<PartResult> Parse(JToken root, SearchMode mode, string query, bool includeExact)
    {
        var results = new List<PartResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Exact matches first (they come back on every page, so only use them on the first), then the page of results.
        var exact = includeExact ? root.GetArr("ExactMatches") : new List<JToken>();
        foreach (var p in exact.Concat(root.GetArr("Products")))
        {
            var mpn = p.GetStr("ManufacturerProductNumber");
            if (mode == SearchMode.PartNumber &&
                !mpn.StartsWith(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (mode == SearchMode.ExactPartNumber && !SamePartNumber(mpn, query)) continue;

            // A product has several packagings (reel, cut tape, Digi-Reel...).
            // Pick the one with the smallest minimum order - the useful one for repairs -
            // or, when looking up a DigiKey order code, the packaging with that exact code.
            var variations = p.GetArr("ProductVariations");
            JToken? variation;
            if (mode == SearchMode.SupplierCode)
            {
                variation = variations.FirstOrDefault(v => SamePartNumber(v.GetStr("DigiKeyProductNumber"), query));
                if (variation == null) continue;
            }
            else
            {
                variation = variations
                    .OrderBy(v => v.GetNum("MinimumOrderQuantity").GetValueOrDefault(decimal.MaxValue))
                    .FirstOrDefault();
            }

            var dkPn = variation.GetStr("DigiKeyProductNumber");
            if (!seen.Add(mpn + "|" + dkPn)) continue;

            var r = new PartResult
            {
                Supplier = Name,
                SupplierPartNumber = dkPn,
                ManufacturerPartNumber = mpn,
                Manufacturer = p.GetStr("Manufacturer", "Name"),
                Description = p.GetStr("Description", "ProductDescription"),
                Category = DeepestCategory(p.GetPath("Category")),
                Package = PackageOf(p),
                Stock = ToNullableLong(p.GetNum("QuantityAvailable")),
                Currency = _currency,
                MinOrderQty = ToNullableInt(variation.GetNum("MinimumOrderQuantity")),
                Status = p.GetStr("ProductStatus", "Status"),
                ProductUrl = p.GetStr("ProductUrl"),
                DatasheetUrl = p.GetStr("DatasheetUrl"),
                ImageUrl = p.GetStr("PhotoUrl")
            };
            var pkg = variation.GetStr("PackageType", "Name");
            var lead = p.GetStr("ManufacturerLeadWeeks");
            r.StockInfo = string.Join(" | ", new[] { pkg, lead.Length > 0 ? $"Lead {lead} wks" : "" }.Where(x => x.Length > 0));

            foreach (var pb in variation.GetArr("StandardPricing"))
            {
                var qty = ToNullableInt(pb.GetNum("BreakQuantity"));
                var price = pb.GetNum("UnitPrice");
                if (qty.HasValue && price.HasValue) r.PriceBreaks.Add(new PriceBreak(qty.Value, price.Value));
            }
            r.UnitPrice = r.PriceBreaks.Count > 0
                ? r.PriceBreaks.OrderBy(b => b.Quantity).First().Price
                : p.GetNum("UnitPrice");
            // (No cap here: dropping any of this page's products would lose them for good,
            // because Load more carries on from the next offset.)
            results.Add(r);
        }
        return results;
    }

    /// <summary>"8-SOIC" from the part's parameters ("Supplier Device Package", else "Package / Case").</summary>
    private static string PackageOf(JToken p)
    {
        var parameters = p.GetArr("Parameters");
        foreach (var paramName in new[] { "Supplier Device Package", "Package / Case" })
        {
            var hit = parameters.FirstOrDefault(x => string.Equals(x.GetStr("ParameterText"), paramName, StringComparison.OrdinalIgnoreCase));
            var v = hit.GetStr("ValueText");
            if (v.Length > 0 && v != "-") return v;
        }
        return "";
    }

    /// <summary>
    /// DigiKey categories nest, e.g. Integrated Circuits (ICs) > Data Acquisition > Analog to Digital Converters (ADC).
    /// The last level is the most useful as a part type.
    /// </summary>
    private static string DeepestCategory(JToken? c)
    {
        var name = "";
        while (c != null)
        {
            var n = c.GetStr("Name");
            if (n.Length > 0) name = n;
            var children = c.GetArr("ChildCategories");
            c = children.Count > 0 ? children[0] : null;
        }
        return name;
    }
}
