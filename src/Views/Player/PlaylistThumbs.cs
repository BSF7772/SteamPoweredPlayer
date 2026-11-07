// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace SteamPoweredPlayer.Views.Player;

/// <summary>Thumbnails of the playlist (F7): Steam's own <c>thumbnail.jpg</c> next to a saved clip's <c>clip.pb</c>.</summary>
internal static class PlaylistThumbs
{
    private const long MaxBytes = 4 << 20;

    /// <summary>
    /// The bytes of the clip's <c>thumbnail.jpg</c>, or null (a cache session, no file, unreadable). The clip folder is the ancestor of the
    /// media folder (<c>…/clip_*/video/bg_*</c>) named like the recording's key. Read-only with delete sharing: never in Steam's way.
    /// </summary>
    public static byte[]? ReadSteamThumbnail(string relativeFolder, string? mediaFolder)
    {
        if (string.IsNullOrEmpty(mediaFolder)) return null;
        var name = relativeFolder.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
        var dir = new DirectoryInfo(mediaFolder);
        for (var i = 0; i < 4 && dir is not null && !string.Equals(dir.Name, name, StringComparison.OrdinalIgnoreCase); i++) dir = dir.Parent;
        if (dir is null || !string.Equals(dir.Name, name, StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(dir.FullName, "thumbnail.jpg");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length is 0 or > MaxBytes) return null;
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Decodes at <paramref name="decodeWidth"/> device pixels. <c>KeepAlive</c> is the stream behind the image: hold it as long as the
    /// image can be shown (a closed stream or bitmap read again by XAML fails fast with RO_E_CLOSED).
    /// </summary>
    public static async Task<(ImageSource Source, object KeepAlive)?> DecodeAsync(byte[] jpg, int decodeWidth)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(jpg));
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Physical };
            await image.SetSourceAsync(stream);
            return (image, stream);
        }
        catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
    }
}
