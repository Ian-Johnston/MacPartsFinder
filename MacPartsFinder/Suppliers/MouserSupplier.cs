using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MacPartsFinder.ParseHelpers;
using static MacPartsFinder.WebHelper;

namespace MacPartsFinder;

/// <summary>
/// Mouser Search API v1. Free key from https://www.mouser.co.uk/api-hub/ (Search API).
/// Prices come back in the currency of the account the key was registered with.
/// </summary>
public class MouserSupplier : ApiSupplier
{
    private const string BaseUrl = "https://api.mouser.com/api/v1/search/";
    private readonly string _apiKey;

    public MouserSupplier(string? apiKey, string searchUrlTemplate) : base(AppSettings.Mouser, searchUrlTemplate)
    {
        _apiKey = (apiKey ?? "").Trim();
    }

    public override bool IsConfigured => _apiKey.Length > 0;

    // The part-number endpoint has no paging; keyword search does (startingRecord).
    public override bool CanLoadMore(SearchMode mode)
    {
        // (Only keyword search pages.)
        return mode == SearchMode.Keyword;
    }

    public override async Task<SearchPage> SearchAsync(string query, SearchMode mode, int maxResults, int offset,
                                                       SupplierFilters? filters, CancellationToken ct)
    {
        filters ??= SupplierFilters.None;
        string endpoint;
        object payload;
        if (mode == SearchMode.Keyword)
        {
            // Mouser filters keyword searches itself: None / Rohs / InStock / RohsAndInStock.
            var options = filters.InStockOnly && filters.RohsOnly ? "RohsAndInStock"
                        : filters.InStockOnly ? "InStock"
                        : filters.RohsOnly ? "Rohs" : "None";
            endpoint = "keyword";
            payload = new
            {
                SearchByKeywordRequest = new
                {
                    keyword = query,
                    records = Math.Min(maxResults, 50),
                    startingRecord = offset,
                    searchOptions = options,
                    searchWithYourSignUpLanguage = "false"
                }
            };
        }
        else
        {
            // Part number, exact part number or Mouser order code ("595-LM358DR") - the part-number endpoint.
            var exact = mode == SearchMode.ExactPartNumber || mode == SearchMode.SupplierCode;
            endpoint = "partnumber";
            payload = new
            {
                SearchByPartRequest = new { mouserPartNumber = query, partSearchOptions = exact ? "Exact" : "None" }
            };
        }

        var url = $"{BaseUrl}{endpoint}?apiKey={Uri.EscapeDataString(_apiKey)}";
        var json = JsonConvert.SerializeObject(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var root = await SendForJsonAsync(req, ct).ConfigureAwait(false);
        var parts = Parse(root, maxResults);
        var total = ToNullableInt(root.GetNum("SearchResults", "NumberOfResult"));
        if (mode != SearchMode.Keyword)
        {
            // The part-number endpoint can't filter, so do it here.
            if (filters.InStockOnly) parts = parts.Where(p => p.Stock.GetValueOrDefault() > 0).ToList();
            if (mode == SearchMode.ExactPartNumber)
                parts = parts.Where(p => SamePartNumber(p.ManufacturerPartNumber, query)).ToList();
            else if (mode == SearchMode.SupplierCode)
                parts = parts.Where(p => SamePartNumber(p.SupplierPartNumber, query)).ToList();
            total = parts.Count;
        }
        return new SearchPage(parts, total);
    }

    private List<PartResult> Parse(JToken root, int maxResults)
    {
        var errors = root.GetArr("Errors");
        if (errors.Count > 0)
        {
            throw new SupplierException(string.Join("; ", errors.Select(er => er.GetStr("Message"))));
        }

        var results = new List<PartResult>();
        foreach (var p in root.GetArr("SearchResults", "Parts").Take(maxResults))
        {
            var availability = p.GetStr("Availability");
            var stock = ParseLongLoose(p.GetStr("AvailabilityInStock"));
            if (!stock.HasValue)
            {
                // "1,234 In Stock" or "None" / "On Order"
                stock = availability.IndexOf("In Stock", StringComparison.OrdinalIgnoreCase) >= 0
                    ? ParseLongLoose(availability) : 0L;
            }

            var leadTime = p.GetStr("LeadTime");
            if (ParseLongLoose(leadTime).GetValueOrDefault() == 0) leadTime = ""; // "0 Days" means unknown

            var mouserPn = p.GetStr("MouserPartNumber");
            var notStocked = mouserPn.Equals("N/A", StringComparison.OrdinalIgnoreCase);

            var r = new PartResult
            {
                Supplier = Name,
                SupplierPartNumber = notStocked ? "" : mouserPn,
                ManufacturerPartNumber = p.GetStr("ManufacturerPartNumber"),
                Manufacturer = p.GetStr("Manufacturer"),
                Description = p.GetStr("Description"),
                Category = p.GetStr("Category"),
                Stock = stock,
                StockInfo = JoinNonEmpty(notStocked ? "Not stocked by Mouser" : availability,
                                         leadTime.Length > 0 ? "Lead " + leadTime : ""),
                MinOrderQty = ToNullableInt(ParseDecimalLoose(p.GetStr("Min"))),
                Status = p.GetStr("LifecycleStatus"),
                ProductUrl = p.GetStr("ProductDetailUrl"),
                DatasheetUrl = p.GetStr("DataSheetUrl")
            };
            // ImageUrl is deliberately left blank: Mouser's image server rejects non-browser
            // requests ("Access denied"). The grid borrows a photo of the same MPN from
            // another supplier instead.
            foreach (var pb in p.GetArr("PriceBreaks"))
            {
                var price = ParseDecimalLoose(pb.GetStr("Price"));
                var qty = ToNullableInt(pb.GetNum("Quantity"));
                if (price.HasValue && qty.HasValue) r.PriceBreaks.Add(new PriceBreak(qty.Value, price.Value));
                if (r.Currency.Length == 0) r.Currency = pb.GetStr("Currency");
            }
            if (r.PriceBreaks.Count > 0) r.UnitPrice = r.PriceBreaks.OrderBy(b => b.Quantity).First().Price;
            if (r.Status.Length == 0 && p.GetStr("IsDiscontinued") == "true") r.Status = "Discontinued";
            results.Add(r);
        }
        return results;
    }

    private static string JoinNonEmpty(params string[] parts)
    {
        return string.Join(" | ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
