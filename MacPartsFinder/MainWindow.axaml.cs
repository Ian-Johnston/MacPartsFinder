// MacPartsFinder - Mac version of WinPartsFinder by Ian Johnston

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MacPartsFinder;

public partial class MainWindow : Window
{
    // ==== THE version number - change it here for each release (to match the vX.Y.Z tag Release.bat makes) ====
    public const string AppVersion = "V1.0.0";
    private const string TitleSuffix = "(Free for Non-Commercial Use • Support MacPartsFinder - see About)";

    private AppSettings _settings;
    private List<Supplier> _suppliers = new();
    private readonly ObservableCollection<SupplierRow> _supplierRows = new();
    private readonly ObservableCollection<ResultRow> _rows = new();
    private readonly DataGridCollectionView _view;
    private CancellationTokenSource? _cts;
    private bool _loading;

    private readonly ImageCache _images = new();
    // First photo seen for each manufacturer part no., so suppliers without photos (Mouser) can borrow one.
    private readonly Dictionary<string, (string Thumb, string Large)> _imagesByMpn = new();

    private const double ThumbRowHeight = ImageCache.ThumbSize + 4;
    private const double PlainRowHeight = 22;

    // Default layout, for Settings > Reset window layout.
    private readonly Dictionary<string, ColumnLayout> _defaultColumns;

    // Normal (not maximised) position and size, kept up to date for saving.
    private PixelPoint _normalPosition;
    private Size _normalSize;

    #region Startup / shutdown

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        Title = $"MacPartsFinder - {AppVersion}      {TitleSuffix}";
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = _settings.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;

        cboMode.ItemsSource = new[] { "Keyword", "Manufacturer part no." };
        cboMode.SelectedIndex = 0;
        nudMax.Value = Math.Max(1, Math.Min(50, _settings.MaxResults));
        _loading = true;
        chkInStock.IsChecked = _settings.InStockOnly;
        _loading = false;
        _settingConvert = true; // (no fetching while the window is still being set up)
        chkConvert.IsChecked = _settings.ConvertPrices;
        _settingConvert = false;
        UpdateConvertText();

