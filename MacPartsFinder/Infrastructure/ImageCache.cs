using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace MacPartsFinder;

/// <summary>
/// Downloads product photos and keeps small thumbnails in memory for the session.
/// GetThumbnail must be called on the UI thread; ThumbnailReady is raised there too.
/// </summary>
public sealed class ImageCache
{
    // Thumbnail size (screen points). The photo column width and the results row height follow it.
    public const int ThumbSize = 35;
    // Thumbnails are kept at twice that in pixels, so they're sharp on Retina screens.
    private const int ThumbPixels = (ThumbSize - 2) * 2;

    // Be polite to the image servers: a few downloads at a time.
    private static readonly SemaphoreSlim _downloadGate = new(4, 4);

    private readonly Dictionary<string, Bitmap> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on the UI thread) with the URL whenever a thumbnail finishes loading or fails.</summary>
    public event Action<string>? ThumbnailReady;

    /// <summary>The thumbnail if it's ready; otherwise null, with <paramref name="failed"/> saying whether it never will be.
    /// Starts the download the first time it's asked for.</summary>
    public Bitmap? GetThumbnail(string url, out bool failed)
    {
        failed = string.IsNullOrEmpty(url) || _failed.Contains(url);
        if (failed) return null;
        if (_thumbs.TryGetValue(url, out var img)) return img;
        if (_pending.Add(url)) LoadThumbnail(url);
        return null;
    }

    public bool HasFailed(string url) => _failed.Contains(url);

    private async void LoadThumbnail(string url)
    {
        Bitmap? thumb = null;
        try
        {
            using var full = await DownloadImageAsync(url, CancellationToken.None);
            thumb = MakeThumbnail(full);
        }
        catch
        {
            // Bad URL, 404, not an image... just show the "no image" box.
        }
        _pending.Remove(url);
        if (thumb == null) _failed.Add(url); else _thumbs[url] = thumb;
        ThumbnailReady?.Invoke(url);
    }

    /// <summary>Downloads an image. Throws if the server sends something that isn't an image.</summary>
    public static async Task<Bitmap> DownloadImageAsync(string url, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Common formats. "image/*" makes some servers (Farnell) send WebP - which would also be fine here.
            req.Headers.Accept.ParseAdd("image/jpeg, image/png, image/gif, image/bmp");
            using var resp = await WebHelper.Client.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Not an image ({mediaType}).");
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>Scales the photo to fit the thumbnail box, keeping its shape.</summary>
    private static Bitmap MakeThumbnail(Bitmap source)
    {
        var size = source.PixelSize;
        var scale = Math.Min((double)ThumbPixels / size.Width, (double)ThumbPixels / size.Height);
        if (scale >= 1) scale = 1; // small photos stay as they are
        var w = Math.Max(1, (int)Math.Round(size.Width * scale));
        var h = Math.Max(1, (int)Math.Round(size.Height * scale));
        return source.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.HighQuality);
    }
}
