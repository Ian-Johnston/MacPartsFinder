using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MacPartsFinder;

/// <summary>One line of the supplier list on the left: tick box, name, mode and how its search went.</summary>
public sealed class SupplierRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public SupplierRow(Supplier supplier, bool isChecked)
    {
        Supplier = supplier;
        _isChecked = isChecked;
    }

    public Supplier Supplier { get; }
    public string Name => Supplier.Name;
    public string ModeText => Supplier.ModeText;

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _hits = "";
    public string Hits { get => _hits; set => Set(ref _hits, value); }

    private string _time = "";
    public string Time { get => _time; set => Set(ref _time, value); }

    private string _tip = "";
    /// <summary>Tooltip: how the search went, or the error message.</summary>
    public string Tip { get => _tip; set => Set(ref _tip, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
