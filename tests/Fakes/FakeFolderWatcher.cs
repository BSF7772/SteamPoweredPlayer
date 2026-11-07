// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Fakes;

public sealed class FakeFolderWatcher : IFolderWatcher
{
    public event Action<FolderChange>? Changed;
    public event Action<Exception>? Failed;
    public string? Root { get; private set; }
    public bool Disposed { get; private set; }
    public void Start(string root) => Root = root;
    public void Raise(FolderChangeKind kind, string path, string? old = null) => Changed?.Invoke(new(kind, path, old));
    public void Fail(Exception e) => Failed?.Invoke(e);
    public void Dispose() => Disposed = true;
}
