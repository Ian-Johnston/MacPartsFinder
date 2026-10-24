using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MacPartsFinder;

/// <summary>
/// User settings. Loading, saving and the API key store (macOS Keychain) come in the next step;
/// for now this holds the values the suppliers are built from.
/// </summary>
public class AppSettings
{
    // Supplier names - also used as dictionary keys in this file.
    public const string RS = "RS Components";
    public const string Farnell = "Farnell";
    public const string CPC = "CPC";
    public const string DigiKey = "DigiKey";
    public const string Mouser = "Mouser";
    public const string Newark = "Newark";
    public const string TME = "TME";
    public const string Rapid = "Rapid";
    public const string LCSC = "LCSC";
    public const string AliExpress = "AliExpress";

    public static readonly string[] SupplierOrder = { Farnell, CPC, DigiKey, Mouser, Newark, TME, RS, Rapid, LCSC, AliExpress };

    /// <summary>Website search URLs. {0} is replaced by the URL-encoded search text.</summary>
    public static readonly Dictionary<string, string> DefaultSearchUrls = new()
    {
        { RS, "https://uk.rs-online.com/web/c/?searchTerm={0}" },
        { Farnell, "https://uk.farnell.com/search?st={0}" },
        { CPC, "https://cpc.farnell.com/search?st={0}" },
        { DigiKey, "https://www.digikey.co.uk/en/products/result?keywords={0}" },
        { Mouser, "https://www.mouser.co.uk/c/?q={0}" },
        { Newark, "https://www.newark.com/search?st={0}" },
        { TME, "https://www.tme.eu/gb/en/katalog/?search={0}" },
        { Rapid, "https://www.rapidonline.com/search?query={0}" },
        { LCSC, "https://www.lcsc.com/search?q={0}" },
        { AliExpress, "https://www.aliexpress.com/wholesale?SearchText={0}" }
    };

    // ---- Secrets (in memory only - never written to the settings file) ----
    [JsonIgnore] public string MouserApiKey { get; set; } = "";
    [JsonIgnore] public string DigiKeyClientId { get; set; } = "";
    [JsonIgnore] public string DigiKeyClientSecret { get; set; } = "";
    [JsonIgnore] public string Element14ApiKey { get; set; } = "";
    [JsonIgnore] public string TmeToken { get; set; } = "";
    [JsonIgnore] public string TmeSecret { get; set; } = "";

    // ---- DigiKey locale ----
    public string DigiKeySite { get; set; } = "UK";
    public string DigiKeyLanguage { get; set; } = "en";
    public string DigiKeyCurrency { get; set; } = "GBP";

    // ---- TME locale (country decides stock/prices offered; currency of the prices) ----
    public string TmeCountry { get; set; } = "GB";
    public string TmeCurrency { get; set; } = "GBP";

    // ---- General ----
    public int MaxResults { get; set; } = 25;
    public bool InStockOnly { get; set; } = false;
    public bool ShowThumbnails { get; set; } = true;
    public bool DarkMode { get; set; } = false;
    /// <summary>User-added website-link suppliers, searched after the built-in ones.</summary>
    public List<CustomSupplier> CustomSuppliers { get; set; } = new();
    /// <summary>Main window size/position from last time; null = default.</summary>
    public WindowPlacement? MainWindow { get; set; }
    /// <summary>Results grid column widths and order, keyed by column name.</summary>
    public Dictionary<string, ColumnLayout> ResultColumns { get; set; } = new();
    public Dictionary<string, bool> EnabledSuppliers { get; set; } = new();
    public Dictionary<string, string> SearchUrls { get; set; } = new();
    public List<string> RecentSearches { get; set; } = new();
    /// <summary>The Advanced search window reopens with these - while the app is open only (not saved),
    /// so every run starts with a plain Advanced search.</summary>
    [JsonIgnore] public AdvancedSearchOptions LastAdvanced { get; set; } = new();

    public string GetSearchUrl(string supplierName)
    {
        if (SearchUrls.TryGetValue(supplierName, out var url) && !string.IsNullOrWhiteSpace(url)) return url;
        if (DefaultSearchUrls.TryGetValue(supplierName, out url)) return url;
        return "";
    }

    public bool IsEnabled(string supplierName)
    {
        return !EnabledSuppliers.TryGetValue(supplierName, out var v) || v;
    }

    public void AddRecentSearch(string text)
    {
        RecentSearches.RemoveAll(s => string.Equals(s, text, StringComparison.OrdinalIgnoreCase));
        RecentSearches.Insert(0, text);
        if (RecentSearches.Count > 100) RecentSearches.RemoveRange(100, RecentSearches.Count - 100);
    }
}

/// <summary>Where the main window was, in its normal (not maximised) state.</summary>
public class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public int SplitterDistance { get; set; }
}

public class ColumnLayout
{
    public int Width { get; set; }
    public int DisplayIndex { get; set; }
}

/// <summary>A supplier the user added in Settings: just a name and a website search URL.</summary>
public class CustomSupplier
{
    public string Name { get; set; } = "";
    /// <summary>{0} is replaced by the URL-encoded search text.</summary>
    public string SearchUrl { get; set; } = "";
}
