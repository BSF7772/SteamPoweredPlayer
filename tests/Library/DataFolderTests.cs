// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Library;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spp-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class DataFolderTests
{
    [Fact]
    public void Proposal_is_a_sibling_of_the_recording_path()
    {
        var p = DataFolder.Propose(@"D:\Recordings");
        Assert.True(p.IsValid);
        Assert.Equal(@"D:\SteamPoweredPlayer", p.Path);
    }

    [Fact]
    public void Proposal_handles_trailing_separator_and_nested_path()
    {
        Assert.Equal(@"D:\Steam\SteamPoweredPlayer", DataFolder.Propose(@"D:\Steam\Rec\").Path);
        Assert.Equal(@"D:\Steam\SteamPoweredPlayer", DataFolder.Propose(@"D:\Steam\Other\..\Rec").Path);
    }

    [Fact]
    public void Proposal_works_on_a_network_share_folder()
    {
        Assert.Equal(@"\\nas\games\SteamPoweredPlayer", DataFolder.Propose(@"\\nas\games\Rec").Path);
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"D:")]
    [InlineData(@"\\nas\share")]
    public void Recording_path_at_a_root_has_no_proposal(string root)
    {
        // A relative "D:" is rejected as invalid; the roots are never proposed with a child inside them.
        var p = DataFolder.Propose(root);
        Assert.False(p.IsValid);
        Assert.Null(p.Path);
        Assert.Contains(p.Problem, new[] { DataFolderProblem.RecordingPathIsRoot, DataFolderProblem.InvalidPath });
    }

    [Fact]
    public void Drive_root_recording_path_reports_root_problem() =>
        Assert.Equal(DataFolderProblem.RecordingPathIsRoot, DataFolder.Propose(@"D:\").Problem);

    [Theory]
    [InlineData(@"D:\Rec", DataFolderProblem.InsideRecordingPath)]
    [InlineData(@"d:\rec", DataFolderProblem.InsideRecordingPath)]
    [InlineData(@"D:\Rec\", DataFolderProblem.InsideRecordingPath)]
    [InlineData(@"D:\Rec\SteamPoweredPlayer", DataFolderProblem.InsideRecordingPath)]
    [InlineData(@"D:\Rec\a\..\b", DataFolderProblem.InsideRecordingPath)]
    [InlineData(@"D:\Other\..\Rec\x", DataFolderProblem.InsideRecordingPath)]
    [InlineData(@"D:\", DataFolderProblem.ContainsRecordingPath)]
    [InlineData(@"D:\Games", DataFolderProblem.ContainsRecordingPath)]
    [InlineData(@"relative\path", DataFolderProblem.InvalidPath)]
    [InlineData("", DataFolderProblem.InvalidPath)]
    public void Validation_rejects_overlap_with_the_recording_path(string candidate, DataFolderProblem expected)
    {
        var brp = candidate.StartsWith(@"D:\Games", StringComparison.OrdinalIgnoreCase) ? @"D:\Games\Rec" : @"D:\Rec";
        Assert.Equal(expected, DataFolder.Validate(candidate, brp).Problem);
    }

    [Theory]
    [InlineData(@"D:\Recordings2")]            // shares a name prefix, is not inside
    [InlineData(@"D:\Rec-Data")]
    [InlineData(@"E:\SteamPoweredPlayer")]
    [InlineData(@"D:\SteamPoweredPlayer")]
    public void Validation_accepts_unrelated_folders_even_with_a_common_prefix(string candidate) =>
        Assert.True(DataFolder.Validate(candidate, @"D:\Rec").IsValid);

    [Fact]
    public void Prefix_lookalike_is_not_inside()
    {
        Assert.True(DataFolder.Validate(@"D:\Recordings2\x", @"D:\Recordings").IsValid);
        Assert.Equal(DataFolderProblem.InvalidPath, DataFolder.Validate(@"D:\ok", "").Problem);
    }

    [Fact]
    public void Open_throws_for_a_rejected_folder_and_creates_nothing()
    {
        using var t = new TempDir();
        var brp = t.Sub("Rec");
        Directory.CreateDirectory(brp);
        var inside = Path.Combine(brp, "SteamPoweredPlayer");
        Assert.Throws<ArgumentException>(() => DataFolder.Open(inside, brp));
        Assert.False(Directory.Exists(inside));
    }

    [Fact]
    public void Layout_paths_are_under_the_root()
    {
        var f = DataFolder.Open(@"D:\SteamPoweredPlayer", @"D:\Rec");
        Assert.Equal(@"D:\SteamPoweredPlayer\library.json", f.LibraryJson);
        Assert.Equal(@"D:\SteamPoweredPlayer\library.json.tmp", f.LibraryJsonTemp);
        Assert.Equal(@"D:\SteamPoweredPlayer\library-id", f.LibraryIdFile);
        Assert.Equal(@"D:\SteamPoweredPlayer\art", f.ArtDir);
        Assert.Equal(@"D:\SteamPoweredPlayer\trick", f.TrickDir);
        Assert.Equal(@"D:\SteamPoweredPlayer\proxies", f.ProxiesDir);
        Assert.Equal(@"D:\SteamPoweredPlayer\edits", f.EditsDir);
        Assert.Equal(@"D:\SteamPoweredPlayer\art\730", f.ArtDirFor("730"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"..\x")]
    [InlineData("12a")]
    [InlineData("123456789012345678901")]
    public void Unsafe_game_ids_are_refused(string id)
    {
        var f = DataFolder.Open(@"D:\SteamPoweredPlayer", @"D:\Rec");
        Assert.Throws<ArgumentException>(() => f.ArtDirFor(id));
    }

    [Fact]
    public void Library_id_is_created_once_and_read_back()
    {
        using var t = new TempDir();
        var f = DataFolder.Open(t.Sub("data"), t.Sub("Rec"));
        var a = f.ReadOrCreateLibraryId();
        var b = f.ReadOrCreateLibraryId();
        Assert.NotEqual(Guid.Empty, a);
        Assert.Equal(a, b);
        Assert.Equal(a, DataFolder.Open(t.Sub("data"), t.Sub("Rec")).ReadOrCreateLibraryId());
        Assert.Equal(a.ToString("D") + "\n", File.ReadAllText(f.LibraryIdFile));
    }

    [Fact]
    public void Library_id_is_stable_under_concurrent_first_use()
    {
        using var t = new TempDir();
        var f = DataFolder.Open(t.Sub("data"), t.Sub("Rec"));
        var ids = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8).Select(_ => f.ReadOrCreateLibraryId()).Distinct().ToList();
        Assert.Single(ids);
    }

    [Fact]
    public void Garbage_library_id_is_reported_not_overwritten()
    {
        using var t = new TempDir();
        var f = DataFolder.Open(t.Sub("data"), t.Sub("Rec"));
        f.EnsureCreated();
        File.WriteAllText(f.LibraryIdFile, "not a guid");
        Assert.Throws<IOException>(() => f.ReadOrCreateLibraryId());
        Assert.Equal("not a guid", File.ReadAllText(f.LibraryIdFile));
    }
}
