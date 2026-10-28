using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace MacPartsFinder;

/// <summary>
/// Dark grey's title bar: the window's own slate (#2C3038) rather than the system's black/neutral dark one
/// (as WinPartsFinder's Theme.vb). Every other theme keeps the normal title bar. App applies it to each window.
/// </summary>
public static class TitleBar
{
    private const byte SlateR = 0x2C, SlateG = 0x30, SlateB = 0x38;

    /// <summary>Colours this window's title bar for the current theme.</summary>
    public static void Apply(Window window)
    {
        var slate = Application.Current?.ActualThemeVariant == AppThemes.DarkGrey;
        var handle = window.TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero) return;
        try
        {
            if (OperatingSystem.IsWindows()) ApplyWindows(handle.Handle, slate);
            else if (OperatingSystem.IsMacOS()) ApplyMac(handle.Handle, slate);
        }
        catch
        {
            // older system: the title bar just keeps its normal colour
        }
    }

    // ---- Windows: the caption colour (Windows 11 only - Windows 10 ignores it and keeps the black one) ----
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyWindows(IntPtr hwnd, bool slate)
    {
        var colour = slate ? (SlateB << 16) | (SlateG << 8) | SlateR : DWMWA_COLOR_DEFAULT; // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colour, 4);
    }

    // ---- macOS: a transparent title bar shows the NSWindow's background colour, so set that to slate ----
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendId(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendId(IntPtr receiver, IntPtr selector, IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendRgba(IntPtr receiver, IntPtr selector, double r, double g, double b, double a);

    private static void ApplyMac(IntPtr nsWindow, bool slate)
    {
        var nsColor = objc_getClass("NSColor");
        var colour = slate
            ? SendRgba(nsColor, sel_registerName("colorWithSRGBRed:green:blue:alpha:"), SlateR / 255.0, SlateG / 255.0, SlateB / 255.0, 1.0)
            : SendId(nsColor, sel_registerName("windowBackgroundColor"));
        SendBool(nsWindow, sel_registerName("setTitlebarAppearsTransparent:"), slate);
        SendId(nsWindow, sel_registerName("setBackgroundColor:"), colour);
    }
}