        // Filtered (hidden rows stay in _rows) and in arrival order until the user sorts by a column.
        _view = new DataGridCollectionView(_rows) { Filter = o => RowVisible((ResultRow)o) };
        _view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(ResultRow.Order)));
        gridResults.ItemsSource = _view;
        gridSuppliers.ItemsSource = _supplierRows;

        // Row lines under the real columns only (see the "gl" style) - not on across the empty space to the right.
        foreach (var col in gridResults.Columns)
        {
            col.CellStyleClasses.Add("gl");
        }
        AddColumnHeaderTips();
        _defaultColumns = CurrentColumnLayout();
        ApplyColumnLayout(_settings.ResultColumns);
        ApplyThumbnailSetting();
        _images.ThumbnailReady += Images_ThumbnailReady;
        RestoreWindowPlacement();

        RebuildSuppliers();
        ApplyFilter();
        SetUpRecentSearches();

        // Right-click selects the row under the mouse first (for the menus), as in WinPartsFinder.
        gridResults.AddHandler(PointerPressedEvent, GridResults_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        gridSuppliers.AddHandler(PointerPressedEvent, GridSuppliers_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // ⌘F (Ctrl+F on Windows) opens Advanced search.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && IsCommandKey(e.KeyModifiers))
            {
                e.Handled = true;
                BtnAdvanced_Click(this, new RoutedEventArgs());
            }
        }, RoutingStrategies.Tunnel);

        if (!_suppliers.Any(s => s.IsConfigured))
            SetStatus("No API keys set yet - open Settings to add Mouser / DigiKey / element14 / TME keys. Until then you get website links.");
        else
            SetStatus("Ready.");

        Opened += async (_, _) =>
        {
            _normalPosition = Position;
            _normalSize = ClientSize;
            cboSearch.Focus();
            // Prices in one currency: fetch new exchange rates in the background if the saved ones are 12+ hours old.
            if (chkConvert.IsChecked == true) _ = RefreshRatesAsync(showDone: false);
            if (_settings.LoadError.Length > 0) await MessageBox.Show(this, _settings.LoadError, Title ?? "");
        };
        PositionChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _normalPosition = Position;
            HideSuggestions();
        };
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _normalSize = ClientSize;
            HideSuggestions();
        };
        Deactivated += (_, _) => HideSuggestions();
        Closing += (_, _) =>
        {
            _cts?.Cancel();
            SaveUiSettings(); // (closing - nowhere to show an error)
            _help?.Close();
            _photoPopup?.Close();
        };
    }

    private void RestoreWindowPlacement()
    {
        var p = _settings.MainWindow;
        if (p == null || p.Width < MinWidth || p.Height < MinHeight) return;
        // Ignore it if that spot is no longer on any screen (e.g. a monitor was unplugged).
        var visible = Screens.All.Any(s =>
        {
            var bounds = new PixelRect(p.X, p.Y, (int)(p.Width * s.Scaling), (int)(p.Height * s.Scaling));
            var overlap = s.WorkingArea.Intersect(bounds);
            return overlap.Width >= 200 && overlap.Height >= 100;
        });
        if (!visible) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(p.X, p.Y);
        Width = p.Width;
        Height = p.Height;
        if (p.Maximized) WindowState = WindowState.Maximized;
        if (p.SplitterDistance > 0)
        {
            gridMain.ColumnDefinitions[0].Width = new GridLength(Math.Max(150, Math.Min(p.SplitterDistance, p.Width - 200)));
        }
    }

    private WindowPlacement CurrentWindowPlacement()
    {
        return new WindowPlacement
        {
            X = _normalPosition.X,
            Y = _normalPosition.Y,
            Width = (int)_normalSize.Width,
            Height = (int)_normalSize.Height,
            Maximized = WindowState == WindowState.Maximized,
            SplitterDistance = (int)gridMain.ColumnDefinitions[0].ActualWidth
        };
    }

    private void ResetWindowLayout()
    {
        WindowState = WindowState.Normal;
        Width = 1264;
        Height = 681;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            Position = new PixelPoint(area.X + (area.Width - (int)(Width * screen.Scaling)) / 2,
                                      area.Y + (area.Height - (int)(Height * screen.Scaling)) / 2);
        }
        gridMain.ColumnDefinitions[0].Width = new GridLength(440);
        ApplyColumnLayout(_defaultColumns);
    }

    /// <summary>Saves the settings, with the window layout and supplier ticks. Returns an error message, or "" if all went well.</summary>
    private string SaveUiSettings()
    {
        _settings.MainWindow = CurrentWindowPlacement();
        _settings.ResultColumns = CurrentColumnLayout();
        _settings.MaxResults = MaxPerSupplier;
        _settings.InStockOnly = chkInStock.IsChecked == true;
        _settings.ConvertPrices = chkConvert.IsChecked == true;
        foreach (var row in _supplierRows)
        {
            _settings.EnabledSuppliers[row.Name] = row.IsChecked;
        }
        try
        {
            _settings.Save();
            return "";
        }
        catch (Exception ex)
        {
            return "Could not save settings: " + ex.Message;
        }
    }

    /// <summary>⌘ on a Mac, Ctrl on Windows.</summary>
    private static bool IsCommandKey(KeyModifiers m) =>
        m == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    private int MaxPerSupplier => (int)(nudMax.Value ?? 25);

    /// <summary>
    /// (Re)creates the suppliers from the settings. After the keys window the current search's
    /// Status / Hits / Time and Load more position are carried over by supplier name.
    /// </summary>
    private void RebuildSuppliers()
    {
        var previous = new Dictionary<string, (string Status, string Time, string Tip, PagingState? Paging)>();
        foreach (var old in _supplierRows)
        {
            _paging.TryGetValue(old.Supplier, out var st);
            previous[old.Name] = (old.Status, old.Time, old.Tip, st);
            old.PropertyChanged -= SupplierRow_PropertyChanged;
        }
        _paging.Clear();

        _loading = true;
        _suppliers = SupplierFactory.Create(_settings);
        _supplierRows.Clear();
        foreach (var s in _suppliers)
        {
            var item = new SupplierRow(s, _settings.IsEnabled(s.Name))
            {
                Tip = s.IsConfigured
                    ? $"{s.Name}: searched via API."
                    : $"{s.Name}: no API search - a website search link will be added to the results."
            };
            if (previous.TryGetValue(s.Name, out var prev))
            {
                item.Status = prev.Status;
                item.Time = prev.Time;
                if (prev.Paging != null)
                {
                    // Still searchable (key not removed): keep Load more's place and the tooltip.
                    if (s.IsConfigured)
                    {
                        _paging[s] = prev.Paging;
                        item.Tip = prev.Tip;
                    }
                    item.Hits = HitsText(s, prev.Paging);
                }
                else if (prev.Status.Length > 0)
                {
                    item.Tip = prev.Tip; // e.g. an error message
                }
            }
            item.PropertyChanged += SupplierRow_PropertyChanged;
            _supplierRows.Add(item);
        }
        _loading = false;
        UpdateLoadMoreButton();
    }

    #endregion

    #region Recent searches

    // The Search box is an editable drop-down: type as normal (earlier searches that start the same way are
    // suggested underneath), or click its arrow (or ⌥↓) to pick one of the last 30. Picking one only fills
    // the box - Return or Search runs it.
    private const int RecentShown = 30;
    private bool _fillingSearchBox;
    private bool _listWasOpen;

    private string SearchText => (cboSearch.Text ?? "").Trim();

    private void SetUpRecentSearches()
    {
        popSuggest.PlacementTarget = cboSearch;
        RefreshRecentList();
        cboSearch.AddHandler(KeyDownEvent, SearchBox_KeyDown, RoutingStrategies.Tunnel);
        cboSearch.AddHandler(KeyDownEvent, SearchBox_KeyDownAfter, RoutingStrategies.Bubble, handledEventsToo: true);
        cboSearch.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty) SearchText_Changed();
            else if (e.Property == ComboBox.IsDropDownOpenProperty && cboSearch.IsDropDownOpen) HideSuggestions();
        };
        cboSearch.LostFocus += (_, _) =>
        {
            // Clicking a suggestion must not close the list before the click lands.
            if (SuggestionsVisible && lstSuggest.IsPointerOver) return;
            HideSuggestions();
        };
    }

    /// <summary>Fills the drop-down with the last 30 searches (after each search), keeping what's in the box.</summary>
    private void RefreshRecentList()
    {
        var typed = cboSearch.Text; // refilling the list can clear the text
        _fillingSearchBox = true;
        cboSearch.ItemsSource = _settings.RecentSearches.Take(RecentShown).ToList();
        cboSearch.SelectedItem = null;
        cboSearch.Text = typed;
        _fillingSearchBox = false;
    }

    /// <summary>Puts text in the Search box without suggestions popping up (it doesn't search).</summary>
    private void SetSearchText(string text)
    {
        _fillingSearchBox = true;
        cboSearch.Text = text;
        _fillingSearchBox = false;
        HideSuggestions();
        MoveCaretToEnd();
    }

    private void MoveCaretToEnd()
    {
        if (cboSearch.FindDescendantOfType<TextBox>() is { } box)
            box.SelectionStart = box.SelectionEnd = box.CaretIndex = (box.Text ?? "").Length;
    }

    /// <summary>Typing: earlier searches that start the same way are suggested underneath.</summary>
    private void SearchText_Changed()
    {
        if (_fillingSearchBox || cboSearch.IsDropDownOpen) return;
        // (Picking from the drop-down changes the text too - that isn't typing.)
        if (cboSearch.SelectedItem is string picked && picked == cboSearch.Text)
        {
            HideSuggestions();
            return;
        }
        var typed = SearchText;
        var matches = typed.Length == 0
            ? new List<string>()
            : _settings.RecentSearches.Where(r => r.StartsWith(typed, StringComparison.OrdinalIgnoreCase) &&
                                                  !r.Equals(typed, StringComparison.OrdinalIgnoreCase))
                                      .Take(RecentShown).ToList();
        if (matches.Count > 0 && !_busy && cboSearch.IsKeyboardFocusWithin) ShowSuggestions(matches); else HideSuggestions();
    }

    private bool SuggestionsVisible => popSuggest.IsOpen;

    private void ShowSuggestions(List<string> items)
    {
        lstSuggest.ItemsSource = items;
        lstSuggest.SelectedIndex = -1;
        lstSuggest.MinWidth = cboSearch.Bounds.Width;
        popSuggest.IsOpen = true;
    }

    private void HideSuggestions()
    {
        if (SuggestionsVisible) popSuggest.IsOpen = false;
    }

    private void LstSuggest_PointerMoved(object? sender, PointerEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item)
            lstSuggest.SelectedItem = item.DataContext;
    }

    private void LstSuggest_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: string picked })
        {
            SetSearchText(picked);
            cboSearch.Focus();
        }
    }

    /// <summary>Keys in the Search box, before the box itself handles them.</summary>
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        _listWasOpen = cboSearch.IsDropDownOpen;
        // ⌥↓ (Alt+↓ on Windows) opens and closes the last 30 searches.
        if ((e.Key == Key.Down || e.Key == Key.Up) && e.KeyModifiers == KeyModifiers.Alt)
        {
            HideSuggestions();
            cboSearch.IsDropDownOpen = !cboSearch.IsDropDownOpen;
            e.Handled = true;
            return;
        }
        // Up / Down / Return / Esc in the suggestions shown while typing.
        if (e.KeyModifiers != KeyModifiers.None || !SuggestionsVisible) return;
        var count = lstSuggest.ItemCount;
        switch (e.Key)
        {
            case Key.Down:
                lstSuggest.SelectedIndex = Math.Min(count - 1, lstSuggest.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up:
                lstSuggest.SelectedIndex = Math.Max(-1, lstSuggest.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (lstSuggest.SelectedItem is string picked)
                {
                    SetSearchText(picked);
                    e.Handled = true;
                }
                else
                {
                    HideSuggestions(); // nothing picked: Return searches as normal
                }
                break;
            case Key.Escape:
                HideSuggestions();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Return in the open list of last 30 just picks that search (it would otherwise also press Search).</summary>
    private void SearchBox_KeyDownAfter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_listWasOpen) return;
        _listWasOpen = false;
        cboSearch.IsDropDownOpen = false;
        MoveCaretToEnd();
        e.Handled = true;
    }

    #endregion

    #region Results list

    private static readonly Dictionary<string, string> HeaderTips = new()
    {
        { "No", "Part number in this list - stays with the part until the next search, even if rows are removed or sorted.\nWebsite-link rows have no number, so the last # matches Total parts." },
        { "Supplier", "Who sells it. Blue italic rows are website links for suppliers without an API." },
        { "SupplierPartNo", "Supplier's own order code - click to open the product page." },
        { "MfrPartNo", "Manufacturer part number." },
        { "Manufacturer", "Who makes the part." },
        { "Type", "Kind of part, in the supplier's own words (its category).\nBlank for website links, and for the odd part a supplier hasn't categorised." },
        { "Description", "Supplier's description of the part." },
        { "Stock", "Quantity in stock now. Green = in stock, grey row = none." },
        { "UnitPrice", "Price each at the lowest price break (usually qty 1)." },
        { "Currency", "Currency of the prices in this row." },
        { "PriceBreaks", "Quantity+ unit price, for each price break." },
        { "MOQ", "Minimum order quantity." },
        { "Availability", "Stock wording, packaging and lead time from the supplier." },
        { "Status", "Lifecycle status, e.g. Active, Obsolete, Not recommended for new designs." },
        { "Datasheet", "Click to open the datasheet. Blank when the supplier didn't give one (TME and website links never do)." }
    };

    /// <summary>Header tooltips (click a header to sort; drag to reorder; drag the edge to resize).</summary>
    private void AddColumnHeaderTips()
    {
        foreach (var col in gridResults.Columns)
        {
            if (col.Tag is not string name || !HeaderTips.TryGetValue(name, out var tip)) continue;
            // (A Header of "#" doesn't survive the XAML, so the # column's is set here.)
            var header = new TextBlock { Text = name == "No" ? "#" : col.Header as string ?? "" };
            ToolTip.SetTip(header, tip + "\nClick to sort.");
            col.Header = header;
        }
    }

    private static string ColumnName(DataGridColumn c) => c.Tag as string ?? "";

    private static string HeaderText(DataGridColumn c) => c.Header as string ?? (c.Header as TextBlock)?.Text ?? "";

    private DataGridColumn Column(string name) => gridResults.Columns.First(c => ColumnName(c) == name);

    private Dictionary<string, ColumnLayout> CurrentColumnLayout()
    {
        return gridResults.Columns.ToDictionary(
            ColumnName,
            c => new ColumnLayout { Width = (int)(c.ActualWidth > 0 ? c.ActualWidth : c.Width.Value), DisplayIndex = c.DisplayIndex });
    }

    /// <summary>Restores widths and (drag-and-drop) column order saved by the user.</summary>
    private void ApplyColumnLayout(Dictionary<string, ColumnLayout>? layout)
    {
        if (layout == null || layout.Count == 0) return;
        var byName = gridResults.Columns.ToDictionary(ColumnName);
        foreach (var kv in layout)
        {
            if (!byName.TryGetValue(kv.Key, out var col)) continue; // column renamed/removed in a later version
            if (col.CanUserResize) col.Width = new DataGridLength(Math.Max(col.MinWidth, kv.Value.Width));
        }
        // The # column always stays first. The rest go after it in their saved order, set lowest first
        // so each move doesn't disturb the ones already placed.
        var position = 1;
        foreach (var kv in layout.Where(x => x.Key != "No").OrderBy(x => x.Value.DisplayIndex))
        {
            if (!byName.TryGetValue(kv.Key, out var col)) continue;
            col.DisplayIndex = Math.Min(position, gridResults.Columns.Count - 1);
            position++;
        }
        // Columns added in a later version than the saved layout go just after the column they follow by default.
        for (var i = 1; i < gridResults.Columns.Count; i++)
        {
            var col = gridResults.Columns[i];
            if (!layout.ContainsKey(ColumnName(col)))
                col.DisplayIndex = Math.Min(gridResults.Columns[i - 1].DisplayIndex + 1, gridResults.Columns.Count - 1);
        }
        Column("No").DisplayIndex = 0;
    }

    private void ApplyThumbnailSetting()
    {
        var show = _settings.ShowThumbnails;
        Column("Thumb").IsVisible = show;
        gridResults.RowHeight = show ? ThumbRowHeight : PlainRowHeight;
    }

    private static string MpnKey(string? mpn) => (mpn ?? "").Trim().ToUpperInvariant();

    /// <summary>The row's own photo, or one borrowed from another supplier with the same MPN.</summary>
    private (string Thumb, string Large) ImageUrlsFor(ResultRow row)
    {
        var thumb = row.ImageUrl;
        if (thumb.Length == 0)
        {
            if (row.LinkOnly) return ("", "");
            return _imagesByMpn.TryGetValue(MpnKey(row.MfrPartNo), out var borrowed) ? borrowed : ("", "");
        }
        var large = row.LargeImageUrl;
        return (thumb, large.Length > 0 ? large : thumb);
    }

    /// <summary>Shows the row's photo, starting its download if need be. Only called for rows on screen.</summary>
    private void EnsureThumb(ResultRow row)
    {
        var url = ImageUrlsFor(row).Thumb;
        row.ThumbFor = url;
        row.Thumb = _images.GetThumbnail(url, out var failed);
        row.NoPhoto = failed;
    }

    private void Images_ThumbnailReady(string url)
    {
        foreach (var r in _rows.Where(r => r.ThumbFor == url))
        {
            EnsureThumb(r);
        }
    }

    /// <summary>Rows already shown may be able to borrow a photo from rows that just arrived.</summary>
    private void RefreshThumbs()
    {
        if (!_settings.ShowThumbnails) return;
        foreach (var r in _rows.Where(r => r.ThumbFor != null && r.Thumb == null))
        {
            EnsureThumb(r);
        }
    }

    private ResultRow AddResult(PartResult p, int seq)
    {
        var row = new ResultRow
        {
            Supplier = p.Supplier,
            MfrPartNo = p.ManufacturerPartNumber,
            Manufacturer = p.Manufacturer,
            Description = p.Description,
            Type = p.Category,
            Stock = p.Stock,
            UnitPrice = p.UnitPrice,
            Currency = p.Currency,
            PriceBreaks = p.PriceBreaksText(),
            // "Prices in GBP": the columns above show converted prices; the supplier's own are kept here.
            OrigUnitPrice = p.UnitPrice,
            OrigCurrency = p.Currency,
            OrigBreaks = p.PriceBreaksText(),
            BreaksRaw = string.Join(";", p.PriceBreaks.OrderBy(b => b.Quantity)
                .Select(b => b.Quantity.ToString(CultureInfo.InvariantCulture) + ":" + b.Price.ToString(CultureInfo.InvariantCulture))),
            Moq = p.MinOrderQty,
            Availability = p.StockInfo,
            SupplierPartNo = p.SupplierPartNumber,
            Status = p.Status,
            ProductUrl = p.ProductUrl,
            DatasheetUrl = p.DatasheetUrl,
            ImageUrl = p.ImageUrl,
            LargeImageUrl = p.LargeImageUrl,
            LinkOnly = p.IsLinkOnly,
            Seq = seq,
            Order = _batch * OrderPerBatch + seq
            // No - given when the search / Load more finishes
        };
        ConvertRow(row);
        _rows.Add(row);

        var key = MpnKey(p.ManufacturerPartNumber);
        if (p.ImageUrl.Length > 0 && key.Length > 0 && !_imagesByMpn.ContainsKey(key))
        {
            _imagesByMpn[key] = (p.ImageUrl, p.LargeImageUrl.Length > 0 ? p.LargeImageUrl : p.ImageUrl);
        }
        return row;
    }

    private int _nextRowNo = 1;
    // 0 = the search itself, 1 = first Load more, ... Each batch's rows sort after the previous batch's.
    private int _batch;
    private const long OrderPerBatch = 10_000_000L;

    /// <summary>Numbers any parts that don't have a # yet, in the default (arrival) order.
    /// Website-link rows aren't parts, so they get no number - the last # then always equals "Total parts".</summary>
    private void NumberNewRows()
    {
        foreach (var r in _rows.Where(r => r.No == null && !r.LinkOnly).OrderBy(r => r.Order).ToList())
        {
            r.No = _nextRowNo++;
        }
        // Re-apply the column sort: if the list is sorted by #, the new numbers have to be put in order too.
        _view.Refresh();
    }

    private const int SeqPerSupplier = 100000;

    private int SeqFor(Supplier s, int rowNumber)
    {
        return _suppliers.IndexOf(s) * SeqPerSupplier + rowNumber;
    }

    /// <summary>Whether a row passes the supplier ticks, In stock only, hidden Types and the Filter box.
    /// Website-link rows always stay (unless their supplier is unticked or they were removed).</summary>
    private bool RowVisible(ResultRow r)
    {
        if (r.Removed) return false;
        // Unticked suppliers' rows are hidden, not deleted - tick them again to bring them back.
        if (_supplierRows.Any(i => !i.IsChecked && i.Name == r.Supplier)) return false;
        if (r.LinkOnly) return true;
        if (chkInStock.IsChecked == true && !(r.Stock > 0)) return false;
        if (_hiddenTypes.Count > 0 && _hiddenTypes.Contains(r.Type.Trim())) return false;
        var f = (txtFilter.Text ?? "").Trim();
        if (f.Length > 0)
        {
            return new[] { r.MfrPartNo, r.Description, r.Type, r.Manufacturer, r.Supplier, r.SupplierPartNo }
                .Any(v => v.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        return true;
    }

    private void ApplyFilter()
    {
        _view.Refresh();
        UpdateCountStatus();
    }

    /// <summary>Types… needs some parts to list, and shows how many types are hidden.</summary>
    private void UpdateTypesButton()
    {
        btnTypes.IsEnabled = !_busy && _rows.Any(r => !r.LinkOnly);
        btnTypes.Content = _hiddenTypes.Count > 0 ? $"Types ({_hiddenTypes.Count} hidden)…" : "Types…";
    }

    /// <summary>
    /// Lists every part type in the current results with tick boxes; unticked types are hidden from the grid
    /// (not deleted) until the next search. Modal, so nothing else can happen until OK or Cancel.
    /// </summary>
    private async void BtnTypes_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var types = _rows.Where(r => !r.LinkOnly && !r.Removed)
            .GroupBy(r => r.Type.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TypeEntry
            {
                Name = g.Key,
                Count = g.Count(),
                Suppliers = g.Select(r => r.Supplier).Distinct().ToList()
            })
            .OrderBy(t => t.Name.Length == 0).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (types.Count == 0) return;

        var hidden = await new TypesWindow(types, _hiddenTypes).ShowDialog<HashSet<string>?>(this);
        if (hidden == null) return;
        _hiddenTypes.Clear();
        _hiddenTypes.UnionWith(hidden);
        ApplyFilter();
    }

    private void UpdateCountStatus()
    {
        UpdateTypesButton();
        UpdateTotalParts();
        var shown = _view.Count;
        var total = _rows.Count;
        if (total == 0) return;
        var removed = RemovedCount();
        var hidden = HiddenNote();
        SetStatus($"{shown} of {total} results shown." +
                  (removed > 0 ? $" ({removed} removed - right-click > Restore removed rows to bring them back)" : "") +
                  (hidden.Length > 0 ? " " + hidden : ""));
    }

    private int ShowingParts() => _view.Cast<ResultRow>().Count(r => !r.LinkOnly);

    /// <summary>"Total parts = 140", plus "(showing 98)" when Filter / In stock only / Remove hide some. Website-link rows don't count.</summary>
    private void UpdateTotalParts()
    {
        var parts = _rows.Count(r => !r.LinkOnly);
        var showing = ShowingParts();
        lblTotal.Text = $"Total parts = {parts:#,##0}" + (showing < parts ? $"  (showing {showing:#,##0})" : "");
    }

    private int RemovedCount() => _rows.Count(r => r.Removed);

    /// <summary>Hides the selected rows until the next search (or Restore).</summary>
    private void RemoveSelectedRows()
    {
        var rows = gridResults.SelectedItems.OfType<ResultRow>().ToList();
        if (rows.Count == 0) return;
        foreach (var r in rows)
        {
            r.Removed = true;
        }
        ApplyFilter();
    }

    private void RestoreRemovedRows()
    {
        foreach (var r in _rows)
        {
            r.Removed = false;
        }
        ApplyFilter();
    }

    private static string Field(ResultRow? r, string name)
    {
        if (r == null) return "";
        object? v = name switch
        {
            "No" => r.No,
            "Supplier" => r.Supplier,
            "SupplierPartNo" => r.SupplierPartNo,
            "MfrPartNo" => r.MfrPartNo,
            "Manufacturer" => r.Manufacturer,
            "Type" => r.Type,
            "Description" => r.Description,
            "Stock" => r.Stock,
            "UnitPrice" => r.UnitPrice,
            "Currency" => r.Currency,
            "PriceBreaks" => r.PriceBreaks,
            "MOQ" => r.Moq,
            "Availability" => r.Availability,
            "Datasheet" or "DatasheetUrl" => r.DatasheetUrl,
            "Status" => r.Status,
            "ProductUrl" => r.ProductUrl,
            "OrigUnitPrice" => r.OrigUnitPrice,
            "OrigCurrency" => r.OrigCurrency,
            "OrigBreaks" => r.OrigBreaks,
            _ => null
        };
        return Convert.ToString(v) ?? "";
    }

    private void GridResults_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        // Rows are reused while scrolling, so set every class each time.
        if (e.Row.DataContext is not ResultRow r) return;
        e.Row.Classes.Set("link", r.LinkOnly);
        e.Row.Classes.Set("nostock", !r.LinkOnly && r.Stock == 0);
        e.Row.Classes.Set("instock", !r.LinkOnly && r.Stock != 0);
        e.Row.Classes.Set("conv", !r.LinkOnly && r.Converted); // converted prices in purple
        // Photos are only fetched for rows that come into view.
        if (_settings.ShowThumbnails) EnsureThumb(r);
    }

    private void GridResults_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // The photo and link cells already act on a single click; headers aren't rows.
        if (e.Source is not Control c || c.FindAncestorOfType<HyperlinkButton>(includeSelf: true) != null) return;
        if (c.GetSelfAndVisualAncestors().OfType<Control>().Any(a => a.Classes.Contains("thumb"))) return;
        if (c.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is ResultRow r) OpenUrl(r.ProductUrl);
    }

    private void GridResults_KeyDown(object? sender, KeyEventArgs e)
    {
        // Delete (or the Mac's delete key, which is Backspace) removes the selected rows.
        if ((e.Key == Key.Delete || e.Key == Key.Back) && e.KeyModifiers == KeyModifiers.None)
        {
            RemoveSelectedRows();
            e.Handled = true;
        }
        else if (e.Key == Key.A && IsCommandKey(e.KeyModifiers))
        {
            gridResults.SelectAll();
            e.Handled = true;
        }
    }

    private void ProductLink_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ResultRow r) OpenUrl(r.ProductUrl);
    }

    private void DatasheetLink_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ResultRow r && r.DatasheetUrl.Length > 0) OpenUrl(r.DatasheetUrl);
    }

    private void ChkInStock_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_loading) ApplyFilter();
    }

    private void TxtFilter_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    // ---- Right-click menu ----

    private ResultRow? _menuRow;
    private bool _menuOnHeader;

    private void GridResults_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(gridResults).Properties.IsRightButtonPressed) return;
        var c = e.Source as Control;
        // The part menu is for rows - not for a right-click on the column headers.
        _menuOnHeader = c?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) != null;
        _menuRow = c?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as ResultRow;
        // Select the row under the mouse - unless it's already part of a multi-row selection, which is kept (for Remove).
        if (_menuRow != null && !gridResults.SelectedItems.Contains(_menuRow)) gridResults.SelectedItem = _menuRow;
    }

    private void CmsResults_Opening(object? sender, CancelEventArgs e)
    {
        var row = _menuRow ?? gridResults.SelectedItem as ResultRow;
        var removed = RemovedCount();
        // With every row removed there's nothing to click on, but Restore must still be reachable.
        if (_menuOnHeader || (row == null && removed == 0))
        {
            e.Cancel = true;
            return;
        }
        var selectedCount = gridResults.SelectedItems.Count;
        mnuOpenProduct.IsEnabled = Field(row, "ProductUrl").Length > 0;
        mnuOpenDatasheet.IsEnabled = Field(row, "DatasheetUrl").Length > 0;
        mnuCopyMpn.IsEnabled = Field(row, "MfrPartNo").Length > 0;
        mnuCopySupplierPart.IsEnabled = row != null && row.SupplierPartNo.Length > 0 && !row.LinkOnly;
        mnuCopyRow.IsEnabled = selectedCount > 0;
        mnuCopyRow.Header = selectedCount > 1 ? $"Copy {selectedCount} rows" : "Copy row";
        mnuRemoveRows.IsEnabled = selectedCount > 0;
        mnuRemoveRows.Header = selectedCount > 1 ? $"Remove {selectedCount} rows from results" : "Remove from results";
        mnuRestoreRows.IsEnabled = removed > 0;
        mnuRestoreRows.Header = removed > 0 ? $"Restore removed rows ({removed})" : "Restore removed rows";
    }

    private ResultRow? MenuRow => _menuRow ?? gridResults.SelectedItem as ResultRow;

    private void MnuOpenProduct_Click(object? sender, RoutedEventArgs e) => OpenUrl(Field(MenuRow, "ProductUrl"));

    private void MnuOpenDatasheet_Click(object? sender, RoutedEventArgs e)
    {
        var url = Field(MenuRow, "DatasheetUrl");
        if (url.StartsWith("//")) url = "https:" + url;
        OpenUrl(url);
    }

    private void MnuCopyMpn_Click(object? sender, RoutedEventArgs e) => CopyText(Field(MenuRow, "MfrPartNo"));

    private void MnuCopySupplierPart_Click(object? sender, RoutedEventArgs e) => CopyText(Field(MenuRow, "SupplierPartNo"));

    private void MnuCopyRow_Click(object? sender, RoutedEventArgs e)
    {
        // One tab-separated line per selected row, in grid order - pastes straight into Excel or Numbers.
        var cols = VisibleColumns();
        var selected = gridResults.SelectedItems.OfType<ResultRow>().ToHashSet();
        var lines = _view.Cast<ResultRow>().Where(selected.Contains)
            .Select(r => string.Join("\t", cols.Select(c => Field(r, ColumnName(c)))));
        CopyText(string.Join(Environment.NewLine, lines));
    }

    private void MnuRemoveRows_Click(object? sender, RoutedEventArgs e) => RemoveSelectedRows();

    private void MnuRestoreRows_Click(object? sender, RoutedEventArgs e) => RestoreRemovedRows();

    private async void CopyText(string text)
    {
        if (text.Length == 0 || Clipboard == null) return;
        await Clipboard.SetTextAsync(text);
    }

    #endregion

    #region Searching

    private async void BtnSearch_Click(object? sender, RoutedEventArgs e)
    {
        // A normal search: just the toolbar settings, no Advanced filters.
        await RunSearchAsync(new AdvancedSearchOptions
        {
            Query = SearchText,
            Mode = cboMode.SelectedIndex == 1 ? SearchMode.PartNumber : SearchMode.Keyword,
            MaxResults = MaxPerSupplier
        });
    }

    /// <summary>Runs a search (normal or Advanced) across the ticked suppliers.</summary>
    /// <param name="fromAdvanced">True when it came from the Advanced search window, so the status line lists its options.</param>
    private async Task RunSearchAsync(AdvancedSearchOptions opts, bool fromAdvanced = false)
    {
        var query = opts.Query;
        if (query.Length == 0)
        {
            cboSearch.Focus();
            return;
        }
        if (_busy) return;
        var selected = _supplierRows.Where(i => i.IsChecked).ToList();
        if (selected.Count == 0)
        {
            await MessageBox.Show(this, "Tick at least one supplier on the left.", Title ?? "");
            return;
        }

        _settings.AddRecentSearch(query);
        RefreshRecentList();
        _rows.Clear();
        _paging.Clear();
        _searched.Clear();
        _nextRowNo = 1;
        _batch = 0;
        UpdateTotalParts();
        foreach (var item in _supplierRows)
        {
            item.Status = item.IsChecked ? "Waiting…" : "";
            item.Hits = "";
            item.Time = "";
        }

        _active = opts;
        _advancedHidden = 0;
        _codeMpn = "";
        _hiddenTypes.Clear();
        ApplyFilter();       // supplier ticks may have just changed
        UpdatePriceHeader(); // "Price (25)" when a quantity is set

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var max = opts.MaxResults;
        var filters = opts.SupplierFilters();
        var sw = Stopwatch.StartNew();
        // A normal search's Manufacturer part no. mode isn't an "Advanced" option, so only describe Advanced searches.
        var advanced = fromAdvanced ? opts.Describe() : "";

        SetBusy(true);
        SetStatus($"Searching {selected.Count} supplier(s) for \"{query}\"" + (advanced.Length > 0 ? $" ({advanced})" : "") + "…");
        var codeNote = "";
        try
        {
            if (opts.Mode == SearchMode.SupplierCode)
                codeNote = await SearchByOrderCodeAsync(selected, query, max, filters, ct);
            else
                await Task.WhenAll(selected.Select(i => SearchOneAsync(i, query, opts.Mode, max, filters, ct)));
        }
        finally
        {
            SetBusy(false);
        }
        NumberNewRows();

        var apiHits = _rows.Count(r => !r.LinkOnly);
        var more = LoadMoreCandidates().Select(i => i.Name).ToList();
        var hidden = HiddenNote();
        SetStatus((codeNote.Length > 0 ? codeNote + " " : "") +
                  $"\"{query}\": {apiHits} part(s) found in {sw.Elapsed.TotalSeconds:0.0}s. " +
                  (advanced.Length > 0 ? $"Advanced: {advanced.TrimEnd('.')}" + (_advancedHidden > 0 ? $" ({_advancedHidden} left out)" : "") + ". " : "") +
                  (more.Count > 0 ? $"More available from {string.Join(", ", more)} - click Load more. " : "") +
                  (hidden.Length > 0 ? hidden : "Double-click a row to open it in your browser."));
    }

    /// <summary>
    /// When every part found is hidden (e.g. In stock only with nothing in stock), says what's hiding them,
    /// so an empty grid doesn't look like "no results". Empty when some parts are showing.
    /// </summary>
    private string HiddenNote()
    {
        var found = _rows.Count(r => !r.LinkOnly);
        var showing = ShowingParts();
        if (found == 0 || showing > 0) return "";
        var why = new List<string>();
        if (chkInStock.IsChecked == true) why.Add("In stock only (none are in stock)");
        if ((txtFilter.Text ?? "").Trim().Length > 0) why.Add("the Filter box");
        if (_hiddenTypes.Count > 0) why.Add("hidden Types");
        if (_supplierRows.Any(i => !i.IsChecked)) why.Add("unticked suppliers");
        if (RemovedCount() > 0) why.Add("removed rows");
        return $"All {found} are hidden by {string.Join(", ", why)} - change that to see them.";
    }

    /// <summary>
    /// Order-code search: look the code up at every API supplier (it belongs to one of them), take the
    /// manufacturer part number from the match, then search the other suppliers for that exact part.
    /// Returns a note for the status bar.
    /// </summary>
    private async Task<string> SearchByOrderCodeAsync(List<SupplierRow> selected, string code, int max,
                                                      SupplierFilters filters, CancellationToken ct)
    {
        var apiItems = selected.Where(i => i.Supplier.IsConfigured).ToList();
        await Task.WhenAll(apiItems.Select(i => SearchOneAsync(i, code, SearchMode.SupplierCode, max, filters, ct)));

        var hit = _rows.FirstOrDefault(r => !r.LinkOnly && r.MfrPartNo.Length > 0);
        if (hit == null)
        {
            // Nobody recognised it: give the website-link suppliers the code as it is.
            foreach (var i in selected.Except(apiItems))
            {
                await SearchOneAsync(i, code, SearchMode.SupplierCode, max, filters, ct);
            }
            return $"No supplier recognised order code \"{code}\".";
        }

        var mpn = hit.MfrPartNo;
        var foundAt = hit.Supplier;
        _codeMpn = mpn;
        // Everyone who didn't recognise the code (including website-link suppliers) gets the part number instead.
        var others = selected.Where(i => !_paging.TryGetValue(i.Supplier, out var st) || st.RowCount == 0).ToList();
        SetStatus($"Order code {code} is {mpn} at {foundAt} - now searching the other suppliers for {mpn}…");
        await Task.WhenAll(others.Select(i => SearchOneAsync(i, mpn, SearchMode.ExactPartNumber, max, filters, ct)));
        return $"Order code {code} is {mpn} ({foundAt}); the other suppliers were searched for {mpn}.";
    }

    /// <summary>The Price column shows the price at the Advanced search quantity, when one is set,
    /// and the "Prices in" currency when that's ticked, e.g. "Price (100) GBP".</summary>
    private void UpdatePriceHeader()
    {
        var qty = _active?.Quantity ?? 0;
        if (Column("UnitPrice").Header is not TextBlock header) return;
        header.Text = (qty > 0 ? $"Price ({qty:#,##0})" : "Price (1)") + (Converting ? " " + _settings.ConvertCurrency : "");
        ToolTip.SetTip(header, (qty > 0 ? $"Price each when buying {qty:#,##0} (from the price breaks)."
                                        : "Price each at the lowest price break (usually qty 1).") +
                               (Converting ? $"\nShown in {_settings.ConvertCurrency} (purple = converted, approximate)." : "") +
                               "\nClick to sort.");
    }

    #region Prices in one currency

    // "Prices in GBP" (toolbar): every price is shown in the currency chosen in Settings, using ECB exchange rates.
    // Rates are saved in settings.json and only fetched again after 12 hours (see ExchangeRates).
    private bool _settingConvert;   // true while the tick box is set from code (don't fetch)
    private bool _fetchingRates;

    /// <summary>True when prices are being shown converted (ticked, and rates are available).</summary>
    private bool Converting => chkConvert.IsChecked == true && _settings.Rates.HasRates;

    private void UpdateConvertText()
    {
        chkConvert.Content = $"Prices in {_settings.ConvertCurrency}";
    }

    private async void ChkConvert_Changed(object? sender, RoutedEventArgs e)
    {
        if (_settingConvert) return;
        _settings.ConvertPrices = chkConvert.IsChecked == true;
        if (chkConvert.IsChecked == true && _settings.Rates.NeedsRefresh)
            await RefreshRatesAsync(showDone: true);
        else
            ApplyConversion();
    }

    /// <summary>
    /// Fetches new exchange rates if the saved ones are 12+ hours old (or there are none), then shows the prices.
    /// If the rates can't be fetched, the last saved ones are used; with none at all, the tick is taken off.
    /// </summary>
    private async Task RefreshRatesAsync(bool showDone)
    {
        if (_fetchingRates) return;
        if (_settings.Rates.NeedsRefresh)
        {
            _fetchingRates = true;
            var oldStatus = lblStatus.Text ?? "";
            SetStatus("Getting today's exchange rates…");
            try
            {
                _settings.Rates = await ExchangeRates.DownloadAsync(CancellationToken.None);
                try
                {
                    _settings.Save(); // so a restart within 12 hours doesn't fetch again
                }
                catch
                {
                    // saved again on exit anyway
                }
            }
            catch (Exception ex)
            {
                if (!_settings.Rates.HasRates)
                {
                    _settingConvert = true;
                    chkConvert.IsChecked = false;
                    _settingConvert = false;
                    _settings.ConvertPrices = false;
                    SetStatus($"Prices in {_settings.ConvertCurrency} is off: {ex.Message} Check your internet connection and tick it again.");
                    ApplyConversion(showStatus: false);
                    return;
                }
                SetStatus($"Couldn't get new exchange rates ({ex.Message}) - using the saved {_settings.Rates.Describe()}.");
                showDone = false;
            }
            finally
            {
                _fetchingRates = false;
            }
            if (!showDone && (lblStatus.Text ?? "").StartsWith("Getting today", StringComparison.Ordinal)) SetStatus(oldStatus);
        }
        ApplyConversion(showStatus: showDone);
    }

    /// <summary>Shows every row's prices converted, or back as the supplier gave them, to match the tick box.</summary>
    private void ApplyConversion(bool showStatus = true)
    {
        foreach (var r in _rows)
        {
            ConvertRow(r);
        }
        UpdatePriceHeader();
        _view.Refresh(); // re-sorts by the new prices, and redraws the purple
        if (!showStatus) return;
        if (Converting)
        {
            var missing = _rows.Where(r => !r.LinkOnly && !r.Converted && r.OrigCurrency.Length > 0 &&
                                           !string.Equals(r.OrigCurrency, _settings.ConvertCurrency, StringComparison.OrdinalIgnoreCase))
                               .Select(r => r.OrigCurrency.ToUpperInvariant()).Distinct().ToList();
            SetStatus($"Prices shown in {_settings.ConvertCurrency} (purple = converted) at {_settings.Rates.Describe()}." +
                      (_settings.Rates.AgeDays > 4 ? $" These rates are {_settings.Rates.AgeDays} days old." : "") +
                      (missing.Count > 0 ? $" No rate for {string.Join(", ", missing)}, so those are shown as the supplier gave them." : "") +
                      " Converted prices are approximate.");
        }
        else
        {
            SetStatus("Prices shown as each supplier gave them.");
        }
    }

    /// <summary>Puts one row's converted prices (or the supplier's own) into the Price, Cur and Price breaks columns,
    /// with the tooltip for those cells.</summary>
    private void ConvertRow(ResultRow r)
    {
        if (r.LinkOnly) return;
        var origCur = r.OrigCurrency;
        var target = _settings.ConvertCurrency;
        decimal? rate = null;
        if (Converting && !string.Equals(origCur, target, StringComparison.OrdinalIgnoreCase))
        {
            rate = _settings.Rates.Rate(origCur, target);
        }
        if (rate.HasValue)
        {
            r.UnitPrice = r.OrigUnitPrice.HasValue ? Math.Round(r.OrigUnitPrice.Value * rate.Value, 4) : null;
            r.Currency = target;
            r.PriceBreaks = BreaksText(r.BreaksRaw, rate.Value);
            r.Converted = true;
            r.RateUsed = rate.Value;
            // Hover over a converted price: what the supplier actually charges, and the rate used.
            var orig = r.OrigUnitPrice.HasValue ? $"{r.OrigUnitPrice.Value:0.00##} {origCur}" : "";
            r.PriceTip = $"{r.Supplier} price: {orig}\nBreaks: {r.OrigBreaks}\n" +
                         $"Converted at 1 {origCur} = {rate.Value:0.#####} {target} ({_settings.Rates.Describe()}).\n" +
                         "Approximate - the supplier's own rate, VAT and import charges will differ.";
        }
        else
        {
            r.UnitPrice = r.OrigUnitPrice;
            r.Currency = origCur;
            r.PriceBreaks = r.OrigBreaks;
            r.Converted = false;
            r.RateUsed = null;
            r.PriceTip = Converting && origCur.Length > 0 && !string.Equals(origCur, target, StringComparison.OrdinalIgnoreCase)
                ? $"No exchange rate for {origCur}, so this price is shown as {r.Supplier} gave it."
                : null;
        }
    }

    /// <summary>"1+ 0.20   10+ 0.139" from "1:0.2;10:0.139", with every price multiplied by the rate.</summary>
    private static string BreaksText(string raw, decimal rate)
    {
        return string.Join("   ", raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var bits = pair.Split(':');
            var price = Math.Round(decimal.Parse(bits[1], CultureInfo.InvariantCulture) * rate, 4);
            return $"{bits[0]}+ {price:0.00##}";
        }));
    }

    /// <summary>A price in the "Prices in" currency when converting (for Advanced search's Max price each).</summary>
    private decimal PriceInShownCurrency(decimal price, string currency)
    {
        if (!Converting) return price;
        var rate = _settings.Rates.Rate(currency, _settings.ConvertCurrency);
        return rate.HasValue ? price * rate.Value : price;
    }

    #endregion

    /// <summary>Where a supplier's results got to, so Load more can fetch the next page.</summary>
    private sealed class PagingState
    {
        public string Query = "";
        public SearchMode Mode;
        public SupplierFilters? Filters; // in stock / RoHS, applied by the supplier
        public int Filtered;             // parts left out by Advanced search filters
        public int NextOffset;           // where the next page starts (in the supplier's own numbering)
        public int? Total;               // total matches the supplier reported
        public bool LastPageFull;        // fallback when no total is given
        public int RowCount;             // rows added to the grid (after removing duplicates)
        public int Duplicates;           // repeats the supplier sent that were skipped
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);

        public bool HasMore => Total.HasValue ? NextOffset < Total.Value : LastPageFull;
    }

    private readonly Dictionary<Supplier, PagingState> _paging = new();
    private bool _busy;

    // The current search (normal or Advanced), so a supplier ticked afterwards can be searched on its own,
    // and so Load more applies the same filters.
    private AdvancedSearchOptions? _active;
    private int _advancedHidden;   // parts left out by Advanced filters in this search
    private string _codeMpn = "";  // order-code search: the part number the code turned out to be
    // Part types hidden in the Types… window ("" = parts with no type). Cleared by every new search.
    private readonly HashSet<string> _hiddenTypes = new(StringComparer.OrdinalIgnoreCase);
    // Names of suppliers already searched (or given a link row) in the current search.
    private readonly HashSet<string> _searched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs one supplier's first page. Called on the UI thread, so it's safe to touch controls after each await.</summary>
    private async Task SearchOneAsync(SupplierRow item, string query, SearchMode mode,
                                      int max, SupplierFilters filters, CancellationToken ct,
                                      List<ResultRow>? added = null)
    {
        var s = item.Supplier;
        added ??= new List<ResultRow>();
        _searched.Add(s.Name);
        if (!s.IsConfigured)
        {
            added.Add(AddResult(s.MakeLinkResult(query), SeqFor(s, 0)));
            item.Status = "Link added";
            ApplyFilter(); // (also re-applies the column sort)
            return;
        }
        var st = new PagingState { Query = query, Mode = mode, Filters = filters };
        _paging[s] = st;
        await FetchPageAsync(item, st, max, ct, added);
    }

    /// <summary>A supplier ticked after the search: search just that one now, adding its rows at the end.</summary>
    private async Task SearchJustThisSupplierAsync(SupplierRow item)
    {
        if (_active == null) return;
        var s = item.Supplier;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var added = new List<ResultRow>();
        _batch++; // goes at the end, like Load more
        SetBusy(true);
        // After an order-code search, search the newcomer for the part number the code turned out to be.
        var byCode = _active.Mode == SearchMode.SupplierCode && _codeMpn.Length > 0;
        var q = byCode ? _codeMpn : _active.Query;
        var m = byCode ? SearchMode.ExactPartNumber : _active.Mode;
        SetStatus($"Searching {s.Name} for \"{q}\"…");
        try
        {
            await SearchOneAsync(item, q, m, _active.MaxResults, _active.SupplierFilters(), _cts.Token, added);
        }
        finally
        {
            SetBusy(false);
        }
        NumberNewRows();
        SelectRows(added);
        var parts = added.Count(r => !r.LinkOnly);
        SetStatus(s.IsConfigured
            ? $"Added {parts} part(s) from {s.Name} at the end. The new rows are selected."
            : $"Added a {s.Name} website link at the end.");
    }

    /// <summary>Fetches the next page for one supplier and adds its rows (to <paramref name="added"/> too).</summary>
    private async Task FetchPageAsync(SupplierRow item, PagingState st, int pageSize,
                                      CancellationToken ct, List<ResultRow> added)
    {
        var s = item.Supplier;
        var firstPage = st.NextOffset == 0;
        item.Status = firstPage ? "Searching…" : "Loading more…";
        item.Tip = "";
        var sw = Stopwatch.StartNew();
        try
        {
            var page = await s.SearchAsync(st.Query, st.Mode, pageSize, st.NextOffset, st.Filters, ct);
            st.NextOffset += pageSize;
            st.Total = page.TotalAvailable;
            st.LastPageFull = page.Parts.Count >= pageSize;
            var before = st.RowCount;
            // (Rows go straight into the list: the grid can't take new rows while the view's refresh is deferred.)
            foreach (var p in page.Parts)
            {
                // Skip parts already shown: pages can overlap (Mouser repeats one now and then; DigiKey's
                // exact matches). Manufacturer is in the key because Mouser lists e.g. both onsemi's and
                // Diodes' LM358 with no Mouser part number - different parts, not duplicates.
                var key = p.SupplierPartNumber + "|" + p.ManufacturerPartNumber + "|" + p.Manufacturer;
                if (key.Length > 2 && !st.Seen.Add(key))
                {
                    st.Duplicates++;
                    continue;
                }
                // Advanced search: price at the chosen quantity, then the "only show" filters.
                if (_active != null)
                {
                    if (_active.Quantity > 0) p.UnitPrice = AdvancedSearchOptions.PriceAt(p, _active.Quantity);
                    if (!_active.Matches(p, PriceInShownCurrency))
                    {
                        st.Filtered++;
                        _advancedHidden++;
                        continue;
                    }
                }
                st.RowCount++;
                added.Add(AddResult(p, SeqFor(s, st.RowCount)));
            }
            // Earlier rows (e.g. Mouser) may now be able to borrow a photo from these.
            RefreshThumbs();
            item.Status = st.RowCount == 0 ? "No matches" : "OK";
            item.Tip = $"{s.Name}: {st.RowCount} result(s)" +
                       (st.Total.HasValue ? $" of {st.Total} found." : ".") +
                       (st.Duplicates > 0 ? $" {st.Duplicates} repeated by {s.Name} and skipped." : "") +
                       (st.Filtered > 0 ? $" {st.Filtered} left out by the Advanced search filters." : "") +
                       (HasMore(s) ? " Right-click > Load more for the next batch." : "");
            if (!firstPage && st.RowCount == before) item.Status = "No more";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.Status = "Cancelled";
        }
        catch (Exception ex)
        {
            // Timeout, bad key, rate limit... show it, and on a first page fall back to a website link.
            item.Status = "Error";
            item.Tip = $"{s.Name}: {ex.Message}";
            if (firstPage) AddResult(s.MakeLinkResult(st.Query), SeqFor(s, 0));
        }
        item.Hits = HitsText(s, st);
        item.Time = $"{sw.Elapsed.TotalSeconds:0.0}s";
        // Re-apply the column the user sorted by (e.g. Price) to everything that's arrived so far, so the list
        // always matches the header's sort arrow - a new search keeps the sort, as in WinPartsFinder.
        ApplyFilter();
        UpdateLoadMoreButton();
    }

    /// <summary>"50 / 312" while there's more to load; just the count once everything has been fetched
    /// (which can be a little under the supplier's total, if it sent the same part twice).</summary>
    private string HitsText(Supplier s, PagingState st)
    {
        if (st.Total.HasValue && HasMore(s)) return $"{st.RowCount} / {st.Total}";
        return st.RowCount.ToString();
    }

    private bool HasMore(Supplier s)
    {
        return _paging.TryGetValue(s, out var st) && s.CanLoadMore(st.Mode) && st.HasMore;
    }

    /// <summary>Ticked suppliers that have another page to fetch.</summary>
    private List<SupplierRow> LoadMoreCandidates()
    {
        return _supplierRows.Where(i => i.IsChecked && HasMore(i.Supplier)).ToList();
    }

    private void UpdateLoadMoreButton()
    {
        btnLoadMore.IsEnabled = !_busy && LoadMoreCandidates().Count > 0;
    }

    private async void BtnLoadMore_Click(object? sender, RoutedEventArgs e)
    {
        await LoadMoreAsync(LoadMoreCandidates());
    }

    private async Task LoadMoreAsync(List<SupplierRow> items)
    {
        items = items.Where(i => HasMore(i.Supplier)).ToList();
        if (items.Count == 0 || _busy) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var pageSize = MaxPerSupplier;
        var added = new List<ResultRow>();
        _batch++; // this batch goes at the end
        SetBusy(true);
        SetStatus($"Loading more from {string.Join(", ", items.Select(i => i.Name))}…");
        try
        {
            await Task.WhenAll(items.Select(i => FetchPageAsync(i, _paging[i.Supplier], pageSize, ct, added)));
        }
        finally
        {
            SetBusy(false);
        }

        NumberNewRows();
        SelectRows(added);
        var summary = string.Join(", ", items.Select(i =>
        {
            var st = _paging[i.Supplier];
            return $"{i.Name} {st.RowCount}" + (st.Total.HasValue ? $" of {st.Total}" : "");
        }));
        SetStatus($"Loaded {added.Count} more ({summary}). The new rows are selected.");
    }

    /// <summary>Selects the given rows and scrolls to the first one, so new results are easy to spot.</summary>
    private void SelectRows(List<ResultRow> rows)
    {
        var shown = rows.Where(r => _view.Contains(r)).ToList();
        if (shown.Count == 0) return;
        gridResults.SelectedItems.Clear();
        foreach (var r in shown)
        {
            gridResults.SelectedItems.Add(r);
        }
        var first = _view.Cast<ResultRow>().First(r => shown.Contains(r));
        gridResults.ScrollIntoView(first, null);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy)
        {
            HideSuggestions();
            cboSearch.IsDropDownOpen = false;
        }
        btnSearch.IsEnabled = !busy;
        btnAdvanced.IsEnabled = !busy;
        btnCancel.IsEnabled = busy;
        btnSettings.IsEnabled = !busy;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
        UpdateLoadMoreButton();
        UpdateTypesButton();
    }

    private void SetStatus(string text)
    {
        lblStatus.Text = text;
        ToolTip.SetTip(lblStatus, text); // long messages are cut off at the window edge
    }

    #endregion

    #region Supplier list

    /// <summary>
    /// Untick: hide that supplier's rows (kept in memory). Tick: show them again - or, if it wasn't part
    /// of the current search, search just that supplier now.
    /// </summary>
    private async void SupplierRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(SupplierRow.IsChecked) || sender is not SupplierRow item) return;
        _settings.EnabledSuppliers[item.Name] = item.IsChecked;
        ApplyFilter();
        UpdateLoadMoreButton();

        var searchDone = _active != null && !string.IsNullOrEmpty(_active.Query);
        if (!item.IsChecked || !searchDone || _searched.Contains(item.Name)) return;
        if (_busy)
        {
            // Don't start a second search on top of one that's running.
            item.Status = "Not searched yet";
            item.Tip = $"{item.Name} was ticked during a search - press Search again to include it.";
            return;
        }
        await SearchJustThisSupplierAsync(item);
    }

    private void GridSuppliers_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is SupplierRow s) e.Row.Classes.Set("unconfigured", !s.Supplier.IsConfigured);
    }

    private SupplierRow? SelectedSupplier => gridSuppliers.SelectedItem as SupplierRow;

    private void GridSuppliers_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(gridSuppliers).Properties.IsRightButtonPressed) return;
        if ((e.Source as Control)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is SupplierRow s)
            gridSuppliers.SelectedItem = s;
    }

    private void CmsSuppliers_Opening(object? sender, CancelEventArgs e)
    {
        var hasQuery = SearchText.Length > 0;
        var s = SelectedSupplier?.Supplier;
        mnuOpenSupplierSearch.IsEnabled = hasQuery && s != null;
        mnuOpenAllSearches.IsEnabled = hasQuery;
        mnuLoadMoreSupplier.IsEnabled = s != null && !_busy && HasMore(s);
        mnuLoadMoreSupplier.Header = s == null ? "Load more" : $"Load {MaxPerSupplier} more from {s.Name}";
    }

    private async void MnuLoadMoreSupplier_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedSupplier is { } item) await LoadMoreAsync(new List<SupplierRow> { item });
    }

    private void MnuOpenSupplierSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedSupplier is { } item) OpenUrl(item.Supplier.GetSearchUrl(SearchText));
    }

    private void MnuOpenAllSearches_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _supplierRows.Where(i => i.IsChecked))
        {
            OpenUrl(item.Supplier.GetSearchUrl(SearchText));
        }
    }

    /// <summary>Double-click a supplier: open the search on its website.</summary>
    private void GridSuppliers_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var query = SearchText;
        if (query.Length == 0 || e.Source is not Control c || c.FindAncestorOfType<CheckBox>(includeSelf: true) != null) return;
        if (c.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is SupplierRow s) OpenUrl(s.Supplier.GetSearchUrl(query));
    }

    #endregion

    #region Advanced search / photo pop-up

    private async void BtnAdvanced_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || !btnAdvanced.IsEffectivelyEnabled) return;
        HideSuggestions();
        // Open with last time's choices, but today's search text, batch size and ticked suppliers.
        var start = _settings.LastAdvanced;
        // (Unless Search for was left blank last time and the search box still shows those words.)
        var typed = SearchText;
        if (typed.Length > 0 && !(start.ContainsIsQuery && string.Equals(typed, start.Query, StringComparison.OrdinalIgnoreCase)))
        {
            start.Query = typed;
            start.ContainsIsQuery = false;
        }
        start.MaxResults = MaxPerSupplier;
        start.Suppliers = _supplierRows.Where(i => i.IsChecked).Select(i => i.Name).ToList();

        var o = await new AdvancedSearchWindow(start, _suppliers.Select(s => s.Name).ToList()).ShowDialog<AdvancedSearchOptions?>(this);
        if (o == null) return;
        _settings.LastAdvanced = o;
        SetSearchText(o.Query);
        nudMax.Value = Math.Max(1, Math.Min(50, o.MaxResults));

        // Make the supplier list match the window (without the "ticked after a search" auto-search).
        _loading = true;
        foreach (var item in _supplierRows)
        {
            item.IsChecked = o.Suppliers.Contains(item.Name);
            _settings.EnabledSuppliers[item.Name] = item.IsChecked;
        }
        _loading = false;

        await RunSearchAsync(o, fromAdvanced: true);
    }

    private ImagePopupWindow? _photoPopup;

    /// <summary>Click a thumbnail: a bigger photo pops up beside the mouse. One at a time - another click replaces it.</summary>
    private void Thumb_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ResultRow row) return;
        var urls = ImageUrlsFor(row);
        if (urls.Thumb.Length == 0 || _images.HasFailed(urls.Thumb)) return; // nothing to enlarge

        var title = string.Join(" - ", new[] { row.MfrPartNo, row.Manufacturer }.Where(s => s.Length > 0));
        _photoPopup?.Close();
        var popup = new ImagePopupWindow(urls.Large, urls.Thumb, title.Length > 0 ? title : "Photo", row.Description);
        var at = this.PointToScreen(e.GetPosition(this));
        var scale = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
        popup.Position = new PixelPoint(at.X + (int)(16 * scale), at.Y + (int)(16 * scale));
        popup.Closed += (_, _) =>
        {
            if (_photoPopup == popup) _photoPopup = null;
        };
        _photoPopup = popup;
        popup.Show(this);
    }

    #endregion

    #region Settings / help / export / links

    private async void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        HideSuggestions();
        var ratesBefore = _settings.Rates;
        var dlg = new SettingsWindow(_settings);
        var saved = await dlg.ShowDialog<bool>(this);
        // "Update now" fetched new exchange rates: show them straight away, even if Settings was cancelled.
        if (!saved && _settings.Rates != ratesBefore) ApplyConversion(showStatus: false);
        if (!saved) return;
        if (dlg.ResetWindowRequested) ResetWindowLayout();
        var error = SaveUiSettings();
        SettingsWindow.ApplyTheme(_settings.DarkMode);
        RebuildSuppliers();
        ApplyThumbnailSetting();
        UpdateConvertText();
        ApplyConversion(showStatus: false); // the "Prices in" currency may have changed
        // Rows already on screen pick up the photo setting when they're next shown.
        if (_settings.ShowThumbnails) RefreshThumbsOnScreen();
        if (error.Length > 0)
        {
            await MessageBox.Show(this, error, Title ?? "");
            SetStatus(error);
        }
        else
        {
            SetStatus("Settings saved.");
        }
    }

    /// <summary>After turning photos on: fetch them for every row (the grid shows the ones on screen).</summary>
    private void RefreshThumbsOnScreen()
    {
        foreach (var r in _view.Cast<ResultRow>().Take(200))
        {
            EnsureThumb(r);
        }
    }

    // Help stays open (not modal) so it can be read while using the app; one window at a time.
    private HelpWindow? _help;

    private void BtnHelp_Click(object? sender, RoutedEventArgs e)
    {
        if (_help != null)
        {
            _help.Activate();
            return;
        }
        _help = new HelpWindow();
        _help.Closed += (_, _) => _help = null;
        _help.Show(this);
    }

    private void BtnAbout_Click(object? sender, RoutedEventArgs e) => ShowAbout();

    /// <summary>Also used by the Mac's app menu (MacPartsFinder > About MacPartsFinder).</summary>
    public async void ShowAbout()
    {
        await new AboutWindow().ShowDialog(this);
    }

    private List<DataGridColumn> VisibleColumns()
    {
        return gridResults.Columns.Where(c => c.IsVisible && ColumnName(c) != "Thumb")
                                  .OrderBy(c => c.DisplayIndex).ToList();
    }

    private async void BtnExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_view.Count == 0)
        {
            await MessageBox.Show(this, "Nothing to export yet.", Title ?? "");
            return;
        }
        var safeName = string.Concat(SearchText.Split(Path.GetInvalidFileNameChars()));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export CSV",
            SuggestedFileName = $"MacPartsFinder {safeName} {DateTime.Now:yyyy-MM-dd HHmm}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV file") { Patterns = new[] { "*.csv" } } }
        });
        if (file == null) return;

        // The Datasheet column is the URL itself; it goes at the end as "Datasheet URL" instead.
        var cols = VisibleColumns().Where(c => ColumnName(c) != "Datasheet").ToList();
        var names = cols.Select(ColumnName).Concat(new[] { "ProductUrl", "DatasheetUrl" }).ToList();
        var headers = cols.Select(HeaderText).Concat(new[] { "Product URL", "Datasheet URL" }).ToList();
        if (Converting) // keep the supplier's own prices too
        {
            names.AddRange(new[] { "OrigUnitPrice", "OrigCurrency", "OrigBreaks" });
            headers.AddRange(new[] { "Supplier's price", "Supplier's currency", "Supplier's price breaks" });
        }
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(CsvQuote)));
        foreach (var row in _view.Cast<ResultRow>())
        {
            sb.AppendLine(string.Join(",", names.Select(n => CsvQuote(Field(row, n)))));
        }
        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true)); // BOM so Excel shows £ properly
            await writer.WriteAsync(sb.ToString());
            SetStatus("Exported to " + (file.TryGetLocalPath() ?? file.Name));
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Export failed: " + ex.Message, Title ?? "");
        }
    }

    private static string CsvQuote(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private async void OpenUrl(string url)
    {
        try
        {
            WebHelper.OpenInBrowser(url);
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Could not open " + url + Environment.NewLine + ex.Message, Title ?? "");
        }
    }

    #endregion
}
