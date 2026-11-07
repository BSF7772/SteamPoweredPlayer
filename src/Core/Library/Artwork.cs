// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Library;

/// <summary>Absolute source files (Steam's library cache) for one game. Null or missing files are skipped.</summary>
public sealed record ArtworkSource(string? Cover = null, string? Hero = null, string? HeroBlur = null, string? Logo = null);

/// <summary>
/// Copies a game's art once into <c>art/&lt;gameId&gt;/</c> of the data folder so remote clients get it through the same share.
/// The source is only read (read + delete sharing); an existing copy with the same size and modification time is kept.
/// </summary>
public sealed class ArtworkStore(DataFolder folder)
{
    public async Task<GameArt> CopyAsync(string gameId, ArtworkSource source, CancellationToken ct = default)
    {
        var dir = folder.ArtDirFor(gameId);   // validates gameId
        return new GameArt(
            await CopyOneAsync(gameId, dir, "cover", source.Cover, ct).ConfigureAwait(false),
            await CopyOneAsync(gameId, dir, "hero", source.Hero, ct).ConfigureAwait(false),
            await CopyOneAsync(gameId, dir, "heroBlur", source.HeroBlur, ct).ConfigureAwait(false),
            await CopyOneAsync(gameId, dir, "logo", source.Logo, ct).ConfigureAwait(false));
    }

    private async Task<string?> CopyOneAsync(string gameId, string dir, string role, string? sourcePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        ct.ThrowIfCancellationRequested();

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext.Length is 0 or > 6 || !ext.Skip(1).All(char.IsAsciiLetterOrDigit)) ext = ".img";
        var fileName = role + ext;
        var dest = Path.Combine(dir, fileName);
        var relative = $"art/{gameId}/{fileName}";

        try
        {
            var src = new FileInfo(sourcePath);
            if (!src.Exists) return null;
            var srcLength = src.Length;
            var srcTime = src.LastWriteTimeUtc;

            var existing = new FileInfo(dest);
            if (existing.Exists && existing.Length == srcLength && existing.LastWriteTimeUtc == srcTime) return relative;

            folder.EnsureCreated();
            Directory.CreateDirectory(dir);
            var tmp = dest + ".tmp";
            try
            {
                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(tmp, srcTime);
                File.Move(tmp, dest, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { }
                throw;
            }
            return relative;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
