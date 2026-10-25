using Avalonia;
using Avalonia.Styling;

namespace MacPartsFinder;

/// <summary>The colour themes the user can pick in Settings.</summary>
public enum ThemeKind
{
    Light,    // the normal look
    SoftGrey, // light, with the glare taken off
    DarkGrey, // a softer, slate-grey dark
    Dark
}

/// <summary>
/// The four themes, as Avalonia theme variants. Light and Dark are Avalonia's own; Soft grey and Dark grey
/// are built on them, with their own colour palettes (in App.axaml) - the same colours as WinPartsFinder's.
/// </summary>
public static class AppThemes
{
    /// <summary>The names shown in Settings, in ThemeKind order.</summary>
    public static readonly string[] Names = { "Light", "Soft grey", "Dark grey", "Dark" };

    public static readonly ThemeVariant SoftGrey = new("SoftGrey", ThemeVariant.Light);
    public static readonly ThemeVariant DarkGrey = new("DarkGrey", ThemeVariant.Dark);

    public static ThemeVariant Variant(ThemeKind kind) => kind switch
    {
        ThemeKind.SoftGrey => SoftGrey,
        ThemeKind.DarkGrey => DarkGrey,
        ThemeKind.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Light
    };

    /// <summary>Switches the whole app (every open window) to this theme.</summary>
    public static void Apply(ThemeKind kind)
    {
        if (Application.Current != null) Application.Current.RequestedThemeVariant = Variant(kind);
    }
}
