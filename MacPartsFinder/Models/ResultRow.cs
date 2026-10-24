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
    public long? Stock { get; init; }
    public decimal? UnitPrice { get; init; }
    public string Currency { get; init; } = "";
    public string PriceBreaks { get; init; } = "";
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
}
