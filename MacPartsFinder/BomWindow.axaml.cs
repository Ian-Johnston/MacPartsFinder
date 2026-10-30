using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MacPartsFinder;

/// <summary>
/// The BOM window: parts collected across searches (right-click > Add to BOM, or the toolbar button), with
/// quantities, totals and a link to each product page for buying. Stays open beside the main window; closing
/// it only hides it, so the list is kept. As in WinPartsFinder.
/// </summary>
public partial class BomWindow : Window
{
    private Bom _bom = new();
    private string _filePath = "";
    private bool _dirty;
    private bool _settingText;          // true while the name / notes boxes are filled from code
    private bool _closingForGood;       // the app is closing - really close, don't just hide
    // Each Remove (one or several lines) can be put back with ⌘Z, most recent first.
    private readonly Stack<List<(int Index, BomLine Line)>> _undo = new();
    // "Prices in" currency from the main window; null = show each supplier's own currency.
    private string? _convertTo;
    private ExchangeRates? _rates;
    private readonly ObservableCollection<BomRow> _rows = new();

    /// <summary>Raised whenever the lines, name or notes change, or another BOM is loaded.</summary>
    public event EventHandler? BomChanged;

    public Bom Bom => _bom;

    /// <summary>The file this BOM was last saved to or loaded from; "" = never saved.</summary>
    public string FilePath => _filePath;

    /// <summary>Folder the last BOM was saved to or loaded from - where Load, Save As and Open Folder start.</summary>
    public string LastFolder { get; set; } = "";

    /// <summary>True once the window has been shown (so its place is worth saving).</summary>
    public bool HasBeenShown { get; private set; }

    // Normal (not maximised) position and size, kept up to date for saving.
    private PixelPoint _normalPosition;
    private Size _normalSize;

