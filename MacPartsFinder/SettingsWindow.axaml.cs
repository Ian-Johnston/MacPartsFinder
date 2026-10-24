using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace MacPartsFinder;

/// <summary>One line of the search-URL list in Settings.</summary>
public sealed class UrlRow
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Built-in suppliers can be edited but not renamed or deleted.</summary>
    public bool IsCustom { get; init; }
}

/// <summary>The Settings window. Closes with true when OK was clicked (the settings object has then been updated).</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<UrlRow> _urls = new();

    /// <summary>True if the user asked to put the main window back to its original size and position.</summary>
    public bool ResetWindowRequested => !btnResetWindow.IsEnabled;

    // Needed by the XAML loader / previewer.
    public SettingsWindow() : this(new AppSettings())
    {
    }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        cboDkSite.ItemsSource = new[] { "UK", "US", "IE", "DE", "FR", "NL", "CA", "AU" };
        cboDkCurrency.ItemsSource = new[] { "GBP", "EUR", "USD", "CAD", "AUD" };

        txtMouserKey.Text = settings.MouserApiKey;
        txtDkClientId.Text = settings.DigiKeyClientId;
        txtDkSecret.Text = settings.DigiKeyClientSecret;
        cboDkSite.Text = settings.DigiKeySite;
        cboDkCurrency.Text = settings.DigiKeyCurrency;
        txtE14Key.Text = settings.Element14ApiKey;
        txtTmeToken.Text = settings.TmeToken;
        txtTmeSecret.Text = settings.TmeSecret;
        chkShowThumbnails.IsChecked = settings.ShowThumbnails;
        chkDarkMode.IsChecked = settings.DarkMode;
        gridUrls.ItemsSource = _urls;
        LoadUrls(settings.GetSearchUrl, settings.CustomSuppliers);

        // (Esc is the Cancel button.)
        Closed += (_, _) =>
        {
            _apiHelp?.Close();
            if (!_saved) ApplyTheme(_settings.DarkMode); // undo the Dark mode preview
        };
    }

    private bool _saved;

    private void LoadUrls(Func<string, string> getUrl, IEnumerable<CustomSupplier> customs)
    {
        _urls.Clear();
        foreach (var supplierName in AppSettings.SupplierOrder)
        {
            _urls.Add(new UrlRow { Name = supplierName, Url = getUrl(supplierName) });
        }
        foreach (var c in customs)
        {
            _urls.Add(new UrlRow { Name = c.Name, Url = c.SearchUrl, IsCustom = true });
        }
        UpdateDeleteButton();
    }

    private List<CustomSupplier> CurrentCustoms()
    {
        return _urls.Where(r => r.IsCustom)
                    .Select(r => new CustomSupplier { Name = r.Name.Trim(), SearchUrl = r.Url.Trim() }).ToList();
    }

    /// <summary>Built-in suppliers' names can't be changed.</summary>
    private void GridUrls_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Column.DisplayIndex == 0 && e.Row.DataContext is UrlRow { IsCustom: false }) e.Cancel = true;
    }

    private void GridUrls_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateDeleteButton();

    private void UpdateDeleteButton()
    {
        btnDeleteSupplier.IsEnabled = gridUrls.SelectedItem is UrlRow { IsCustom: true };
    }

    private void BtnAddSupplier_Click(object? sender, RoutedEventArgs e)
    {
        gridUrls.CommitEdit();
        var existing = _urls.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = "New supplier";
        var n = 2;
        while (existing.Contains(name))
        {
            name = $"New supplier {n}";
            n++;
        }
        var row = new UrlRow { Name = name, Url = "https://www.example.com/search?q={0}", IsCustom = true };
        _urls.Add(row);
        gridUrls.SelectedItem = row;
        gridUrls.ScrollIntoView(row, gridUrls.Columns[0]);
        gridUrls.CurrentColumn = gridUrls.Columns[0];
        gridUrls.BeginEdit();
    }

    private void BtnDeleteSupplier_Click(object? sender, RoutedEventArgs e)
    {
        if (gridUrls.SelectedItem is not UrlRow { IsCustom: true } row) return;
        gridUrls.CancelEdit();
        _urls.Remove(row);
        UpdateDeleteButton();
    }

    private async System.Threading.Tasks.Task<bool> ValidateUrls()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _urls)
        {
            var name = (row.Name ?? "").Trim();
            var url = (row.Url ?? "").Trim();
            string? problem = null;

            if (name.Length == 0)
                problem = "Every supplier needs a name.";
            else if (!seen.Add(name))
                problem = $"There are two suppliers called \"{name}\".";
            else if (row.IsCustom && url.Length == 0)
                problem = $"Enter a search URL for {name}.";
            else if (url.Length > 0 && !url.Contains("{0}"))
                problem = $"The search URL for {name} must contain {{0}} where the search text goes.";
            else if (url.Length > 0 && !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                problem = $"The search URL for {name} must start with https:// (or http://).";

            if (problem != null)
            {
                await MessageBox.Show(this, problem, Title ?? "");
                gridUrls.SelectedItem = row;
                gridUrls.ScrollIntoView(row, null);
                return false;
            }
        }
        return true;
    }

    private async void BtnOK_Click(object? sender, RoutedEventArgs e)
    {
        gridUrls.CommitEdit();
        if (!await ValidateUrls()) return;

        _settings.MouserApiKey = (txtMouserKey.Text ?? "").Trim();
        _settings.DigiKeyClientId = (txtDkClientId.Text ?? "").Trim();
        _settings.DigiKeyClientSecret = (txtDkSecret.Text ?? "").Trim();
        _settings.DigiKeySite = (cboDkSite.Text ?? "").Trim().ToUpperInvariant();
        _settings.DigiKeyCurrency = (cboDkCurrency.Text ?? "").Trim().ToUpperInvariant();
        _settings.Element14ApiKey = (txtE14Key.Text ?? "").Trim();
        _settings.TmeToken = (txtTmeToken.Text ?? "").Trim();
        _settings.TmeSecret = (txtTmeSecret.Text ?? "").Trim();
        _settings.ShowThumbnails = chkShowThumbnails.IsChecked == true;
        _settings.DarkMode = chkDarkMode.IsChecked == true;

        _settings.SearchUrls.Clear();
        foreach (var row in _urls.Where(r => !r.IsCustom))
        {
            var url = (row.Url ?? "").Trim();
            // Only store overrides, so future default changes still apply.
            if (url.Length > 0 && url != AppSettings.DefaultSearchUrls[row.Name]) _settings.SearchUrls[row.Name] = url;
        }
        _settings.CustomSuppliers = CurrentCustoms();

        _saved = true;
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void BtnResetUrls_Click(object? sender, RoutedEventArgs e)
    {
        // Resets the built-in URLs only; your own suppliers are kept.
        gridUrls.CommitEdit();
        LoadUrls(name => AppSettings.DefaultSearchUrls[name], CurrentCustoms());
    }

    private void BtnResetWindow_Click(object? sender, RoutedEventArgs e)
    {
        // Takes effect on OK, like everything else here.
        btnResetWindow.IsEnabled = false;
        btnResetWindow.Content = "Layout resets on OK";
    }

    private void ChkShowKeys_Changed(object? sender, RoutedEventArgs e)
    {
        var mask = chkShowKeys.IsChecked == true ? '\0' : '•';
        foreach (var tb in new[] { txtMouserKey, txtDkClientId, txtDkSecret, txtE14Key, txtTmeToken, txtTmeSecret })
        {
            tb.PasswordChar = mask;
        }
    }

    private void ChkDarkMode_Changed(object? sender, RoutedEventArgs e)
    {
        // Live preview (the whole app follows); Cancel puts it back.
        if (IsLoaded) ApplyTheme(chkDarkMode.IsChecked == true);
    }

    public static void ApplyTheme(bool dark)
    {
        if (Application.Current != null) Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    // API Help isn't modal: it's owned by this window, so the key boxes stay usable while it's open,
    // and it closes along with Settings. One at a time.
    private HelpWindow? _apiHelp;

    private void BtnApiHelp_Click(object? sender, RoutedEventArgs e)
    {
        if (_apiHelp != null)
        {
            _apiHelp.Activate();
            return;
        }
        _apiHelp = new HelpWindow(apiHelp: true) { WindowStartupLocation = WindowStartupLocation.Manual };
        _apiHelp.Closed += (_, _) => _apiHelp = null;
        // Beside Settings (right if there's room, else left) so it doesn't cover the key boxes.
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var w = (int)(_apiHelp.Width * scale);
            var right = Position.X + (int)(Bounds.Width * scale);
            var x = right + w <= area.Right ? right : Math.Max(area.X, Position.X - w);
            _apiHelp.Position = new PixelPoint(x, Math.Max(area.Y, Position.Y));
            _apiHelp.Height = Math.Max(_apiHelp.MinHeight, Math.Min(Bounds.Height, area.Height / scale));
        }
        _apiHelp.Show(this);
    }

    private void KeyLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url }) WebHelper.OpenInBrowser(url);
    }
}
