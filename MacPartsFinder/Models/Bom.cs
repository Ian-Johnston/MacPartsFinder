using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MacPartsFinder;

/// <summary>One line of a BOM: a supplier's part and how many to buy. Prices are the supplier's own, as added.</summary>
public class BomLine
{
    /// <summary>Ticked: counted in the totals. Unticked lines are saved as DNP.</summary>
    public bool Include { get; set; } = true;
    /// <summary>Component references, e.g. "R1,R2" - only filled in by hand or by a loaded file.</summary>
    public string Refs { get; set; } = "";
    /// <summary>Package; saved in the Footprint column.</summary>
    public string Footprint { get; set; } = "";
    public int Qty { get; set; } = 1;
    public string Supplier { get; set; } = "";
    public string SupplierPartNumber { get; set; } = "";
    public string ManufacturerPartNumber { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Unit price at the lowest price break, in <see cref="Currency"/>.</summary>
    public decimal? UnitPrice { get; set; }
    public string Currency { get; set; } = "";
    public List<PriceBreak> PriceBreaks { get; set; } = new();
    public string ProductUrl { get; set; } = "";
    /// <summary>Stock when the part was added.</summary>
    public long? Stock { get; set; }
    public DateTime? DateAdded { get; set; }

    /// <summary>Unit price when buying <see cref="Qty"/>: the highest price break at or below it.</summary>
    public decimal? PriceEach()
    {
        if (PriceBreaks.Count == 0) return UnitPrice;
        var ordered = PriceBreaks.OrderBy(b => b.Quantity).ToList();
        var best = ordered[0];
        foreach (var b in ordered)
        {
            if (b.Quantity <= Qty) best = b;
        }
        return best.Price;
    }

    /// <summary>True when this is the same supplier part as <paramref name="other"/> (so adding it again adds to Qty).</summary>
    public bool SamePart(BomLine other) =>
        SupplierPartNumber.Length > 0 &&
        string.Equals(Supplier, other.Supplier, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SupplierPartNumber, other.SupplierPartNumber, StringComparison.OrdinalIgnoreCase);

    /// <summary>"1:0.2;10:0.139" - the price breaks as numbers.</summary>
    public string BreaksRaw() =>
        string.Join(";", PriceBreaks.OrderBy(b => b.Quantity)
            .Select(b => b.Quantity.ToString(CultureInfo.InvariantCulture) + ":" + b.Price.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Reads "1:0.2;10:0.139"; anything that isn't a number pair is skipped.</summary>
    public static List<PriceBreak> ParseBreaks(string? raw)
    {
        var list = new List<PriceBreak>();
        foreach (var pair in (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = pair.Split(':');
            if (bits.Length == 2 &&
                int.TryParse(bits[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) &&
                decimal.TryParse(bits[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p))
            {
                list.Add(new PriceBreak(q, p));
            }
        }
        return list;
    }
}

/// <summary>
/// A bill of materials built up across searches, saved as CSV - the same file as WinPartsFinder's, so either app opens it.
/// The file starts with the usual BOM columns (Refs, Value, Footprint, Qty, DNP) followed by the supplier columns, so
/// other BOM CSVs with those columns load too. The BOM's name and notes go after the parts, below a blank line,
/// as "#BOM Name" and "#Notes" rows.
/// </summary>
public class Bom
{
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<BomLine> Lines { get; set; } = new();

    public const string UntitledName = "Untitled BOM";

    /// <summary>The name, or "Untitled BOM" when it hasn't been named.</summary>
    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : UntitledName;

    private const string NameTag = "#BOM Name";
    private const string NotesTag = "#Notes";

    private static readonly string[] Headers =
    {
        "Refs", "Value", "Footprint", "Qty", "DNP", "Supplier", "Supplier Part No", "MPN", "Manufacturer", "Description",
        "Unit Price", "Currency", "Price Breaks", "Product URL", "Stock", "Date Added"
    };

    /// <summary>
    /// Adds a line, keeping each supplier's lines together. If the same supplier part is already there,
    /// its quantity goes up instead. Returns the line that holds the part.
    /// </summary>
    public BomLine Add(BomLine line)
    {
        var existing = Lines.FirstOrDefault(l => l.SamePart(line));
        if (existing != null)
        {
            existing.Qty += line.Qty;
            return existing;
        }
        var lastOfSupplier = Lines.FindLastIndex(l => string.Equals(l.Supplier, line.Supplier, StringComparison.OrdinalIgnoreCase));
        Lines.Insert(lastOfSupplier >= 0 ? lastOfSupplier + 1 : Lines.Count, line);
        return line;
    }

    public void Save(string path)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers.Select(Quote)));
        foreach (var l in Lines)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                l.Refs, l.ManufacturerPartNumber, l.Footprint, l.Qty.ToString(inv), l.Include ? "" : "DNP",
                l.Supplier, l.SupplierPartNumber, l.ManufacturerPartNumber, l.Manufacturer, l.Description,
                l.UnitPrice?.ToString(inv) ?? "", l.Currency, l.BreaksRaw(), l.ProductUrl,
                l.Stock?.ToString(inv) ?? "",
                l.DateAdded?.ToString("yyyy-MM-dd", inv) ?? ""
            }.Select(Quote)));
        }
        sb.AppendLine();
        sb.AppendLine(Quote(NameTag) + "," + Quote(Name));
        sb.AppendLine(Quote(NotesTag) + "," + Quote(Notes));
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM so Excel shows £ properly
    }

    /// <summary>Reads a BOM saved by MacPartsFinder or WinPartsFinder, or another BOM CSV (any columns it doesn't have stay blank).</summary>
    public static Bom Load(string path)
    {
        var inv = CultureInfo.InvariantCulture;
        var records = ReadCsv(File.ReadAllText(path));
        var bom = new Bom();
        var header = records.FirstOrDefault(r => r.Any(f => f.Trim().Length > 0));
        if (header == null) return bom;

        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var h = header[i].Trim();
            if (h.Length > 0 && !cols.ContainsKey(h)) cols[h] = i;
        }

        foreach (var r in records.Skip(records.IndexOf(header) + 1))
        {
            if (r.All(f => f.Trim().Length == 0)) continue;
            var first = r[0].Trim();
            if (first.Equals(NameTag, StringComparison.OrdinalIgnoreCase))
            {
                bom.Name = r.Count > 1 ? r[1].Trim() : "";
                continue;
            }
            if (first.Equals(NotesTag, StringComparison.OrdinalIgnoreCase))
            {
                bom.Notes = r.Count > 1 ? r[1] : "";
                continue;
            }

            var l = new BomLine
            {
                Refs = Cell(r, cols, "Refs", "Reference", "References", "Designator"),
                Footprint = Cell(r, cols, "Footprint", "Package"),
                Include = Cell(r, cols, "DNP", "Do not populate").Length == 0,
                Supplier = Cell(r, cols, "Supplier"),
                SupplierPartNumber = Cell(r, cols, "Supplier Part No"),
                ManufacturerPartNumber = Cell(r, cols, "MPN", "Manufacturer Part No", "Value"),
                Manufacturer = Cell(r, cols, "Manufacturer"),
                Description = Cell(r, cols, "Description"),
                Currency = Cell(r, cols, "Currency"),
                PriceBreaks = BomLine.ParseBreaks(Cell(r, cols, "Price Breaks")),
                ProductUrl = Cell(r, cols, "Product URL")
            };
            if (int.TryParse(Cell(r, cols, "Qty", "Quantity", "Quantity Per PCB"), NumberStyles.Integer, inv, out var q) && q > 0) l.Qty = q;
            if (decimal.TryParse(Cell(r, cols, "Unit Price"), NumberStyles.Number, inv, out var price)) l.UnitPrice = price;
            if (long.TryParse(Cell(r, cols, "Stock"), NumberStyles.Integer, inv, out var stock)) l.Stock = stock;
            if (DateTime.TryParseExact(Cell(r, cols, "Date Added"), "yyyy-MM-dd", inv, DateTimeStyles.None, out var added)) l.DateAdded = added;
            bom.Lines.Add(l);
        }
        return bom;
    }

    /// <summary>The first of <paramref name="names"/> that is a column of the file (other common column names are accepted too); "" if none.</summary>
    private static string Cell(List<string> r, Dictionary<string, int> cols, params string[] names)
    {
        foreach (var n in names)
        {
            if (cols.TryGetValue(n, out var i) && i < r.Count) return r[i].Trim();
        }
        return "";
    }

    private static string Quote(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    /// <summary>Splits CSV text into records of fields. Quoted fields may hold commas, quotes ("") and line breaks.</summary>
    private static List<List<string>> ReadCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = new List<string>();
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }
}