    public BomWindow()
    {
        InitializeComponent();
        gridBom.ItemsSource = _rows;
        // Right-click selects the line under the mouse first (unless it's already part of the selection).
        gridBom.AddHandler(PointerPressedEvent, GridBom_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) =>
        {
            HasBeenShown = true;
            _normalPosition = Position;
            _normalSize = ClientSize;
        };
        PositionChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _normalPosition = Position;
        };
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _normalSize = ClientSize;
        };
        // The close button only hides the window - the BOM stays (and the app asks about saving it when it closes).
        Closing += (_, e) =>
        {
            if (_closingForGood) return;
            e.Cancel = true;
            CommitEdit();
            Hide();
        };
        RefreshGrid();
    }

    /// <summary>True when there's something in the BOM that hasn't been saved (an empty, unnamed BOM doesn't count).</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            CommitEdit();
            return _dirty && !(_bom.Lines.Count == 0 && _bom.Name.Trim().Length == 0 && _bom.Notes.Trim().Length == 0);
        }
    }

    /// <summary>Where the window is, for saving in the settings.</summary>
    public WindowPlacement Placement => new()
    {
        X = _normalPosition.X,
        Y = _normalPosition.Y,
        Width = (int)_normalSize.Width,
        Height = (int)_normalSize.Height
    };

    /// <summary>Closes the window properly (when the app closes).</summary>
    public void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }

    /// <summary>Adds parts (or adds to the quantity of ones already in the BOM). Returns how many lines are new.</summary>
    public int AddLines(IEnumerable<BomLine> lines)
    {
        var before = _bom.Lines.Count;
        var touched = lines.Select(l => _bom.Add(l)).ToList();
        if (touched.Count == 0) return 0;
        RefreshGrid(touched);
        MarkDirty();
        return _bom.Lines.Count - before;
    }

    /// <summary>Shows prices in <paramref name="convertTo"/> (the main window's "Prices in"), or null for the suppliers' own.</summary>
    public void SetCurrency(string? convertTo, ExchangeRates rates)
    {
        _convertTo = convertTo;
        _rates = rates;
        RefreshGrid();
    }

    /// <summary>Finishes typing in a Qty cell, so the value isn't lost.</summary>
    public void CommitEdit() => gridBom.CommitEdit();

    private void SetBom(Bom bom, string filePath, bool dirty)
    {
        _bom = bom;
        _filePath = filePath;
        _dirty = dirty;
        _undo.Clear();
        _settingText = true;
        txtName.Text = bom.Name;
        txtNotes.Text = bom.Notes;
        _settingText = false;
        RefreshGrid();
        BomChanged?.Invoke(this, EventArgs.Empty);
    }

    #region Grid

    private List<BomLine> SelectedLines() =>
        gridBom.SelectedItems.OfType<BomRow>().OrderBy(r => _rows.IndexOf(r)).Select(r => r.Line).ToList();

    /// <summary>
    /// Shows the BOM's lines, keeping the selection (or selecting <paramref name="select"/>). When the lines are the
    /// same ones as before only their text is updated, so the list doesn't jump.
    /// </summary>
    private void RefreshGrid(IEnumerable<BomLine>? select = null)
    {
        var keep = select?.ToList() ?? SelectedLines();
        if (_rows.Select(r => r.Line).SequenceEqual(_bom.Lines))
        {
            foreach (var r in _rows) FillRow(r);
        }
        else
        {
            _rows.Clear();
            foreach (var l in _bom.Lines)
            {
                var r = new BomRow(l, IncludeChanged);
                FillRow(r);
                _rows.Add(r);
            }
        }
        if (select != null || !gridBom.SelectedItems.OfType<BomRow>().Select(r => r.Line).SequenceEqual(keep))
        {
            gridBom.SelectedItems.Clear();
            foreach (var r in _rows.Where(r => keep.Contains(r.Line))) gridBom.SelectedItems.Add(r);
        }
        if (select != null && _rows.FirstOrDefault(r => keep.Contains(r.Line)) is { } first) gridBom.ScrollIntoView(first, null);
        UpdateRowClasses();
        UpdateTotals();
        UpdateButtons();
    }

    private void FillRow(BomRow r)
    {
        var l = r.Line;
        var price = ShownPrice(l);
        r.Update(
            include: l.Include,
            supplier: l.Supplier,
            partNo: l.SupplierPartNumber,
            mpn: l.ManufacturerPartNumber,
            description: DescriptionText(l),
            qty: l.Qty.ToString(CultureInfo.CurrentCulture),
            unitPrice: price.Unit.HasValue ? Money(price.Unit.Value, price.Currency, "0.00##") : "",
            total: price.Unit.HasValue ? Money(price.Unit.Value * l.Qty, price.Currency, "0.00") : "",
            converted: price.Converted,
            infoTip: InfoTip(l),
            priceTip: PriceTip(l, price.Converted),
            linkTip: l.ProductUrl.Length == 0 ? null
                : l.Supplier.Length > 0 ? $"Open this part's page on {l.Supplier}'s website" : "Open this part's page");
    }

    // Unticked lines are greyed out; converted prices are purple, as in the results (see the styles).
    private void GridBom_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is BomRow r) SetRowClasses(e.Row, r);
    }

    /// <summary>The rows on screen, after a line was ticked / unticked or its price changed (LoadingRow only runs as rows appear).</summary>
    private void UpdateRowClasses()
    {
        foreach (var row in gridBom.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.DataContext is BomRow r) SetRowClasses(row, r);
        }
    }

    private static void SetRowClasses(DataGridRow row, BomRow r)
    {
        row.Classes.Set("off", !r.Include);
        row.Classes.Set("conv", r.Include && r.Converted);
    }

    /// <summary>The description, or for a line loaded without one (no supplier yet) its refs, value and footprint.</summary>
    private static string DescriptionText(BomLine l)
    {
        if (l.Description.Length > 0) return l.Description;
        return string.Join("  ", new[] { l.Refs, l.ManufacturerPartNumber, l.Footprint }.Where(s => s.Length > 0));
    }

    /// <summary>The price each at the line's quantity, converted to the "Prices in" currency when that's on.</summary>
    private (decimal? Unit, string Currency, bool Converted) ShownPrice(BomLine l)
    {
        var unit = l.PriceEach();
        if (!unit.HasValue) return (null, l.Currency, false);
        if (_convertTo != null && _rates != null && !string.Equals(l.Currency, _convertTo, StringComparison.OrdinalIgnoreCase))
        {
            var rate = _rates.Rate(l.Currency, _convertTo);
            if (rate.HasValue) return (Math.Round(unit.Value * rate.Value, 4), _convertTo, true);
        }
        return (unit, l.Currency, false);
    }

    /// <summary>e.g. "£1.25", "€0.139", "CHF 3.10".</summary>
    private static string Money(decimal value, string? currency, string format)
    {
        var number = value.ToString(format, CultureInfo.CurrentCulture);
        return (currency ?? "").ToUpperInvariant() switch
        {
            "GBP" => "£" + number,
            "EUR" => "€" + number,
            "USD" => "$" + number,
            "" => number,
            var c => c + " " + number
        };
    }

    /// <summary>Several amounts in different currencies, e.g. "£61.20 + €11.40", in the order the currencies first appear in the list.</summary>
    private static string MoneyList(Dictionary<string, decimal> amounts, List<string> order) =>
        string.Join(" + ", amounts.OrderBy(a => order.IndexOf(a.Key)).Select(a => Money(a.Value, a.Key, "0.00")));

    private void UpdateTotals()
    {
        var bySupplier = new List<(string Supplier, Dictionary<string, decimal> Amounts)>();
        var grand = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var currencies = new List<string>();
        var noPrice = 0;
        var anyConverted = false;
        foreach (var l in _bom.Lines.Where(x => x.Include))
        {
            var price = ShownPrice(l);
            if (!price.Unit.HasValue)
            {
                noPrice++;
                continue;
            }
            anyConverted |= price.Converted;
            var amount = price.Unit.Value * l.Qty;
            var name = l.Supplier.Length > 0 ? l.Supplier : "(no supplier)";
            var entry = bySupplier.FirstOrDefault(s => string.Equals(s.Supplier, name, StringComparison.OrdinalIgnoreCase));
            if (entry.Amounts == null)
            {
                entry = (name, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));
                bySupplier.Add(entry);
            }
            var cur = price.Currency.ToUpperInvariant();
            if (!currencies.Contains(cur)) currencies.Add(cur);
            entry.Amounts[cur] = entry.Amounts.GetValueOrDefault(cur) + amount;
            grand[cur] = grand.GetValueOrDefault(cur) + amount;
        }

        if (_bom.Lines.Count == 0)
        {
            lblSubtotals.Text = "No parts yet - right-click a result and choose Add to BOM.";
        }
        else
        {
            var bits = bySupplier.Select(s => $"{s.Supplier} {MoneyList(s.Amounts, currencies)}").ToList();
            if (noPrice > 0) bits.Add($"{noPrice} without a price");
            lblSubtotals.Text = string.Join("  ·  ", bits);
        }
        lblGrandTotal.Text = "Grand total: " + (grand.Count == 0 ? Money(0m, _convertTo ?? "", "0.00") : MoneyList(grand, currencies));
        ToolTip.SetTip(lblSubtotals, (lblSubtotals.Text ?? "").Replace("  ·  ", Environment.NewLine));
        ToolTip.SetTip(lblGrandTotal, anyConverted && _rates != null
            ? $"Converted prices use the {_rates.Describe()} - approximate: the supplier's own rate, VAT and import charges will differ."
            : "Ticked lines only.");
        Title = $"BOM – {_bom.DisplayName}{(_dirty ? " *" : "")}";
    }

    private void UpdateButtons()
    {
        var any = gridBom.SelectedItems.Count > 0;
        btnRemove.IsEnabled = any;
        btnQtyDown.IsEnabled = any;
        btnQtyUp.IsEnabled = any;
    }

    private void GridBom_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTotals();
        BomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A line's tick box was clicked.</summary>
    private void IncludeChanged(BomRow r)
    {
        if (r.Line.Include == r.Include) return;
        r.Line.Include = r.Include;
        FillRow(r);
        UpdateRowClasses();
        MarkDirty();
    }

    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BomRow r) OpenUrl(r.Line.ProductUrl);
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BomRow r) RemoveLines(new[] { r.Line });
    }

    // Double-click a line to open its product page (not on the tick box, Qty, link or ✕, which do their own thing).
    private void GridBom_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var cell = (e.Source as Control)?.FindAncestorOfType<DataGridCell>(includeSelf: true);
        if (cell == null || cell.Classes.Contains("noopen")) return;
        if (cell.DataContext is BomRow r) OpenUrl(r.Line.ProductUrl);
    }

    // Qty: whole numbers only. 0 asks whether to remove the line; anything else that isn't a number puts the old value back.
    private void GridBom_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.Row.DataContext is not BomRow r) return;
        var typed = (r.Qty ?? "").Trim();
        var valid = int.TryParse(typed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var q) && q >= 0;
        var commit = e.EditAction == DataGridEditAction.Commit;
        // (The grid is still finishing the edit - change its rows afterwards.)
        Dispatcher.UIThread.Post(async () =>
        {
            if (commit && valid && q == 0)
            {
                await AskRemove(new[] { r.Line });
            }
            else if (commit && valid && q != r.Line.Qty)
            {
                r.Line.Qty = q;
                RefreshGrid();
                MarkDirty();
            }
            else
            {
                RefreshGrid();
            }
        });
    }

    private void GridBom_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(gridBom).Properties.IsRightButtonPressed) return;
        if ((e.Source as Control)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is BomRow r &&
            !gridBom.SelectedItems.Contains(r))
            gridBom.SelectedItem = r;
    }

    /// <summary>⌘ on a Mac, Ctrl on Windows.</summary>
    private static bool IsCommandKey(KeyModifiers m) =>
        m == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && IsCommandKey(e.KeyModifiers))
        {
            e.Handled = true;
            CommitEdit();
            await SaveBom();
            return;
        }
        // Typing in the name, the notes or a Qty cell: their keys are their own.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        if (e.Key == Key.Z && IsCommandKey(e.KeyModifiers))
        {
            e.Handled = true;
            UndoRemove();
            return;
        }
        var inGrid = (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual)?
                     .FindAncestorOfType<DataGrid>(includeSelf: true) == gridBom;
        if (!inGrid || e.KeyModifiers != KeyModifiers.None) return;
        // Delete (or the Mac's delete key, which is Backspace) removes the selected lines.
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            e.Handled = true;
            RemoveLines(SelectedLines());
        }
        else if (e.Key == Key.Space)
        {
            // Space ticks / unticks the selected lines.
            e.Handled = true;
            var lines = SelectedLines();
            if (lines.Count == 0) return;
            var include = !lines.All(l => l.Include);
            lines.ForEach(l => l.Include = include);
            RefreshGrid();
            MarkDirty();
        }
    }

    private string? InfoTip(BomLine l)
    {
        var info = new List<string>();
        if (l.Description.Length > 0) info.Add(l.Description);
        if (l.ManufacturerPartNumber.Length > 0) info.Add("Manufacturer part no.: " + l.ManufacturerPartNumber);
        if (l.Manufacturer.Length > 0) info.Add("Manufacturer: " + l.Manufacturer);
        if (l.Footprint.Length > 0) info.Add("Package: " + l.Footprint);
        if (l.Refs.Length > 0) info.Add("Refs: " + l.Refs);
        if (l.Stock.HasValue) info.Add($"Stock when added: {l.Stock.Value:N0}");
        if (l.DateAdded.HasValue) info.Add($"Added: {l.DateAdded.Value:d MMM yyyy}");
        return info.Count > 0 ? string.Join(Environment.NewLine, info) : null;
    }

    private string? PriceTip(BomLine l, bool converted)
    {
        var prices = new List<string>();
        var unit = l.PriceEach();
        if (unit.HasValue) prices.Add($"Supplier's price: {Money(unit.Value, l.Currency, "0.00##")} each at qty {l.Qty}");
        if (l.PriceBreaks.Count > 1)
            prices.Add("Price breaks: " + string.Join("   ", l.PriceBreaks.OrderBy(b => b.Quantity).Select(b => $"{b.Quantity}+ {b.Price:0.00##}")));
        if (converted && _rates != null) prices.Add($"Converted to {_convertTo} at the {_rates.Describe()} (approximate)");
        return prices.Count > 0 ? string.Join(Environment.NewLine, prices) : null;
    }

    #endregion

    #region Removing lines and quantities

    private void RemoveLines(IEnumerable<BomLine> lines)
    {
        var removed = lines.Select(l => (Index: _bom.Lines.IndexOf(l), Line: l)).Where(x => x.Index >= 0)
                           .OrderBy(x => x.Index).ToList();
        if (removed.Count == 0) return;
        foreach (var x in removed) _bom.Lines.Remove(x.Line);
        _undo.Push(removed);
        RefreshGrid();
        MarkDirty();
    }

    private async Task AskRemove(IList<BomLine> lines)
    {
        var what = lines.Count == 1
            ? (lines[0].SupplierPartNumber.Length > 0 ? lines[0].SupplierPartNumber : "this line")
            : $"these {lines.Count} lines";
        if (await MessageBox.AskYesNo(this, $"Remove {what} from the BOM?", Title ?? ""))
            RemoveLines(lines);
        else
            RefreshGrid();
    }

    private void UndoRemove()
    {
        if (_undo.Count == 0) return;
        var removed = _undo.Pop();
        foreach (var x in removed) // lowest index first, so each goes back where it was
        {
            _bom.Lines.Insert(Math.Min(x.Index, _bom.Lines.Count), x.Line);
        }
        RefreshGrid(removed.Select(x => x.Line));
        MarkDirty();
    }

    private void BtnRemove_Click(object? sender, RoutedEventArgs e) => RemoveLines(SelectedLines());

    private void BtnQtyUp_Click(object? sender, RoutedEventArgs e)
    {
        var lines = SelectedLines();
        if (lines.Count == 0) return;
        lines.ForEach(l => l.Qty++);
        RefreshGrid();
        MarkDirty();
    }

    // Lines already at 1 would go to 0: those are offered for removal instead.
    private async void BtnQtyDown_Click(object? sender, RoutedEventArgs e)
    {
        var lines = SelectedLines();
        if (lines.Count == 0) return;
        var atOne = lines.Where(l => l.Qty <= 1).ToList();
        var others = lines.Except(atOne).ToList();
        if (others.Count > 0)
        {
            others.ForEach(l => l.Qty--);
            RefreshGrid(lines);
            MarkDirty();
        }
        if (atOne.Count > 0) await AskRemove(atOne);
    }

    private void CmsBom_Opening(object? sender, CancelEventArgs e)
    {
        var lines = SelectedLines();
        if (lines.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        var l = MenuLine() ?? lines[0];
        mnuOpenProduct.IsEnabled = l.ProductUrl.Length > 0;
        mnuCopyPartNo.IsEnabled = l.SupplierPartNumber.Length > 0;
        mnuCopyMpn.IsEnabled = l.ManufacturerPartNumber.Length > 0;
        mnuRemove.Header = lines.Count > 1 ? $"Remove {lines.Count} lines" : "Remove";
    }

    private BomLine? MenuLine() => (gridBom.SelectedItem as BomRow)?.Line ?? SelectedLines().FirstOrDefault();

    private void MnuOpenProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuLine() is { } l) OpenUrl(l.ProductUrl);
    }

    private void MnuCopyPartNo_Click(object? sender, RoutedEventArgs e) => CopyText(MenuLine()?.SupplierPartNumber ?? "");

    private void MnuCopyMpn_Click(object? sender, RoutedEventArgs e) => CopyText(MenuLine()?.ManufacturerPartNumber ?? "");

    private async void CopyText(string text)
    {
        if (text.Length == 0 || Clipboard == null) return;
        await Clipboard.SetTextAsync(text);
    }

    private async void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
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

    #region Name, notes and files

    private void TxtName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_settingText) return;
        _bom.Name = txtName.Text ?? "";
        MarkDirty();
    }

    private void TxtNotes_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_settingText) return;
        _bom.Notes = txtNotes.Text ?? "";
        MarkDirty();
    }

    private async void BtnNew_Click(object? sender, RoutedEventArgs e)
    {
        CommitEdit();
        if (!await ConfirmDiscard()) return;
        SetBom(new Bom(), "", dirty: false);
        txtName.Focus();
    }

    private async void BtnLoad_Click(object? sender, RoutedEventArgs e)
    {
        CommitEdit();
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load BOM",
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolder(),
            FileTypeFilter = new[]
            {
                new FilePickerFileType("BOM (CSV)") { Patterns = new[] { "*.csv" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;
        try
        {
            SetBom(Bom.Load(path), path, dirty: false);
            LastFolder = Path.GetDirectoryName(path) ?? "";
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Could not load the BOM: " + ex.Message, Title ?? "");
        }
    }

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        CommitEdit();
        await SaveBom();
    }

    private async void BtnSaveAs_Click(object? sender, RoutedEventArgs e)
    {
        CommitEdit();
        await SaveBomAs();
    }

    /// <summary>Unsaved changes: Save / Don't Save / Cancel. Returns false for Cancel (or a save that failed).</summary>
    public async Task<bool> ConfirmDiscard()
    {
        if (!HasUnsavedChanges) return true;
        return await MessageBox.AskSave(this, $"Save changes to \"{_bom.DisplayName}\"?", Title ?? "") switch
        {
            SaveChoice.Save => await SaveBom(),
            SaveChoice.DontSave => true,
            _ => false
        };
    }

    private async Task<bool> SaveBom()
    {
        if (_filePath.Length == 0) return await SaveBomAs();
        return await WriteFile(_filePath);
    }

    /// <summary>Asks where to save. An unnamed BOM takes the file's name.</summary>
    private async Task<bool> SaveBomAs()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save BOM",
            SuggestedFileName = SafeName() + ".csv",
            SuggestedStartLocation = await StartFolder(),
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("BOM (CSV)") { Patterns = new[] { "*.csv" } } }
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return false;
        if (_bom.Name.Trim().Length == 0)
        {
            _settingText = true;
            txtName.Text = Path.GetFileNameWithoutExtension(path);
            _settingText = false;
            _bom.Name = txtName.Text ?? "";
        }
        return await WriteFile(path);
    }

    private async Task<bool> WriteFile(string path)
    {
        try
        {
            _bom.Save(path);
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Could not save the BOM: " + ex.Message, Title ?? "");
            return false;
        }
        _filePath = path;
        LastFolder = Path.GetDirectoryName(path) ?? "";
        _dirty = false;
        UpdateTotals();
        BomChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The BOM's name made safe for a file name ("Untitled BOM" if that leaves nothing).</summary>
    private string SafeName()
    {
        var safe = string.Concat(_bom.DisplayName.Split(Path.GetInvalidFileNameChars())).Trim();
        return safe.Length > 0 ? safe : Bom.UntitledName;
    }

    /// <summary>This BOM's folder, else the last one used, else "" (the file dialogs then start where they like).</summary>
    private string StartFolderPath()
    {
        foreach (var folder in new[] { _filePath.Length > 0 ? Path.GetDirectoryName(_filePath) : "", LastFolder })
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder;
        }
        return "";
    }

    private async Task<IStorageFolder?> StartFolder()
    {
        var folder = StartFolderPath();
        return folder.Length == 0 ? null : await StorageProvider.TryGetFolderFromPathAsync(folder);
    }

    private async void BtnExportTxt_Click(object? sender, RoutedEventArgs e)
    {
        CommitEdit();
        if (_bom.Lines.Count == 0)
        {
            await MessageBox.Show(this, "Nothing to export yet.", Title ?? "");
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export TXT",
            SuggestedFileName = SafeName() + ".txt",
            SuggestedStartLocation = await StartFolder(),
            DefaultExtension = "txt",
            FileTypeChoices = new[] { new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } } }
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            File.WriteAllText(path, BomText(), new UTF8Encoding(true)); // BOM so £ and € show properly everywhere
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Could not export the BOM: " + ex.Message, Title ?? "");
        }
    }

    /// <summary>
    /// The BOM as plain text for reading: its name, then each supplier's lines in columns lined up with spaces
    /// (with a subtotal), then the grand total. Prices are as shown in the window. Unticked lines are listed but not counted.
    /// </summary>
    private string BomText()
    {
        const int maxDescription = 45;
        const string gap = "  ";
        var headers = new[] { "Supplier Part No.", "Mfr Part No.", "Description", "Qty", "Unit Price", "Total", "Link" };
        var rightAligned = new[] { false, false, false, true, true, true, false };
        var cells = _bom.Lines.Select(l =>
        {
            var price = ShownPrice(l);
            var desc = DescriptionText(l);
            if (desc.Length > maxDescription) desc = desc.Substring(0, maxDescription - 1) + "…";
            return new[]
            {
                l.SupplierPartNumber, l.ManufacturerPartNumber, desc, l.Qty.ToString(CultureInfo.CurrentCulture),
                price.Unit.HasValue ? Money(price.Unit.Value, price.Currency, "0.00##") : "",
                price.Unit.HasValue ? Money(price.Unit.Value * l.Qty, price.Currency, "0.00") : "",
                l.ProductUrl + (l.Include ? "" : "   (not counted)")
            };
        }).ToList();
        // Each column as wide as its widest entry (the link is last, so it isn't padded).
        var widths = Enumerable.Range(0, headers.Length)
            .Select(c => Math.Max(headers[c].Length, cells.Max(r => r[c].Length))).ToArray();
        string LineOf(string[] values) =>
            ("  " + string.Join(gap, values.Select((v, c) => c == values.Length - 1 ? v
                : rightAligned[c] ? v.PadLeft(widths[c]) : v.PadRight(widths[c])))).TrimEnd();
        // Right edge of the Total column, so subtotals line up under it.
        var totalEnd = 2 + widths.Take(6).Sum() + gap.Length * 5;

        var currencies = new List<string>();
        var grand = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var noPrice = 0;
        var sb = new StringBuilder();
        sb.AppendLine("BOM: " + _bom.DisplayName);
        foreach (var group in _bom.Lines.Select((l, i) => (Line: l, Cells: cells[i]))
                                        .GroupBy(x => x.Line.Supplier.Length > 0 ? x.Line.Supplier : "(no supplier)"))
        {
            sb.AppendLine();
            sb.AppendLine(group.Key);
            sb.AppendLine(LineOf(headers));
            var subtotal = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in group)
            {
                sb.AppendLine(LineOf(x.Cells));
                if (!x.Line.Include) continue;
                var price = ShownPrice(x.Line);
                if (!price.Unit.HasValue)
                {
                    noPrice++;
                    continue;
                }
                var cur = price.Currency.ToUpperInvariant();
                if (!currencies.Contains(cur)) currencies.Add(cur);
                var amount = price.Unit.Value * x.Line.Qty;
                subtotal[cur] = subtotal.GetValueOrDefault(cur) + amount;
                grand[cur] = grand.GetValueOrDefault(cur) + amount;
            }
            if (subtotal.Count > 0) sb.AppendLine(("Subtotal:  " + MoneyList(subtotal, currencies)).PadLeft(totalEnd));
        }
        sb.AppendLine();
        sb.AppendLine("Grand total: " + (grand.Count == 0 ? Money(0m, _convertTo ?? "", "0.00") : MoneyList(grand, currencies)) +
                      (noPrice > 0 ? $"   ({noPrice} line{(noPrice == 1 ? "" : "s")} without a price)" : ""));
        return sb.ToString();
    }

    // Shows the BOM's file in Finder (Explorer on Windows) - or, before it's saved, the last BOM folder (else Documents).
    private async void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = _filePath.Length > 0 && File.Exists(_filePath) ? _filePath : "";
            var folder = StartFolderPath();
            if (folder.Length == 0) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", file.Length > 0 ? $"/select,\"{file}\"" : $"\"{folder}\"");
            else if (file.Length > 0)
                Process.Start("open", new[] { "-R", file });
            else
                Process.Start("open", new[] { folder });
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Could not open the folder: " + ex.Message, Title ?? "");
        }
    }

    #endregion
}

