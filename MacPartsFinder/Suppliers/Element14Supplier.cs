using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MacPartsFinder.ParseHelpers;
using static MacPartsFinder.WebHelper;

namespace MacPartsFinder;

/// <summary>
/// element14 Product Search API - covers Farnell (uk.farnell.com) and Newark (www.newark.com)
/// with the same key. Register at https://partner.element14.com/
/// </summary>
public class Element14Supplier : ApiSupplier
{
    private const string BaseUrl = "https://api.element14.com/catalog/products";

    // Farnell and Newark share one key, and the Basic tier allows 2 calls/second.
    // These are static so both instances queue through the same gate.
    private static readonly SemaphoreSlim _rateGate = new(1, 1);
    private static DateTime _lastCallUtc = DateTime.MinValue;
    private static readonly TimeSpan MinCallSpacing = TimeSpan.FromMilliseconds(600);

    /// <summary>One element14 country store: same API key, its own stock and prices.</summary>
    public sealed class Store
    {
        public Store(string id, string country, string currency, string imageLocale = "en_GB")
        {
            Id = id;
            Country = country;
            Currency = currency;
            ImageLocale = imageLocale;
        }

        public string Id { get; }          // e.g. "nz.element14.com"
        public string Country { get; }
        public string Currency { get; }    // the API doesn't say, so it's listed here (checked against live prices)
        public string ImageLocale { get; } // photo folder: en_GB on most stores, en_US on AU/NZ

        public override string ToString() => $"{Country} - {Id} ({Currency})";
    }

    /// <summary>The stores the Farnell supplier can use - each one tested live with a search and a photo (Oct 2026).</summary>
    public static readonly List<Store> FarnellStores = new()
    {
        new Store("uk.farnell.com", "United Kingdom", "GBP"),
        new Store("ie.farnell.com", "Ireland", "EUR"),
        new Store("de.farnell.com", "Germany", "EUR"),
        new Store("fr.farnell.com", "France", "EUR"),
        new Store("it.farnell.com", "Italy", "EUR"),
        new Store("es.farnell.com", "Spain", "EUR"),
        new Store("nl.farnell.com", "Netherlands", "EUR"),
        new Store("be.farnell.com", "Belgium", "EUR"),
        new Store("at.farnell.com", "Austria", "EUR"),
        new Store("pt.farnell.com", "Portugal", "EUR"),
        new Store("fi.farnell.com", "Finland", "EUR"),
        new Store("sk.farnell.com", "Slovakia", "EUR"),
        new Store("si.farnell.com", "Slovenia", "EUR"),
        new Store("ee.farnell.com", "Estonia", "EUR"),
        new Store("lv.farnell.com", "Latvia", "EUR"),
        new Store("lt.farnell.com", "Lithuania", "EUR"),
        new Store("bg.farnell.com", "Bulgaria", "EUR"),
        new Store("ch.farnell.com", "Switzerland", "CHF"),
        new Store("se.farnell.com", "Sweden", "SEK"),
        new Store("dk.farnell.com", "Denmark", "DKK"),
        new Store("no.farnell.com", "Norway", "NOK"),
        new Store("pl.farnell.com", "Poland", "PLN"),
        new Store("cz.farnell.com", "Czech Republic", "CZK"),
        new Store("hu.farnell.com", "Hungary", "HUF"),
        new Store("ro.farnell.com", "Romania", "RON"),
        new Store("il.farnell.com", "Israel", "USD"),
        new Store("tr.farnell.com", "Turkey", "EUR"),
        new Store("export.farnell.com", "Export (other countries)", "GBP"),
        new Store("au.element14.com", "Australia", "AUD", "en_US"),
        new Store("nz.element14.com", "New Zealand", "NZD", "en_US"),
        new Store("sg.element14.com", "Singapore", "SGD"),
        new Store("my.element14.com", "Malaysia", "MYR"),
        new Store("in.element14.com", "India", "INR"),
        new Store("cn.element14.com", "China", "CNY"),
        new Store("hk.element14.com", "Hong Kong", "HKD"),
        new Store("tw.element14.com", "Taiwan", "TWD"),
        new Store("kr.element14.com", "South Korea", "KRW"),
        new Store("th.element14.com", "Thailand", "THB"),
        new Store("ph.element14.com", "Philippines", "PHP"),
        new Store("vn.element14.com", "Vietnam", "USD")
    };

    /// <summary>The store with this id, or the UK one if it isn't known.</summary>
    public static Store FindFarnellStore(string? id)
    {
        return FarnellStores.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? FarnellStores[0];
    }

    private readonly string _storeId;
    private readonly string _currency;
    private readonly string _imageLocale;
    private readonly string _apiKey;

    public Element14Supplier(string name, string storeId, string currency, string? apiKey, string searchUrlTemplate,
                             string imageLocale = "en_GB")
        : base(name, searchUrlTemplate)
    {
        _storeId = storeId;
        _currency = currency;
        _imageLocale = imageLocale;
        _apiKey = (apiKey ?? "").Trim();
    }

    public override bool IsConfigured => _apiKey.Length > 0;

