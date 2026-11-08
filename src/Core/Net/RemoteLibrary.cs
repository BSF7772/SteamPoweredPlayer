// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Net;

public enum LinkKind { Unknown, Local, Wired, Wireless }

/// <summary>Which kind of link reaches a library. Download-first applies to every kind until wired streaming is proven (spikes S3b, S9).</summary>
public static class LinkDetector
{
    public static LinkKind Classify(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Wman or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => LinkKind.Wireless,
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.TokenRing => LinkKind.Wired,
        _ => LinkKind.Unknown,                          // VPN adapters, tunnels: what's underneath is unknown here
    };

    /// <summary>The host of a UNC path, or null for a local path.</summary>
    public static string? HostOf(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc ? uri.Host : null;

    /// <summary>
    /// Asks the OS which interface routes to the library's host: connecting a UDP socket picks the route and sends nothing. Any failure
    /// gives <see cref="LinkKind.Unknown"/>.
    /// </summary>
    public static async Task<LinkKind> DetectAsync(string path, CancellationToken ct)
    {
        if (HostOf(path) is not { } host) return LinkKind.Local;
        try
        {
            var address = (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false)).FirstOrDefault();
            if (address is null) return LinkKind.Unknown;
            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(address, 445));
            if (socket.LocalEndPoint is not IPEndPoint local) return LinkKind.Unknown;
            if (IPAddress.IsLoopback(local.Address)) return LinkKind.Local;
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(local.Address)));
            return nic is null ? LinkKind.Unknown : Classify(nic.NetworkInterfaceType);
        }
        catch (Exception e) when (e is SocketException or NetworkInformationException or ArgumentException)
        {
            return LinkKind.Unknown;
        }
    }
}

/// <summary>One recording found in a remote library.</summary>
public sealed record RemoteRecording(string Folder, RecordingName Name, SessionMedia Media);

/// <summary>Lists a remote library by scanning its files, read-only; it also works where no index exists yet.</summary>
public static class RemoteLibrary
{
    /// <summary>A stable, plain cache folder name for a library path. Says nothing about the path itself.</summary>
    public static string IdFor(string root)
    {
        var normal = Path.TrimEndingDirectorySeparator(root).ToUpperInvariant();
        return "net-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normal)))[..12];
    }

    /// <summary>
    /// The recording's markers, read from the library. They need the library's timeline files, so offline the answer is empty after
    /// <paramref name="timeoutSeconds"/>: markers must never hold up playing something already on this PC.
    /// </summary>
    public static async Task<IReadOnlyList<Marker>> TryLoadMarkersAsync(string root, RemoteRecording recording, double timeoutSeconds = 4)
    {
        var relative = (recording.Name.Kind == RecordingKind.CacheSession ? "video" : "clips") + "/" + recording.Folder;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await Task.Run(async () =>
            {
                var builder = new LibraryBuilder(root);
                if (builder.FolderFor(relative) is not { } folder) return (IReadOnlyList<Marker>)[];
                var context = await builder.LoadContextAsync(cts.Token).ConfigureAwait(false);
                return (await builder.LoadMarkersAsync(folder, context, cts.Token).ConfigureAwait(false)).Markers;
            }, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <exception cref="IOException">The library can't be reached.</exception>
    public static async Task<IReadOnlyList<RemoteRecording>> ListAsync(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) throw new IOException("The library folder can't be reached.");
        var folders = new List<(string Path, bool Clip)>();
        foreach (var (sub, pattern, clip) in new[] { ("clips", "clip_*", true), ("video", "bg_*", false) })
        {
            var dir = Path.Combine(root, sub);
            if (Directory.Exists(dir)) folders.AddRange(Directory.EnumerateDirectories(dir, pattern).Select(p => (p, clip)));
        }
        var found = new List<RemoteRecording>();
        await Parallel.ForEachAsync(folders, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (f, token) =>
        {
            if (FolderNames.ParseRecording(Path.GetFileName(f.Path)) is not { } name) return;
            var scan = f.Clip ? await Scanner.ScanClipAsync(f.Path, ct: token).ConfigureAwait(false) : await Scanner.ScanAsync(f.Path, ct: token).ConfigureAwait(false);
            if (scan.Media is { ChunkCount: > 0 } media)
                lock (found) found.Add(new RemoteRecording(Path.GetFileName(f.Path), name, media));
        }).ConfigureAwait(false);
        return [.. found.OrderByDescending(r => r.Name.StartUtc)];
    }
}