/// <summary>One line of the BOM window's grid: a <see cref="BomLine"/> and the text shown for it.</summary>
public sealed class BomRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Action<BomRow> _includeChanged;

    public BomRow(BomLine line, Action<BomRow> includeChanged)
    {
        Line = line;
        _include = line.Include;
        _includeChanged = includeChanged;
    }

    public BomLine Line { get; }

    private bool _include;
    /// <summary>The tick box. Clicking it tells the window, which updates the line.</summary>
    public bool Include
    {
        get => _include;
        set
        {
            if (_include == value) return;
            _include = value;
            Changed(nameof(Include));
            Changed(nameof(IncludeTip));
            _includeChanged(this);
        }
    }

    public string IncludeTip => Include
        ? "Ticked: counted in the totals. Untick to leave this line out."
        : "Unticked: not counted in the totals. Tick to count this line.";

    public string Supplier { get; private set; } = "";
    public string PartNo { get; private set; } = "";
    public string Mpn { get; private set; } = "";
    public string Description { get; private set; } = "";
    /// <summary>Typed in the grid; the window checks it when the edit ends.</summary>
    public string Qty { get; set; } = "";
    public string UnitPrice { get; private set; } = "";
    public string Total { get; private set; } = "";
    public bool Converted { get; private set; }
    public string? InfoTip { get; private set; }
    public string? PriceTip { get; private set; }
    public string? LinkTip { get; private set; }
    public bool HasLink => Line.ProductUrl.Length > 0;

    public void Update(bool include, string supplier, string partNo, string mpn, string description, string qty,
                       string unitPrice, string total, bool converted, string? infoTip, string? priceTip, string? linkTip)
    {
        _include = include;
        Supplier = supplier;
        PartNo = partNo;
        Mpn = mpn;
        Description = description;
        Qty = qty;
        UnitPrice = unitPrice;
        Total = total;
        Converted = converted;
        InfoTip = infoTip;
        PriceTip = priceTip;
        LinkTip = linkTip;
        Changed(""); // everything
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
