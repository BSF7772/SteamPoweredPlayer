// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamPoweredPlayer.Core.Library;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SteamPoweredPlayer.Services;

/// <summary>Cuts one tile out of a trickplay sheet (a JPEG written by Core) for use as an image. Call from the UI thread; decoding itself is asynchronous.</summary>
public static class ThumbnailLoader
{
    private static readonly SemaphoreSlim Gate = new(2);

    /// <summary>The thumbnail of the recording's first chunk (the grid card), or null when there is none or the sheet can't be read right now.</summary>
    public static async Task<ImageSource?> FirstTileAsync(TrickplayIndex index)
    {
        if (index.Chunks.Count == 0 || index.FindChunk(index.Chunks[0]) is not { } tile) return null;
        return await LoadAsync(tile).ConfigureAwait(true);
    }

    public static async Task<ImageSource?> LoadAsync(TrickplayTile tile)
    {
        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            using var stream = await FileRandomAccessStream.OpenAsync(tile.SheetPath, Windows.Storage.FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (tile.X + tile.Width > decoder.PixelWidth || tile.Y + tile.Height > decoder.PixelHeight) return null;
            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds { X = (uint)tile.X, Y = (uint)tile.Y, Width = (uint)tile.Width, Height = (uint)tile.Height },
            };
            // Never dispose this bitmap: XAML re-reads it whenever the Image is realised again (the Library page restored by Back from
            // the Player), and a closed SoftwareBitmap fails fast there with RO_E_CLOSED (0xC000027B). The GC releases it with the source.
            var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            return source;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null;   // the sheet is being replaced or was never complete: the placeholder stays
        }
        finally { Gate.Release(); }
    }
}