    public override async Task<SearchPage> SearchAsync(string query, SearchMode mode, int maxResults, int offset,
                                                       SupplierFilters? filters, CancellationToken ct)
    {
        filters ??= SupplierFilters.None;
        // any: = keyword, manuPartNum: = manufacturer part no., id: = Farnell/CPC/Newark order code
        var prefix = mode == SearchMode.Keyword ? "any:" : mode == SearchMode.SupplierCode ? "id:" : "manuPartNum:";
        var term = prefix + query;
        // element14 filters on its side: resultsSettings.refinements.filters=inStock,rohsCompliant
        var refine = new List<string>();
        if (filters.InStockOnly) refine.Add("inStock");
        if (filters.RohsOnly) refine.Add("rohsCompliant");
        // (The API accepts at least 100 per request - tested - but 50 keeps it in step with the others.)
        var url = BaseUrl +
            "?term=" + Uri.EscapeDataString(term) +
            "&storeInfo.id=" + _storeId +
            "&resultsSettings.offset=" + offset.ToString() +
            "&resultsSettings.numberOfResults=" + Math.Min(maxResults, 50).ToString() +
            (refine.Count > 0 ? "&resultsSettings.refinements.filters=" + string.Join(",", refine) : "") +
            "&resultsSettings.responseGroup=large" +
            "&callInfo.responseDataFormat=json" +
            "&callInfo.apiKey=" + Uri.EscapeDataString(_apiKey);

        await _rateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastCallUtc + MinCallSpacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var page = Parse(await SendForJsonAsync(req, ct).ConfigureAwait(false));
                if (mode == SearchMode.ExactPartNumber)
                {
                    page = new SearchPage(page.Parts.Where(p => SamePartNumber(p.ManufacturerPartNumber, query)).ToList(), null);
                    page.TotalAvailable = page.Parts.Count;
                }
                if (filters.InStockOnly && mode != SearchMode.Keyword)
                {
                    page.Parts = page.Parts.Where(p => p.Stock.GetValueOrDefault() > 0).ToList();
                }
                return page;
            }
            finally
            {
                _lastCallUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _rateGate.Release();
        }
    }

    private SearchPage Parse(JToken root)
    {
        if (root.GetPath("Fault") is JObject fault)
        {
            throw new SupplierException(Truncate(fault.ToString(Formatting.None), 300));
        }

        // The wrapper name depends on the search type, e.g. "keywordSearchReturn",
        // "manufacturerPartNumberSearchReturn" - so take whichever "...Return" object is there.
        JToken? wrapper = null;
        if (root is JObject rootObj)
        {
            foreach (var prop in rootObj.Properties())
            {
                if (prop.Name.EndsWith("Return", StringComparison.OrdinalIgnoreCase))
                {
                    wrapper = prop.Value;
                    break;
                }
            }
        }

        var results = new List<PartResult>();
        foreach (var p in wrapper.GetArr("products"))
        {
            var sku = p.GetStr("sku");
            var stock = ToNullableLong(p.GetNum("stock", "level"));
            if (!stock.HasValue) stock = ToNullableLong(p.GetNum("inv"));

            var moq = p.GetNum("translatedMinimumOrderQuality");   // (sic - that's the API's spelling)
            if (!moq.HasValue) moq = p.GetNum("translatedMinimumOrderQuantity");

            var lead = p.GetStr("stock", "leastLeadTime");
            var r = new PartResult
            {
                Supplier = Name,
                SupplierPartNumber = sku,
                ManufacturerPartNumber = p.GetStr("translatedManufacturerPartNumber"),
                Manufacturer = p.GetStr("brandName").Length > 0 ? p.GetStr("brandName") : p.GetStr("vendorName"),
                Description = p.GetStr("displayName"),
                Category = TypeFromName(p.GetStr("displayName")),
                Stock = stock,
                StockInfo = lead.Length > 0 && lead != "0" ? $"Lead {lead} days" : "",
                Currency = _currency,
                MinOrderQty = ToNullableInt(moq),
                Status = p.GetStr("productStatus"),
                ProductUrl = GetSearchUrl(sku)
            };
            foreach (var pb in p.GetArr("prices"))
            {
                var qty = ToNullableInt(pb.GetNum("from"));
                var cost = pb.GetNum("cost");
                if (qty.HasValue && cost.HasValue) r.PriceBreaks.Add(new PriceBreak(qty.Value, cost.Value));
            }
            if (r.PriceBreaks.Count > 0) r.UnitPrice = r.PriceBreaks.OrderBy(b => b.Quantity).First().Price;

            var ds = p.GetArr("datasheets").FirstOrDefault();
            r.DatasheetUrl = ds.GetStr("url");

            // Only the file name comes back, e.g. "/GE8SOIC-40.jpg". Most stores (Newark too) serve it from
            // the en_GB folder; Australia and New Zealand only from en_US.
            var imageFile = p.GetStr("image", "baseName");
            if (imageFile.Length > 0)
            {
                r.ImageUrl = $"https://{_storeId}/productimages/standard/{_imageLocale}{imageFile}";
                r.LargeImageUrl = $"https://{_storeId}/productimages/large/{_imageLocale}{imageFile}";
            }
            results.Add(r);
        }
        return new SearchPage(results, ToNullableInt(wrapper.GetNum("numberOfResults")));
    }

    /// <summary>
    /// element14 sends no category, but every product name reads "MAKER - PART NO - Type, details…",
    /// e.g. "ANALOG DEVICES - AD7781BRUZ - Analogue to Digital Converter, Delta-Sigma, 20 bit". Returns the type.
    /// </summary>
    private static string TypeFromName(string displayName)
    {
        var parts = displayName.Split(new[] { " - " }, 3, StringSplitOptions.None);
        if (parts.Length < 3) return "";
        return parts[2].Split(',')[0].Trim();
    }
}
