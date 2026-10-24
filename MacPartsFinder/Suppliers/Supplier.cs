using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MacPartsFinder;

/// <summary>
/// Base class for all suppliers. Suppliers with an API override SearchAsync;
/// the rest just supply a website search link.
/// </summary>
public abstract class Supplier
{
    protected Supplier(string name, string searchUrlTemplate)
    {
        Name = name;
        SearchUrlTemplate = searchUrlTemplate;
    }

    public string Name { get; }
    public string SearchUrlTemplate { get; set; }

    /// <summary>True when the API can be called (key present etc).</summary>
    public abstract bool IsConfigured { get; }

    /// <summary>Short text for the supplier list, e.g. "API" or "Web link".</summary>
    public abstract string ModeText { get; }

    /// <summary>Fetches one page of results, starting at <paramref name="offset"/> (0 = first page).
    /// <paramref name="filters"/> are applied by the supplier where its API can (in stock, RoHS).</summary>
    public virtual Task<SearchPage> SearchAsync(string query, SearchMode mode, int maxResults, int offset,
                                                SupplierFilters? filters, CancellationToken ct)
    {
        throw new NotSupportedException($"{Name} has no API search.");
    }

    /// <summary>Whether "Load more" (next page) works for this kind of search.</summary>
    public virtual bool CanLoadMore(SearchMode mode)
    {
        return false;
    }

    /// <summary>For exact / code searches: same part number, ignoring case and spaces.</summary>
    public static bool SamePartNumber(string? a, string? b)
    {
        return string.Equals((a ?? "").Replace(" ", "").Trim(), (b ?? "").Replace(" ", "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public string GetSearchUrl(string query)
    {
        return SearchUrlTemplate.Replace("{0}", Uri.EscapeDataString(query));
    }

    public PartResult MakeLinkResult(string query)
    {
        return new PartResult
        {
            Supplier = Name,
            Description = $"Search {Name} website for \"{query}\" (double-click to open)",
            StockInfo = "see website",
            ProductUrl = GetSearchUrl(query),
            IsLinkOnly = true
        };
    }
}

/// <summary>Supplier with no usable public API: opens the website search.</summary>
public class WebLinkSupplier : Supplier
{
    public WebLinkSupplier(string name, string searchUrlTemplate) : base(name, searchUrlTemplate)
    {
    }

    public override bool IsConfigured => false;

    public override string ModeText => "Web link";
}

/// <summary>Base for suppliers that have an API but need a key.</summary>
public abstract class ApiSupplier : Supplier
{
    protected ApiSupplier(string name, string searchUrlTemplate) : base(name, searchUrlTemplate)
    {
    }

    public override string ModeText => IsConfigured ? "API" : "API (no key)";

    // Exact and order-code searches return a handful of parts, so there's no "next page".
    public override bool CanLoadMore(SearchMode mode)
    {
        return mode == SearchMode.Keyword || mode == SearchMode.PartNumber;
    }
}

/// <summary>One page of results from a supplier.</summary>
public class SearchPage
{
    public List<PartResult> Parts { get; set; }
    /// <summary>How many matches the supplier says it has in total, if it said.</summary>
    public int? TotalAvailable { get; set; }

    public SearchPage(List<PartResult> parts, int? totalAvailable)
    {
        Parts = parts;
        TotalAvailable = totalAvailable;
    }
}

public static class SupplierFactory
{
    public static List<Supplier> Create(AppSettings s)
    {
        var all = new Dictionary<string, Supplier>
        {
            { AppSettings.RS, new WebLinkSupplier(AppSettings.RS, s.GetSearchUrl(AppSettings.RS)) },
            { AppSettings.Farnell, new Element14Supplier(AppSettings.Farnell, "uk.farnell.com", "GBP",
                                                         s.Element14ApiKey, s.GetSearchUrl(AppSettings.Farnell)) },
            { AppSettings.CPC, new Element14Supplier(AppSettings.CPC, "cpc.farnell.com", "GBP",
                                                     s.Element14ApiKey, s.GetSearchUrl(AppSettings.CPC)) },
            { AppSettings.DigiKey, new DigiKeySupplier(s.DigiKeyClientId, s.DigiKeyClientSecret, s.DigiKeySite,
                                                       s.DigiKeyLanguage, s.DigiKeyCurrency, s.GetSearchUrl(AppSettings.DigiKey)) },
            { AppSettings.Mouser, new MouserSupplier(s.MouserApiKey, s.GetSearchUrl(AppSettings.Mouser)) },
            { AppSettings.Newark, new Element14Supplier(AppSettings.Newark, "www.newark.com", "USD",
                                                        s.Element14ApiKey, s.GetSearchUrl(AppSettings.Newark)) },
            { AppSettings.TME, new TmeSupplier(s.TmeToken, s.TmeSecret, s.TmeCountry, s.TmeCurrency, s.GetSearchUrl(AppSettings.TME)) },
            { AppSettings.Rapid, new WebLinkSupplier(AppSettings.Rapid, s.GetSearchUrl(AppSettings.Rapid)) },
            { AppSettings.LCSC, new WebLinkSupplier(AppSettings.LCSC, s.GetSearchUrl(AppSettings.LCSC)) },
            { AppSettings.AliExpress, new WebLinkSupplier(AppSettings.AliExpress, s.GetSearchUrl(AppSettings.AliExpress)) }
        };
        var result = AppSettings.SupplierOrder.Select(n => all[n]).ToList();
        foreach (var c in s.CustomSuppliers)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || all.ContainsKey(c.Name)) continue;
            result.Add(new WebLinkSupplier(c.Name.Trim(), c.SearchUrl.Trim()));
        }
        return result;
    }
}
