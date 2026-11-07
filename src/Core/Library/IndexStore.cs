// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Library;

/// <summary>
/// Reads and writes <c>library.json</c>. Only the desktop writes (plan D11). A write is atomic: bytes go to <c>library.json.tmp</c>
/// in the same folder, are flushed to disk, then replace the target, so a reader (a phone over SMB, or a crash mid-write) sees either
/// the old or the new file, never half of one.
/// </summary>
public sealed class IndexStore
{
    private readonly DataFolder _folder;
    private readonly Func<string, Stream> _createTemp;

    public IndexStore(DataFolder folder) : this(folder, static p => new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous)) { }

    /// <summary>Test seam: supply the stream used for the temp file (e.g. one that throws mid-write).</summary>
    internal IndexStore(DataFolder folder, Func<string, Stream> createTemp)
    {
        _folder = folder;
        _createTemp = createTemp;
    }

    public async Task WriteAsync(LibraryIndex index, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = index.ToUtf8();            // serialise first: a model error never touches the disk
        _folder.EnsureCreated();
        var tmp = _folder.LibraryJsonTemp;
        try
        {
            await using (var s = _createTemp(tmp))
            {
                await s.WriteAsync(bytes, ct).ConfigureAwait(false);
                await s.FlushAsync(ct).ConfigureAwait(false);
                if (s is FileStream fs) fs.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();  // last chance to abort; after this the swap is not cancellable
            Swap(tmp, _folder.LibraryJson);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public async Task<LibraryIndexParse> ReadAsync(CancellationToken ct = default)
    {
        // File.Replace is not instantaneous for a reader: for a few milliseconds the open can fail with a sharing violation or the
        // name can be missing while a temp file exists. Retry briefly; readers on other platforms must do the same (docs/data-folder.md).
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(_folder.LibraryJson, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var data = new byte[checked((int)fs.Length)];
                await fs.ReadExactlyAsync(data, ct).ConfigureAwait(false);
                return LibraryIndex.Parse(data);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Measured: a reader can see "not found" for a moment while the writer swaps, even with MoveFileEx. Confirm before saying Missing.
                if (attempt >= 3) return new(LibraryIndexStatus.Missing, null, 0, null);
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }
    }

    internal static void Swap(string tmp, string target)
    {
        // Readers hold the target with FileShare.Delete, so the swap normally succeeds at once; retry briefly for other sharing violations.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // MoveFileEx(REPLACE_EXISTING) swaps the name in one step (no moment without a library.json); it needs the old file's
                // other handles to allow delete sharing, which our readers and phones do. File.Replace is the fallback.
                try { File.Move(tmp, target, overwrite: true); }
                catch (UnauthorizedAccessException) when (File.Exists(target)) { File.Replace(tmp, target, null, ignoreMetadataErrors: true); }
                return;
            }
            catch (IOException) when (attempt < 4) { Thread.Sleep(50 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
