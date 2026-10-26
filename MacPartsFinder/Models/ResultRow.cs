using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace MacPartsFinder;

/// <summary>One row of the results grid (a PartResult plus the grid's own bookkeeping).</summary>
public sealed class ResultRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Supplier { get; init; } = "";
    public string MfrPartNo { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";

    // Package: the supplier's own, or borrowed from another supplier's row with the same manufacturer part no.
    private string _package = "";
    public string Package { get => _package; set => Set(ref _package, value); }
    /// <summary>Supplier the package was borrowed from; "" = the row's own (shown in orange when borrowed).</summary>
    public string PackageFrom { get; set; } = "";
    /// <summary>Tooltip on a borrowed package: whose it is (null = none).</summary>
    public string? PackageTip => PackageFrom.Length == 0 ? null
        : $"{Supplier} doesn't give the package - this is {PackageFrom}'s, for the same manufacturer part number ({MfrPartNo}).";

    public long? Stock { get; init; }

    // Price, Cur and Price breaks: the supplier's own, or converted when "Prices in GBP" is ticked.
    private decimal? _unitPrice;
    public decimal? UnitPrice { get => _unitPrice; set => Set(ref _unitPrice, value); }
    private string _currency = "";
    public string Currency { get => _currency; set => Set(ref _currency, value); }
    private string _priceBreaks = "";
    public string PriceBreaks { get => _priceBreaks; set => Set(ref _priceBreaks, value); }

    // The supplier's own prices, kept while converted ones are shown.
    public decimal? OrigUnitPrice { get; init; }
    public string OrigCurrency { get; init; } = "";
    public string OrigBreaks { get; init; } = "";
    /// <summary>The breaks as numbers, "1:0.2;10:0.139", for converting.</summary>
    public string BreaksRaw { get; init; } = "";
    /// <summary>True while the prices shown are converted (shown in purple).</summary>
    public bool Converted { get; set; }
    public decimal? RateUsed { get; set; }
    private string? _priceTip;
    /// <summary>Tooltip on the price cells: the supplier's own price and the rate used (null = none).</summary>
    public string? PriceTip { get => _priceTip; set => Set(ref _priceTip, value); }

    public int? Moq { get; init; }
    public string Availability { get; init; } = "";
    public string SupplierPartNo { get; init; } = "";
    public string Status { get; init; } = "";
    public string ProductUrl { get; init; } = "";
    public string DatasheetUrl { get; init; } = "";
    public string ImageUrl { get; init; } = "";
    public string LargeImageUrl { get; init; } = "";
    public bool LinkOnly { get; init; }

    /// <summary>Rows the user removed. They stay in the list so Restore can bring them back.</summary>
    public bool Removed { get; set; }
    /// <summary>Hidden order key: supplier's place in the supplier list, then the row's number within that supplier.
    /// Sorting by Supplier uses it: every supplier's rows together, loaded-more ones included.</summary>
    public int Seq { get; init; }
    /// <summary>The supplier's place in the supplier list (Seq without the row number), so "Supplier, then Price"
    /// sorts the prices within each supplier.</summary>
    public int SupplierRank { get; init; }
    /// <summary>Hidden default order: the search first, then each Load more batch at the end - each grouped by supplier.</summary>
    public long Order { get; init; }

    private int? _no;
    /// <summary>Row number shown in the # column. Given out when a search / Load more finishes and never changes,
    /// so a number written down still finds the same part after removing, filtering or sorting.</summary>
    public int? No
    {
        get => _no;
        set
        {
            _no = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(No)));
        }
    }

    /// <summary>Rows with a page to open but no supplier part no. still get a clickable link.</summary>
    public string SupplierPartText =>
        SupplierPartNo.Length == 0 && ProductUrl.Length > 0 ? (LinkOnly ? "Search website" : "View") : SupplierPartNo;

    public bool HasProductLink => SupplierPartText.Length > 0 && ProductUrl.Length > 0;
    public bool HasDatasheet => DatasheetUrl.Length > 0;

    // ---- Photo column (set by the main window as thumbnails arrive) ----

    private Bitmap? _thumb;
    public Bitmap? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    private bool _noPhoto;
    /// <summary>True shows the "no image" cross; false with no Thumb is the empty "loading" box.</summary>
    public bool NoPhoto
    {
        get => _noPhoto;
        set
        {
            _noPhoto = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoPhoto)));
        }
    }

    /// <summary>The photo URL Thumb / NoPhoto currently show ("" = none); null until the row is first shown.</summary>
    public string? ThumbFor { get; set; }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
