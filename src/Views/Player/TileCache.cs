// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Media.Imaging;
using SteamPoweredPlayer.Core.Library;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SteamPoweredPlayer.Views.Player;

/// <summary>
/// Decodes single trickplay tiles out of the JPEG sprite sheets (WIC crop, so a 160x90 tile costs 57 KB of RAM, not a whole decoded
/// sheet). The decoded tiles are cached (bounded) and may be shown by several <c>Image</c> elements. Call from the UI thread: they are XAML objects.
/// </summary>
/// <remarks>
/// Tiles are <see cref="WriteableBitmap"/>s, which own their pixels. With a <c>SoftwareBitmapSource</c>, which depends on the
/// <see cref="SoftwareBitmap"/> it was built from, resizing the window on a long session failed fast in XAML (0xC000027B, 4 of 5 runs of
/// the uicheck's resize stress; WER showed RO_E_CLOSED). With WriteableBitmap: 0 of 8. Resizing asks for new tiles, and short clips have too few to fail.
/// </remarks>
internal sealed class TileCache
{
    private const int MaxTiles = 300;
    private readonly Dictionary<(string Path, int X, int Y), WriteableBitmap> _cache = [];

    /// <summary>The tile's image, or null when the sheet is unreadable (being rewritten, trimmed away): the caller shows nothing.</summary>
    public async Task<WriteableBitmap?> GetAsync(TrickplayTile tile)
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
            using var decoded = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var image = new WriteableBitmap(decoded.PixelWidth, decoded.PixelHeight);
            decoded.CopyToBuffer(image.PixelBuffer);
            image.Invalidate();
            if (_cache.Count >= MaxTiles) _cache.Clear();      // safe: an Image still showing a tile keeps it alive
            _cache[key] = image;
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null;
        }
    }
}
