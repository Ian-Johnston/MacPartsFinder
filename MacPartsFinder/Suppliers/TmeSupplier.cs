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
/// TME (Transfer Multisort Elektronik) Product API v2 - https://developers.tme.eu
/// Login: OAuth2 client credentials (Token + Application secret) gives a 5-minute access token.
/// A search is /products/search (parts, photos, total) then /products/data (prices + stock) for that page.
/// Getting the Token needs the temporary token from the tme.eu shop account - see Help.
/// </summary>
public class TmeSupplier : ApiSupplier
{
    private const string BaseUrl = "https://api.tme.eu";
    private const int MaxPerPage = 50; // /products/data takes up to 50 symbols

    private readonly string _token;
    private readonly string _secret;
    private readonly string _country;
    private readonly string _currency;

    private string? _accessToken;
    private DateTime _accessExpiresUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public TmeSupplier(string? token, string? secret, string? country, string? currency, string searchUrlTemplate)
        : base(AppSettings.TME, searchUrlTemplate)
    {
        _token = (token ?? "").Trim();
        _secret = (secret ?? "").Trim();
        _country = string.IsNullOrWhiteSpace(country) ? "GB" : country.Trim().ToUpperInvariant();
        _currency = string.IsNullOrWhiteSpace(currency) ? "GBP" : currency.Trim().ToUpperInvariant();
    }

