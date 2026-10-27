using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MacPartsFinder;

/// <summary>A name with a tick box, for tick lists.</summary>
public sealed class CheckItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; init; } = "";

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}

/// <summary>The Advanced search window. Closes with the chosen options, or null for Cancel.</summary>
public partial class AdvancedSearchWindow : Window
{
    private readonly List<CheckItem> _suppliers;

    // Needed by the XAML loader / previewer.
    public AdvancedSearchWindow() : this(new AdvancedSearchOptions(), new List<string>())
    {
    }

    /// <param name="start">What to show when the window opens (last time's choices).</param>
    /// <param name="supplierNames">Every supplier, in supplier-list order.</param>
    public AdvancedSearchWindow(AdvancedSearchOptions start, List<string> supplierNames)
    {
        InitializeComponent();
        cboMode.ItemsSource = new[]
        {
            "Keyword", "Manufacturer part no. (starts with)", "Exact manufacturer part no.",
            "Supplier order code - find the part at every supplier"
        };
        _suppliers = supplierNames.Select(n => new CheckItem { Name = n }).ToList();
        lstSuppliers.ItemsSource = _suppliers;
        ShowOptions(start ?? new AdvancedSearchOptions());
        Opened += (_, _) =>
        {
            FitSupplierColumns();
            txtQuery.Focus();
            txtQuery.SelectAll();
        };
    }

    private void LstSuppliers_SizeChanged(object? sender, SizeChangedEventArgs e) => FitSupplierColumns();

    // Two columns exactly fill the supplier list, so it only scrolls sideways if it ever needs a third.
    private void FitSupplierColumns()
    {
        if (lstSuppliers.ItemsPanelRoot is WrapPanel panel && lstSuppliers.Bounds.Width > 0)
            panel.ItemWidth = Math.Max(60, Math.Floor((lstSuppliers.Bounds.Width - 4) / 2));
    }

    private void ShowOptions(AdvancedSearchOptions o)
    {
        // Search for was left blank last time (the description words were the search): leave it blank again.
        txtQuery.Text = o.ContainsIsQuery ? "" : o.Query;
        cboMode.SelectedIndex = (int)o.Mode;
        chkInStock.IsChecked = o.InStockOnly;
        chkRohs.IsChecked = o.RohsOnly;
        txtMaker.Text = o.ManufacturerContains;
        txtPartNo.Text = o.PartNoContains;
        txtContains.Text = o.MustContain;
        nudQty.Value = Math.Max(0, Math.Min(1000000, o.Quantity));
        txtMaxPrice.Text = o.MaxUnitPrice.HasValue ? o.MaxUnitPrice.Value.ToString("0.00##", CultureInfo.CurrentCulture) : "";
        nudMax.Value = Math.Max(1, Math.Min(50, o.MaxResults));
        foreach (var s in _suppliers)
        {
            s.IsChecked = o.Suppliers.Count == 0 || o.Suppliers.Contains(s.Name);
        }
    }

    private async void BtnSearch_Click(object? sender, RoutedEventArgs e)
    {
        var query = (txtQuery.Text ?? "").Trim();
        var mode = (SearchMode)Math.Max(0, cboMode.SelectedIndex);
        var mustContain = (txtContains.Text ?? "").Trim();
        var partNo = (txtPartNo.Text ?? "").Trim();
        var containsIsQuery = false;
        if (query.Length == 0)
        {
            // Nothing in Search for: the suppliers still need search text, so search for the part-number fragment
            // and description words together, e.g. "931G switch" (DigiKey and Mouser match part of a number).
            // The suppliers match the words their own way (DigiKey finds "20-Bit" as "20BIT"), so they aren't
            // also checked letter-for-letter - that would throw most of those parts away.
            query = string.Join(" ", new[] { partNo, mustContain }.Where(x => x.Length > 0));
            containsIsQuery = true;
            mode = SearchMode.Keyword;
        }
        if (query.Length < 2)
        {
            await MessageBox.Show(this, "Type at least 2 characters in Search for (or in Manufacturer part no. contains / Description contains).", Title ?? "");
            txtQuery.Focus();
            return;
        }

        decimal? maxPrice = null;
        var priceText = (txtMaxPrice.Text ?? "").Trim().TrimStart('£', '$', '€');
        if (priceText.Length > 0)
        {
            if (!decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.CurrentCulture, out var d) || d <= 0)
            {
                await MessageBox.Show(this, "Max price each should be a number, e.g. 0.50 - or leave it blank.", Title ?? "");
                txtMaxPrice.Focus();
                return;
            }
            maxPrice = d;
        }

        var chosen = _suppliers.Where(s => s.IsChecked).Select(s => s.Name).ToList();
        if (chosen.Count == 0)
        {
            await MessageBox.Show(this, "Tick at least one supplier to search.", Title ?? "");
            return;
        }

        Close(new AdvancedSearchOptions
        {
            Query = query,
            Mode = mode,
            MaxResults = (int)(nudMax.Value ?? 25),
            InStockOnly = chkInStock.IsChecked == true,
            RohsOnly = chkRohs.IsChecked == true,
            ManufacturerContains = (txtMaker.Text ?? "").Trim(),
            PartNoContains = partNo,
            MustContain = mustContain,
            ContainsIsQuery = containsIsQuery,
            Quantity = (int)(nudQty.Value ?? 0),
            MaxUnitPrice = maxPrice,
            Suppliers = chosen
        });
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void BtnClear_Click(object? sender, RoutedEventArgs e)
    {
        ShowOptions(new AdvancedSearchOptions { Query = txtQuery.Text ?? "", MaxResults = (int)(nudMax.Value ?? 25) });
    }
}
