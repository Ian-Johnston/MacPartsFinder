using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace MacPartsFinder;

/// <summary>Small always-on-top pop-up showing a bigger product photo. Closes on click, Esc or its close button.</summary>
public partial class ImagePopupWindow : Window
{
    private const int MaxPhotoSide = 600;
    private const int MinPhotoWidth = 260;

    private readonly List<string> _urls;
    private Bitmap? _photo;

    // Needed by the XAML loader / previewer.
    public ImagePopupWindow() : this("", "", "Photo", "")
    {
    }

    /// <param name="largeUrl">Preferred (bigger) image.</param>
    /// <param name="fallbackUrl">Used if the large one can't be loaded.</param>
    public ImagePopupWindow(string largeUrl, string fallbackUrl, string title, string caption)
    {
        InitializeComponent();
        _urls = new[] { largeUrl, fallbackUrl }.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        Title = title;
        lblCaption.Text = caption;
        lblCaption.IsVisible = caption.Length > 0;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Opened += async (_, _) =>
        {
            KeepOnScreen();
            Bitmap? img = null;
            foreach (var url in _urls)
            {
                try
                {
                    img = await ImageCache.DownloadImageAsync(url, CancellationToken.None);
                    break;
                }
                catch
                {
                    // try the next one
                }
            }
            if (!IsVisible)
            {
                img?.Dispose();
                return;
            }
            if (img == null)
            {
                lblLoading.Text = "Photo not available";
                return;
            }

            _photo = img;
            picPhoto.Source = img;
            picPhoto.IsVisible = true;
            lblLoading.IsVisible = false;

            // Size the window to the photo (shrinking big ones), then make sure it still fits on screen.
            var size = img.Size; // in screen points
            var scale = Math.Min(1.0, MaxPhotoSide / Math.Max(size.Width, size.Height));
            picPhoto.Width = size.Width * scale;
            picPhoto.Height = size.Height * scale;
            Width = Math.Max(MinPhotoWidth, size.Width * scale);
            Height = double.NaN;
            SizeToContent = SizeToContent.Height;
            KeepOnScreen();
        };
        Closed += (_, _) => _photo?.Dispose();
    }

    private void Any_Tapped(object? sender, TappedEventArgs e)
    {
        Close();
    }

    private void KeepOnScreen()
    {
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var w = (int)(Bounds.Width * screen.Scaling);
        var h = (int)(Bounds.Height * screen.Scaling);
        Position = new PixelPoint(Math.Max(area.X, Math.Min(Position.X, area.Right - w)),
                                  Math.Max(area.Y, Math.Min(Position.Y, area.Bottom - h)));
    }
}
