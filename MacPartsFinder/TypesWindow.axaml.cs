using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MacPartsFinder;

/// <summary>One line in the Types… list.</summary>
public sealed class TypeEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; init; } = ""; // "" = no type
    public int Count { get; init; }
    public List<string> Suppliers { get; init; } = new();

    public string DisplayName => Name.Length == 0 ? "(no type)" : Name;
    public string SuppliersText => string.Join(", ", Suppliers);

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

/// <summary>
/// The Types… window: every part type in the current results, with tick boxes.
/// Closes with the unticked types ("" = parts with no type), or null for Cancel.
/// </summary>
public partial class TypesWindow : Window
{
    private readonly List<TypeEntry> _types;

    // Needed by the XAML loader / previewer.
    public TypesWindow() : this(new List<TypeEntry>(), new HashSet<string>())
    {
    }

    public TypesWindow(List<TypeEntry> types, HashSet<string> hidden)
    {
        InitializeComponent();
        _types = types;
        foreach (var t in types)
        {
            t.IsChecked = !hidden.Contains(t.Name);
            t.PropertyChanged += (_, _) => UpdateTitle();
        }
        gridTypes.ItemsSource = types;
        UpdateTitle();
    }

    /// <summary>"Part types - 12 of 15 shown (140 of 163 parts)".</summary>
    private void UpdateTitle()
    {
        var ticked = _types.Where(t => t.IsChecked).ToList();
        var parts = _types.Sum(t => t.Count);
        var shown = ticked.Sum(t => t.Count);
        Title = $"Part types - {ticked.Count} of {_types.Count} shown ({shown:#,##0} of {parts:#,##0} parts)";
    }

    private void BtnAll_Click(object? sender, RoutedEventArgs e) => SetAll(true);

    private void BtnNone_Click(object? sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool ticked)
    {
        foreach (var t in _types)
        {
            t.IsChecked = ticked;
        }
    }

    private void BtnOK_Click(object? sender, RoutedEventArgs e)
    {
        Close(_types.Where(t => !t.IsChecked).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
