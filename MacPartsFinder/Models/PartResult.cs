using System;
using System.Collections.Generic;
using System.Linq;

namespace MacPartsFinder;

/// <summary>One line of search results from a supplier.</summary>
public class PartResult
{
    public string Supplier { get; set; } = "";
    public string SupplierPartNumber { get; set; } = "";
    public string ManufacturerPartNumber { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Kind of part, e.g. "Analog to Digital Converters - ADC" - the supplier's own category wording.</summary>
    public string Category { get; set; } = "";

    /// <summary>Quantity in stock, or null when the supplier didn't say.</summary>
    public long? Stock { get; set; }
    /// <summary>Free-text availability / lead time info.</summary>
    public string StockInfo { get; set; } = "";

    /// <summary>Unit price at the lowest price break.</summary>
    public decimal? UnitPrice { get; set; }
    public string Currency { get; set; } = "";
    public List<PriceBreak> PriceBreaks { get; set; } = new();
    public int? MinOrderQty { get; set; }

    public string Status { get; set; } = "";
    public string ProductUrl { get; set; } = "";
    public string DatasheetUrl { get; set; } = "";

    /// <summary>Product photo for the grid thumbnail.</summary>
    public string ImageUrl { get; set; } = "";
    /// <summary>Bigger version for the pop-up, if the supplier has one (else ImageUrl is used).</summary>
    public string LargeImageUrl { get; set; } = "";

    /// <summary>True when this row is just a "search on the website" link (no API data).</summary>
    public bool IsLinkOnly { get; set; }

    public string PriceBreaksText()
    {
        return string.Join("   ", PriceBreaks.OrderBy(b => b.Quantity)
                                             .Select(b => $"{b.Quantity}+ {b.Price:0.00##}"));
    }
}

public class PriceBreak
{
    public int Quantity { get; set; }
    public decimal Price { get; set; }

    public PriceBreak(int quantity, decimal price)
    {
        Quantity = quantity;
        Price = price;
    }
}

public enum SearchMode
{
    Keyword,
    /// <summary>Manufacturer part number starts with the search text.</summary>
    PartNumber,
    /// <summary>Manufacturer part number is exactly the search text.</summary>
    ExactPartNumber,
    /// <summary>A supplier's own order code (Farnell/CPC/Newark, Mouser, DigiKey or TME number).</summary>
    SupplierCode
}

/// <summary>Filters the suppliers apply on their side (so a page of 50 is 50 matching parts).</summary>
public class SupplierFilters
{
    public bool InStockOnly { get; set; }
    public bool RohsOnly { get; set; }

    public static readonly SupplierFilters None = new();
}

/// <summary>Everything set in the Advanced search window. Also saved, so it reopens with the last choices.</summary>
public class AdvancedSearchOptions
{
    public string Query { get; set; } = "";
    public SearchMode Mode { get; set; } = SearchMode.Keyword;
    public int MaxResults { get; set; } = 25;

    // Applied by the suppliers
    public bool InStockOnly { get; set; }
    public bool RohsOnly { get; set; }

    // Applied by the app to what comes back
    public bool ActiveOnly { get; set; }
    public string ManufacturerContains { get; set; } = "";
    /// <summary>Only parts whose manufacturer part number contains this (case and spaces ignored), e.g. 931G.</summary>
    public string PartNoContains { get; set; } = "";
    public string MustContain { get; set; } = "";
    /// <summary>
    /// True when Search for was left blank, so the part-number fragment and MustContain words were searched for
    /// instead. The words are then not checked again letter-for-letter (the suppliers matched them their own way),
    /// and the window reopens with Search for blank.
    /// </summary>
    public bool ContainsIsQuery { get; set; }
    /// <summary>0 = not set. When set, prices are shown at this quantity and parts with a higher MOQ are left out.</summary>
    public int Quantity { get; set; }
    /// <summary>null = no limit. Compared with the price at Quantity (or at 1).</summary>
    public decimal? MaxUnitPrice { get; set; }

    /// <summary>Suppliers to search (names). Empty = whatever is ticked in the main list.</summary>
    public List<string> Suppliers { get; set; } = new();

