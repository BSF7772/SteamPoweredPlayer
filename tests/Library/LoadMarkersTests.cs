// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class LoadMarkersTests
{
    private static void WriteTimeline(string root)
    {
        var tl = Path.Combine(root, "timelines");
        Directory.CreateDirectory(tl);
        // the timeline started 120 s before the session: the entry at 130 s lands at 10 s, the one at 5 s before the media (kept, not trimmed)
        File.WriteAllText(Path.Combine(tl, "timeline_73020250101_115800.json"),
            "{\"daterecorded\":\"1735732680\",\"entries\":[{\"id\":\"a\",\"time\":130000,\"type\":\"event\",\"title\":\"Kill\"},{\"id\":\"b\",\"time\":5000,\"type\":\"event\",\"title\":\"Early\"}]}");
    }

    [Fact]
    public async Task Loads_all_markers_of_a_cache_session_on_the_media_axis_with_the_name_fallback()
    {
        using var s = new SynthLibrary();
        s.AddBg("bg_730_20250101_120000", 1, 20);
        WriteTimeline(s.Root);

        var b = new LibraryBuilder(s.Root, s.Names, null, s.Clock, new ScanOptions());
        var ctx = await b.LoadContextAsync();
        var set = await b.LoadMarkersAsync(b.FolderFor("video/bg_730_20250101_120000")!, ctx);

        Assert.Equal(MarkerAlignment.Approximate, set.Alignment);
        Assert.Equal(2, set.Markers.Count);                                   // unlike the index count, nothing is trimmed away
        Assert.Equal(-115, set.Markers[0].StartSeconds, 3);
        Assert.Equal(10, set.Markers[1].StartSeconds, 3);
    }

    [Fact]
    public async Task A_recording_without_a_timeline_has_no_markers()
    {
        using var s = new SynthLibrary();
        s.AddBg("bg_730_20250101_120000", 1, 5);
        var b = new LibraryBuilder(s.Root, s.Names, null, s.Clock, new ScanOptions());
        var set = await b.LoadMarkersAsync(b.FolderFor("video/bg_730_20250101_120000")!, await b.LoadContextAsync());
        Assert.Empty(set.Markers);
        Assert.Equal(MarkerAlignment.None, set.Alignment);
    }
}
