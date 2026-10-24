// MacPartsFinder - Mac version of WinPartsFinder by Ian Johnston

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MacPartsFinder;

public partial class MainWindow : Window
{
    // ==== THE version number - change it here for each release ====
    public const string AppVersion = "V0.1";

    private AppSettings _settings;
    private List<Supplier> _suppliers = new();
    private readonly ObservableCollection<SupplierRow> _supplierRows = new();
    private readonly ObservableCollection<ResultRow> _rows = new();
    private readonly DataGridCollectionView _view;
    private CancellationTokenSource? _cts;
    private bool _loading;

    #region Startup / shutdown

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        Title = $"MacPartsFinder - {AppVersion}  (test build)";

        cboMode.ItemsSource = new[] { "Keyword", "Manufacturer part no." };
        cboMode.SelectedIndex = 0;
        nudMax.Value = Math.Max(1, Math.Min(50, _settings.MaxResults));
        _loading = true;
        chkInStock.IsChecked = _settings.InStockOnly;
        _loading = false;

        // Filtered (hidden rows stay in _rows) and in arrival order until the user sorts by a column.
        _view = new DataGridCollectionView(_rows) { Filter = o => RowVisible((ResultRow)o) };
        _view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(ResultRow.Order)));
        gridResults.ItemsSource = _view;
        gridSuppliers.ItemsSource = _supplierRows;

        RebuildSuppliers();
        ApplyFilter();

        if (!_suppliers.Any(s => s.IsConfigured))
            SetStatus("No API keys set yet - click API keys (test)… to add Mouser / DigiKey / element14 / TME keys. Until then you get website links.");
        else
            SetStatus("Ready.");

        Opened += async (_, _) =>
        {
            txtSearch.Focus();
            if (_settings.LoadError.Length > 0) await MessageBox.Show(this, _settings.LoadError, Title ?? "");
        };
        Closing += (_, _) =>
        {
            _cts?.Cancel();
            SaveUiSettings();
        };
    }

    private void SaveUiSettings()
    {
        _settings.MaxResults = MaxPerSupplier;
        _settings.InStockOnly = chkInStock.IsChecked == true;
        foreach (var row in _supplierRows)
        {
            _settings.EnabledSuppliers[row.Name] = row.IsChecked;
        }
        try
        {
            _settings.Save();
        }
        catch
        {
            // Closing - nowhere to show it. (Keys are saved, with any error shown, from their own window.)
        }
    }

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

    #region Results list

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
        _rows.Add(row);
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

    private void UpdateCountStatus()
    {
        UpdateTotalParts();
        var shown = _view.Count;
        var total = _rows.Count;
        if (total == 0) return;
        var removed = RemovedCount();
        var hidden = HiddenNote();
        SetStatus($"{shown} of {total} results shown." +
                  (removed > 0 ? $" ({removed} removed)" : "") +
                  (hidden.Length > 0 ? " " + hidden : ""));
    }

    private int ShowingParts() => _view.Cast<ResultRow>().Count(r => !r.LinkOnly);

    /// <summary>"Total parts = 140", plus "(showing 98)" when Filter / In stock only hide some. Website-link rows don't count.</summary>
    private void UpdateTotalParts()
    {
        var parts = _rows.Count(r => !r.LinkOnly);
        var showing = ShowingParts();
        lblTotal.Text = $"Total parts = {parts:#,##0}" + (showing < parts ? $"  (showing {showing:#,##0})" : "");
    }

    private int RemovedCount() => _rows.Count(r => r.Removed);

    private void GridResults_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        // Rows are reused while scrolling, so set every class each time.
        if (e.Row.DataContext is not ResultRow r) return;
        e.Row.Classes.Set("link", r.LinkOnly);
        e.Row.Classes.Set("nostock", !r.LinkOnly && r.Stock == 0);
        e.Row.Classes.Set("instock", !r.LinkOnly && r.Stock != 0);
    }

    private void GridResults_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // The link cells already act on a single click; headers aren't rows.
        if (e.Source is not Control c || c.FindAncestorOfType<HyperlinkButton>(includeSelf: true) != null) return;
        if (c.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is ResultRow r) OpenUrl(r.ProductUrl);
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

    #endregion

    #region Searching

    private async void BtnSearch_Click(object? sender, RoutedEventArgs e)
    {
        // A normal search: just the toolbar settings, no Advanced filters.
        await RunSearchAsync(new AdvancedSearchOptions
        {
            Query = (txtSearch.Text ?? "").Trim(),
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
            txtSearch.Focus();
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

    /// <summary>The Price column shows the price at the Advanced search quantity, when one is set.</summary>
    private void UpdatePriceHeader()
    {
        var qty = _active?.Quantity ?? 0;
        var colPrice = gridResults.Columns.First(c => Equals(c.Tag, "UnitPrice"));
        colPrice.Header = qty > 0 ? $"Price ({qty:#,##0})" : "Price (1)";
    }

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
            UpdateCountStatus();
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
                    if (!_active.Matches(p))
                    {
                        st.Filtered++;
                        _advancedHidden++;
                        continue;
                    }
                }
                st.RowCount++;
                added.Add(AddResult(p, SeqFor(s, st.RowCount)));
            }
            item.Status = st.RowCount == 0 ? "No matches" : "OK";
            item.Tip = $"{s.Name}: {st.RowCount} result(s)" +
                       (st.Total.HasValue ? $" of {st.Total} found." : ".") +
                       (st.Duplicates > 0 ? $" {st.Duplicates} repeated by {s.Name} and skipped." : "") +
                       (st.Filtered > 0 ? $" {st.Filtered} left out by the Advanced search filters." : "") +
                       (HasMore(s) ? " Click Load more for the next batch." : "");
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
        UpdateCountStatus();
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
        btnSearch.IsEnabled = !busy;
        btnCancel.IsEnabled = busy;
        btnKeys.IsEnabled = !busy;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
        UpdateLoadMoreButton();
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

    /// <summary>Double-click a supplier: open the search on its website.</summary>
    private void GridSuppliers_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var query = (txtSearch.Text ?? "").Trim();
        if (query.Length == 0 || e.Source is not Control c || c.FindAncestorOfType<CheckBox>(includeSelf: true) != null) return;
        if (c.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is SupplierRow s) OpenUrl(s.Supplier.GetSearchUrl(query));
    }

    #endregion

    #region Keys / links

    private async void BtnKeys_Click(object? sender, RoutedEventArgs e)
    {
        var saved = await new KeysWindow(_settings).ShowDialog<bool>(this);
        if (!saved) return;
        RebuildSuppliers();
        SetStatus("API keys saved.");
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