    public SupplierFilters SupplierFilters()
    {
        return new SupplierFilters { InStockOnly = InStockOnly, RohsOnly = RohsOnly };
    }

    /// <summary>Short summary for the status bar, e.g. "in stock, RoHS, qty 25". Empty if nothing is set.</summary>
    public string Describe()
    {
        var bits = new List<string>();
        switch (Mode)
        {
            case SearchMode.PartNumber: bits.Add("mfr part no."); break;
            case SearchMode.ExactPartNumber: bits.Add("exact part no."); break;
            case SearchMode.SupplierCode: bits.Add("supplier code"); break;
        }
        if (InStockOnly) bits.Add("in stock");
        if (RohsOnly) bits.Add("RoHS");
        if (ActiveOnly) bits.Add("active only");
        if (ManufacturerContains.Trim().Length > 0) bits.Add($"maker \"{ManufacturerContains.Trim()}\"");
        if (PartNoContains.Trim().Length > 0) bits.Add($"part no. contains \"{PartNoContains.Trim()}\"");
        if (WordsToCheck.Length > 0) bits.Add($"contains \"{WordsToCheck}\"");
        if (Quantity > 0) bits.Add($"qty {Quantity}");
        if (MaxUnitPrice.HasValue) bits.Add($"max {MaxUnitPrice.Value:0.00##} each");
        return string.Join(", ", bits);
    }

    /// <summary>The Description contains words to check in each part - none when they were the search itself.</summary>
    private string WordsToCheck => ContainsIsQuery ? "" : MustContain.Trim();

    /// <summary>True if any of the app-side filters are set.</summary>
    public bool HasAppFilters =>
        ActiveOnly || ManufacturerContains.Trim().Length > 0 || PartNoContains.Trim().Length > 0 || WordsToCheck.Length > 0 ||
        Quantity > 0 || MaxUnitPrice.HasValue;

    /// <summary>App-side filters. Website-link rows always pass.</summary>
    /// <param name="toShownCurrency">When prices are shown in one currency, converts a price so Max price each
    /// is compared in that currency. null = compare in the supplier's own currency.</param>
    public bool Matches(PartResult p, Func<decimal, string, decimal>? toShownCurrency = null)
    {
        if (p.IsLinkOnly) return true;
        if (ActiveOnly && IsNotActive(p.Status)) return false;
        if (ManufacturerContains.Trim().Length > 0 &&
            p.Manufacturer.IndexOf(ManufacturerContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (PartNoContains.Trim().Length > 0 &&
            NoSpaces(p.ManufacturerPartNumber).IndexOf(NoSpaces(PartNoContains), StringComparison.OrdinalIgnoreCase) < 0) return false;
        foreach (var word in WordsToCheck.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (p.Description.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0 &&
                p.ManufacturerPartNumber.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        if (Quantity > 0 && p.MinOrderQty.HasValue && p.MinOrderQty.Value > Quantity) return false;
        if (MaxUnitPrice.HasValue)
        {
            // No price listed counts as "not under the limit" - those usually can't be bought anyway.
            var price = PriceAt(p, Math.Max(1, Quantity));
            if (price.HasValue && toShownCurrency != null) price = toShownCurrency(price.Value, p.Currency);
            if (!price.HasValue || price.Value > MaxUnitPrice.Value) return false;
        }
        return true;
    }

    /// <summary>Unit price when buying <paramref name="qty"/>: the highest price break at or below that quantity.</summary>
    public static decimal? PriceAt(PartResult p, int qty)
    {
        if (p.PriceBreaks.Count == 0) return p.UnitPrice;
        var ordered = p.PriceBreaks.OrderBy(b => b.Quantity).ToList();
        var best = ordered[0];
        foreach (var b in ordered)
        {
            if (b.Quantity <= qty) best = b;
        }
        return best.Price;
    }

    private static string NoSpaces(string? s)
    {
        return (s ?? "").Replace(" ", "");
    }

    private static bool IsNotActive(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false; // unknown - keep it
        foreach (var word in new[] { "obsolete", "discontinued", "not recommended", "nrnd", "end of life", "eol",
                                     "last time", "no longer", "not for new" })
        {
            if (status.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }
}
