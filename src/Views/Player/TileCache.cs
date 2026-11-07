// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Media.Imaging;
using SteamPoweredPlayer.Core.Library;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SteamPoweredPlayer.Views.Player;

/// <summary>
/// Decodes single trickplay tiles out of the JPEG sprite sheets (WIC crop, so a 160x90 tile costs 57 KB of RAM, not a whole decoded
/// sheet). The decoded <see cref="SoftwareBitmap"/>s are cached (bounded); every request gets its own <see cref="SoftwareBitmapSource"/>.
/// Call from the UI thread: the source is a XAML object.
/// </summary>
/// <remarks>
/// Two rules learned the hard way: a <see cref="SoftwareBitmap"/> must not be disposed while a source built from it can still be shown
/// (XAML re-reads it when an <c>Image</c> is realised again, and a closed bitmap fails fast with RO_E_CLOSED), and a source is not
/// shared between <c>Image</c> elements. So bitmaps are kept alive (the GC releases them when evicted) and sources are created per use.
/// </remarks>
internal sealed class TileCache
{
    private const int MaxTiles = 300;
    private readonly Dictionary<(string Path, int X, int Y), SoftwareBitmap> _cache = [];

    /// <summary>The tile's image, or null when the sheet is unreadable (being rewritten, trimmed away): the caller shows nothing.</summary>
    public async Task<SoftwareBitmapSource?> GetAsync(TrickplayTile tile)
    {
        var bitmap = await GetBitmapAsync(tile);
        if (bitmap is null) return null;
        try
        {
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            return source;
        }
        catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private async Task<SoftwareBitmap?> GetBitmapAsync(TrickplayTile tile)
    {
        var key = (tile.SheetPath, tile.X, tile.Y);
        if (_cache.TryGetValue(key, out var hit)) return hit;
        try
        {
            byte[] bytes;
            // The generator rewrites the open sheet by rename, but never lock it against a reader.
            await using (var fs = new FileStream(tile.SheetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous))
            {
                bytes = new byte[fs.Length];
                await fs.ReadExactlyAsync(bytes).ConfigureAwait(true);
            }
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if ((uint)tile.X >= decoder.PixelWidth || (uint)tile.Y >= decoder.PixelHeight) return null;
            var w = Math.Min((uint)tile.Width, decoder.PixelWidth - (uint)tile.X);
            var h = Math.Min((uint)(tile.Height > 0 ? tile.Height : (int)decoder.PixelHeight), decoder.PixelHeight - (uint)tile.Y);
            if (w == 0 || h == 0) return null;
            var transform = new BitmapTransform { Bounds = new BitmapBounds { X = (uint)tile.X, Y = (uint)tile.Y, Width = w, Height = h } };
            var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            if (_cache.Count >= MaxTiles) _cache.Clear();      // not disposed: see the remarks
            _cache[key] = bitmap;
            return bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null;
        }
    }
}