    public override bool IsConfigured => _token.Length > 0 && _secret.Length > 0;

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _accessExpiresUtc) return _accessToken;

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/auth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "grant_type", "client_credentials" } })
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(_token + ":" + _secret));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            var root = await SendForJsonAsync(req, ct).ConfigureAwait(false);
            _accessToken = root.GetStr("access_token");
            if (_accessToken.Length == 0) throw new SupplierException("TME did not return an access token.");
            var expires = root.GetNum("expires_in").GetValueOrDefault(300m);
            _accessExpiresUtc = DateTime.UtcNow.AddSeconds((double)expires - 30);
            return _accessToken;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>GET with the Bearer token; retries once with a fresh token if TME says it expired.</summary>
    private async Task<JToken> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var access = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            req.Headers.AcceptLanguage.ParseAdd("en");
            try
            {
                var root = await SendForJsonAsync(req, ct).ConfigureAwait(false);
                var status = root.GetStr("status");
                if (status.Length > 0 && !status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SupplierException($"TME: {status} {Truncate(root.ToString(Formatting.None), 250)}");
                }
                return root;
            }
            catch (SupplierException ex) when (attempt == 1 && ex.Message.StartsWith("HTTP 401"))
            {
                _accessToken = null; // expired early - log in again and retry once
            }
        }
        throw new SupplierException("TME login failed.");
    }

    private static string Q(string name, string value)
    {
        return Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
    }

    public override async Task<SearchPage> SearchAsync(string query, SearchMode mode, int maxResults, int offset,
                                                       SupplierFilters? filters, CancellationToken ct)
    {
        var limit = Math.Max(1, Math.Min(maxResults, MaxPerPage));
        var page = offset / limit + 1; // TME pages are numbered from 1

        // 1) Search: products (symbol, manufacturer, description, photos...) and the total count.
        var phrase = query.Length > 40 ? query.Substring(0, 40) : query; // TME allows 2-40 characters
        var searchParams = new List<string>
        {
            Q("phrase", phrase), Q("scope[]", "products"), Q("scope[]", "counters"),
            Q("country", _country), Q("limit", limit.ToString()), Q("page", page.ToString())
        };
        // TME filters stock on its side. (Its search has no RoHS option, so that one is ignored for TME.)
        if (filters != null && filters.InStockOnly) searchParams.Add(Q("filter[in_stock]", "true"));
        var searchQuery = string.Join("&", searchParams);
        var found = await GetJsonAsync("/products/search?" + searchQuery, ct).ConfigureAwait(false);

        var data = found.GetPath("data");
        var products = data.GetArr("products", "elements");
        var total = ToNullableInt(data.GetNum("counters", "count"));

        var results = new List<PartResult>();
        var bySymbol = new Dictionary<string, PartResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products)
        {
            var symbol = p.GetStr("symbol");
            if (symbol.Length == 0) continue;
            var mpn = FirstText(p.GetPath("manufacturer_symbols"));
            if (mode == SearchMode.PartNumber && mpn.Length > 0 &&
                !mpn.StartsWith(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (mode == SearchMode.ExactPartNumber && !SamePartNumber(mpn, query)) continue;
            if (mode == SearchMode.SupplierCode && !SamePartNumber(symbol, query)) continue;

            var r = new PartResult
            {
                Supplier = Name,
                SupplierPartNumber = symbol,
                ManufacturerPartNumber = mpn.Length > 0 ? mpn : symbol,
                Manufacturer = p.GetStr("manufacturer", "name"),
                Description = p.GetStr("description"),
                Category = CategoryOf(p),
                Currency = _currency,
                MinOrderQty = ToNullableInt(p.GetNum("minimal_amount")),
                Status = FirstText(p.GetPath("product_status")),
                ProductUrl = GetSearchUrl(symbol),
                ImageUrl = FixUrl(p.GetStr("assets", "primary_photo", "prime")),
                LargeImageUrl = FixUrl(p.GetStr("assets", "primary_photo", "high_resolution"))
            };
            if (r.ImageUrl.Length == 0) r.ImageUrl = FixUrl(p.GetStr("assets", "primary_photo", "thumbnail"));
            results.Add(r);
            bySymbol[symbol] = r;
        }

        // 2) Prices and stock for this page's parts, in one call.
        if (bySymbol.Count > 0)
        {
            var dataQuery = string.Join("&",
                new[] { Q("scope[]", "prices"), Q("scope[]", "stock"), Q("country", _country), Q("currency", _currency) }
                .Concat(bySymbol.Keys.Select(sym => Q("symbols[]", sym))));
            var pricing = await GetJsonAsync("/products/data?" + dataQuery, ct).ConfigureAwait(false);
            ApplyPricesAndStock(pricing, bySymbol);
        }

        if (mode == SearchMode.ExactPartNumber || mode == SearchMode.SupplierCode) total = results.Count;
        if (filters != null && filters.InStockOnly)
        {
            results = results.Where(r => r.Stock.GetValueOrDefault() > 0).ToList(); // belt and braces
        }
        return new SearchPage(results, total);
    }

    /// <summary>Fills in stock and price breaks from /products/data.</summary>
    private static void ApplyPricesAndStock(JToken pricing, Dictionary<string, PartResult> bySymbol)
    {
        var elements = pricing.GetArr("data", "elements");
        if (elements.Count == 0) elements = pricing.GetArr("data", "products", "elements");
        foreach (var el in elements)
        {
            if (!bySymbol.TryGetValue(el.GetStr("symbol"), out var r)) continue;

            var stock = el.GetNum("stock_quantity");
            if (!stock.HasValue) stock = el.GetNum("stock", "quantity");
            if (!stock.HasValue) stock = el.GetNum("stock", "stock_quantity");
            r.Stock = ToNullableLong(stock);

            var prices = el.GetPath("prices");
            var cur = prices.GetStr("currency");
            if (cur.Length > 0) r.Currency = cur;
            var breaks = prices.GetArr("elements");
            if (breaks.Count == 0) breaks = prices.GetArr("price_list");
            foreach (var pb in breaks)
            {
                var qty = ToNullableInt(pb.GetNum("amount"));
                var price = pb.GetNum("price");
                if (!price.HasValue) price = pb.GetNum("price_value");
                if (qty.HasValue && price.HasValue) r.PriceBreaks.Add(new PriceBreak(qty.Value, price.Value));
            }
            if (r.PriceBreaks.Count > 0) r.UnitPrice = r.PriceBreaks.OrderBy(b => b.Quantity).First().Price;
            var priceType = prices.GetStr("type");
            if (priceType.Length > 0) r.StockInfo = $"Prices {priceType.ToLowerInvariant()}"; // e.g. "net" (ex VAT)
        }
    }

    /// <summary>
    /// TME's category for a product (a name, or an object with a name). If it isn't there, the start of the
    /// description, which TME writes as "IC: operational amplifier; 1MHz; …".
    /// </summary>
    private static string CategoryOf(JToken p)
    {
        var c = p.GetStr("category", "name");
        if (c.Length == 0) c = p.GetStr("category");
        if (c.Length == 0) c = p.GetStr("category_name");
        if (c.Length == 0) c = p.GetStr("description").Split(';')[0].Trim();
        return c;
    }

    /// <summary>First text in a value that may be a string, an array of strings, or an array of objects.</summary>
    private static string FirstText(JToken? t)
    {
        if (t == null) return "";
        switch (t.Type)
        {
            case JTokenType.String:
                return ((string?)((JValue)t).Value ?? "").Trim();
            case JTokenType.Array:
                foreach (var item in (JArray)t)
                {
                    var s = item.Type == JTokenType.String ? (string?)((JValue)item).Value
                          : item.GetStr("symbol").Length > 0 ? item.GetStr("symbol")
                          : item.GetStr("name").Length > 0 ? item.GetStr("name") : item.GetStr("value");
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
                break;
        }
        return "";
    }

    private static string FixUrl(string url)
    {
        if (url.StartsWith("//")) return "https:" + url;
        return url;
    }
}
