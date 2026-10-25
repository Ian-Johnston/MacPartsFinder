using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace MacPartsFinder;

/// <summary>
/// User settings, stored as JSON in ~/Library/Application Support/MacPartsFinder/settings.json on a Mac
/// (%AppData%\MacPartsFinder\settings.json on Windows - separate from WinPartsFinder's settings).
/// API keys/secrets are not in that file: they go in the secret store (macOS Keychain / Windows DPAPI).
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

    // ---- Secrets (never written to the settings file - see SecretFields) ----
    [JsonIgnore] public string MouserApiKey { get; set; } = "";
    [JsonIgnore] public string DigiKeyClientId { get; set; } = "";
    [JsonIgnore] public string DigiKeyClientSecret { get; set; } = "";
    [JsonIgnore] public string Element14ApiKey { get; set; } = "";
    [JsonIgnore] public string TmeToken { get; set; } = "";
    [JsonIgnore] public string TmeSecret { get; set; } = "";

    /// <summary>Each secret's name in the store, and how to read/write it here.</summary>
    private static readonly (string Name, Func<AppSettings, string> Get, Action<AppSettings, string> Set)[] SecretFields =
    {
        (nameof(MouserApiKey), s => s.MouserApiKey, (s, v) => s.MouserApiKey = v),
        (nameof(DigiKeyClientId), s => s.DigiKeyClientId, (s, v) => s.DigiKeyClientId = v),
        (nameof(DigiKeyClientSecret), s => s.DigiKeyClientSecret, (s, v) => s.DigiKeyClientSecret = v),
        (nameof(Element14ApiKey), s => s.Element14ApiKey, (s, v) => s.Element14ApiKey = v),
        (nameof(TmeToken), s => s.TmeToken, (s, v) => s.TmeToken = v),
        (nameof(TmeSecret), s => s.TmeSecret, (s, v) => s.TmeSecret = v)
    };

    /// <summary>Secrets as last loaded/saved, so Save only touches the store when a key changed.</summary>
    private readonly Dictionary<string, string> _storedSecrets = new();

    // ---- DigiKey locale ----
    public string DigiKeySite { get; set; } = "UK";
    public string DigiKeyLanguage { get; set; } = "en";
    public string DigiKeyCurrency { get; set; } = "GBP";

    // ---- Farnell country store (element14), e.g. "nz.element14.com" - the same key works for all of them ----
    public string FarnellStore { get; set; } = "uk.farnell.com";

    // ---- TME locale (country decides stock/prices offered; currency of the prices) ----
    public string TmeCountry { get; set; } = "GB";
    public string TmeCurrency { get; set; } = "GBP";

    // ---- Show every price in one currency (toolbar "Prices in GBP") ----
    public bool ConvertPrices { get; set; } = false;
    public string ConvertCurrency { get; set; } = ExchangeRates.DefaultCurrency();
    /// <summary>The last exchange rates fetched, and when - only fetched again after 12 hours.</summary>
    public ExchangeRates Rates { get; set; } = new();

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

    /// <summary>Set by Load when the settings file couldn't be read (defaults used); "" if all was well.
    /// The main window shows it - there's no window yet while loading.</summary>
    [JsonIgnore] public string LoadError { get; private set; } = "";

    public static string Folder { get; } =
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MacPartsFinder")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacPartsFinder");

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    private static ISecretStore? _secretStore;
    private static ISecretStore Secrets => _secretStore ??= SecretStore.ForThisPlatform(Folder);

    public static AppSettings Load()
    {
        AppSettings? s = null;
        var error = "";
        try
        {
            if (File.Exists(FilePath))
            {
                // Replace (not add to) the default empty lists/dictionaries when reading.
                s = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(FilePath),
                        new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
            }
        }
        catch (Exception ex)
        {
            error = "Could not read settings, defaults will be used." + Environment.NewLine + ex.Message;
        }
        s ??= new AppSettings();
        s.LoadError = error;
        foreach (var f in SecretFields)
        {
            var value = Secrets.Get(f.Name);
            f.Set(s, value);
            s._storedSecrets[f.Name] = value;
        }
        return s;
    }

    /// <summary>Writes settings.json, then any keys that changed to the secret store. Throws if either fails.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        foreach (var f in SecretFields)
        {
            var value = f.Get(this);
            if (_storedSecrets.TryGetValue(f.Name, out var stored) && stored == value) continue;
            Secrets.Set(f.Name, value);
            _storedSecrets[f.Name] = value;
        }
    }

    public string GetSearchUrl(string supplierName)
    {
        if (SearchUrls.TryGetValue(supplierName, out var url) && !string.IsNullOrWhiteSpace(url)) return url;
        return DefaultSearchUrl(supplierName, FarnellStore);
    }

    /// <summary>The built-in website search URL. Farnell's follows the country store chosen in Settings.</summary>
    public static string DefaultSearchUrl(string supplierName, string? farnellStore)
    {
        if (supplierName == Farnell) return $"https://{Element14Supplier.FindFarnellStore(farnellStore).Id}/search?st={{0}}";
        return DefaultSearchUrls.TryGetValue(supplierName, out var url) ? url : "";
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
