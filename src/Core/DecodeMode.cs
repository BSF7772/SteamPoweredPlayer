// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core;

/// <summary>
/// The one decode setting (Settings → "Decoding: GPU (auto) / CPU"), shared by playback and trickplay.
/// Playback: <c>hwdec=d3d11va</c> (zero-copy into the renderer's device) or <c>hwdec=no</c>.
/// Trickplay: ffmpeg <c>-hwaccel d3d11va</c>, repeated in <see cref="Cpu"/> mode automatically if it fails.
/// </summary>
public enum DecodeMode
{
    /// <summary>Hardware decode (D3D11VA), with a CPU fallback where the caller supports one.</summary>
    Gpu,
    /// <summary>Software decode (always available).</summary>
    Cpu,
}
