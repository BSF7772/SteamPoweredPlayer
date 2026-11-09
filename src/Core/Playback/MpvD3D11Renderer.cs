// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SteamPoweredPlayer.Core.Playback;

/// <summary>Frame pacing of the last presents (see <see cref="MpvD3D11Renderer.Stats"/>).</summary>
public sealed record PresentStats(long Count, double MeanMs, double P50Ms, double P99Ms, double MaxMs, long DeviceRemoved, long PresentErrors);

/// <summary>
/// Draws mpv's frames with the libmpv D3D11 render API into a DXGI composition swap chain (FlipSequential, 3 buffers; spike S1).
/// No UI types here: the app binds <see cref="SwapChain"/> to its <c>SwapChainPanel</c>.
/// </summary>
/// <remarks>
/// <para><b>Binding to a WinUI 3 SwapChainPanel</b> (UI thread, app code; <c>panel</c> is the XAML element):</para>
/// <code>
/// // ISwapChainPanelNative {63aad0b8-7c24-40ff-85a8-640d944cc325}: vtable slot 3 = SetSwapChain(IDXGISwapChain*)
/// var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
/// var unk = WinRT.MarshalInspectable&lt;object&gt;.FromManaged(panel);
/// try {
///     Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in iid, out var native));
///     try {
///         var vtbl = *(nint**)native;
///         var setSwapChain = (delegate* unmanaged[Stdcall]&lt;nint, nint, int&gt;)vtbl[3];
///         Marshal.ThrowExceptionForHR(setSwapChain(native, renderer.SwapChain));
///     } finally { Marshal.Release(native); }
/// } finally { Marshal.Release(unk); }
/// </code>
/// <para>Size: pass <c>ActualWidth × RasterizationScale</c> (device pixels) and the scale to the constructor and to <see cref="Resize"/>
/// on <c>SizeChanged</c> / <c>CompositionScaleChanged</c> / fullscreen. Create the renderer before the first <see cref="MpvPlayer.LoadAsync"/>
/// (<c>vo=libmpv</c> needs a render context), and dispose it before the player.</para>
/// <para>Threading: mpv's update callback wakes one render thread. Every use of the immediate device context (mpv render, Present,
/// ResizeBuffers) happens under one lock, because mpv draws on it and no other thread may touch it.</para>
/// </remarks>
public sealed unsafe class MpvD3D11Renderer : IDisposable
{
    private const int StatsWindow = 2048;

    [StructLayout(LayoutKind.Sequential)] private struct D3D11Init { public nint Device; }
    [StructLayout(LayoutKind.Sequential)] private struct D3D11Fbo { public nint Tex; }

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly long[] _presentTicks = new long[StatsWindow];     // ring: the last StatsWindow presents
    private readonly ID3D11Device _dev;
    private readonly ID3D11DeviceContext _ctx;
    private readonly IDXGISwapChain1 _sc;
    private readonly IDXGIAdapter3? _adapter3;
    private GCHandle _self;
    private nint _rc;
    private volatile bool _stop, _force;
    private int _w, _h;
    private int _ringCount;
    private long _presentCount, _removed, _presentErrors;

    /// <summary>The IDXGISwapChain1 pointer to hand to <c>ISwapChainPanelNative::SetSwapChain</c>. Owned by the renderer.</summary>
    public IntPtr SwapChain => _sc.NativePointer;

    public string AdapterName { get; }

    /// <summary>Raised on the render thread when the device was removed or reset; the app recreates the renderer.</summary>
    public event Action? DeviceLost;

    /// <param name="player">The player to render for (must use <see cref="MpvOutput.Render"/>).</param>
    /// <param name="width">Back buffer width in device pixels.</param>
    /// <param name="height">Back buffer height in device pixels.</param>
    /// <param name="scale">Composition scale (RasterizationScale): the swap chain is drawn at 1/scale so it fills the panel in DIPs.</param>
    public MpvD3D11Renderer(MpvPlayer player, int width, int height, float scale)
    {
        ArgumentNullException.ThrowIfNull(player);
        _w = Math.Max(width, 16);
        _h = Math.Max(height, 16);

        // No SingleThreaded flag: mpv enables multithread protection and d3d11va decodes on this same device.
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _dev!, out _, out _ctx!).CheckError();
        using var dxgiDev = _dev.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDev.GetAdapter();
        AdapterName = adapter.Description.Description;
        _adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _sc = factory.CreateSwapChainForComposition(_dev, new SwapChainDescription1
        {
            Width = (uint)_w, Height = (uint)_h, Format = Format.B8G8R8A8_UNorm,
            BufferUsage = Usage.RenderTargetOutput, BufferCount = 3, SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Ignore,
        });
        SetTransform(scale);

        _self = GCHandle.Alloc(this);
        _thread = new Thread(Loop) { IsBackground = true, Name = "mpv-render", Priority = ThreadPriority.AboveNormal };
        var init = new D3D11Init { Device = _dev.NativePointer };
        var api = Marshal.StringToCoTaskMemUTF8("d3d11");
        try
        {
            var p = stackalloc MpvRenderParam[3];
            p[0] = new() { Type = MpvConst.RenderApiType, Data = api };
            p[1] = new() { Type = MpvConst.RenderD3D11Init, Data = (nint)(&init) };
            p[2] = default;
            nint ctx;
            var err = MpvNative.mpv_render_context_create(&ctx, player.NativeHandle, p);
            if (err < 0)
            {
                Cleanup();
                throw new MpvException($"mpv_render_context_create (d3d11): {MpvNative.ErrorString(err)}", err);
            }
            _rc = ctx;
        }
        finally { Marshal.FreeCoTaskMem(api); }
        MpvNative.mpv_render_context_set_update_callback(_rc, &OnUpdate, GCHandle.ToIntPtr(_self));
        _thread.Start();
        _wake.Set();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUpdate(nint ctx)
    {
        if (GCHandle.FromIntPtr(ctx).Target is MpvD3D11Renderer r) r._wake.Set();
    }

    private void SetTransform(float scale)
    {
        // The back buffer is in device pixels, the panel in DIPs: undo the compositor's DPI scale.
        if (scale <= 0) scale = 1;
        using var sc2 = _sc.QueryInterface<IDXGISwapChain2>();
        sc2.MatrixTransform = Matrix3x2.CreateScale(1f / scale, 1f / scale);
    }

    /// <summary>The panel's pixel size or DPI scale changed (SizeChanged, CompositionScaleChanged, fullscreen). Any thread.</summary>
    public void Resize(int width, int height, float scale)
    {
        width = Math.Max(width, 16);
        height = Math.Max(height, 16);
        lock (_gate)
        {
            if (_stop) return;
            if (width != _w || height != _h)
            {
                _sc.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
                _w = width;
                _h = height;
            }
            SetTransform(scale);
            _force = true;
        }
        _wake.Set();
    }

    private volatile bool _hold;

    /// <summary>While true the screen keeps its picture: a back-step's walk over earlier frames isn't shown. Setting it false draws the current frame.</summary>
    public bool HoldPicture
    {
        get => _hold;
        set
        {
            _hold = value;
            if (!value) RequestRedraw();
        }
    }

    /// <summary>Draw the current frame again (e.g. after a window-state change while paused).</summary>
    public void RequestRedraw()
    {
        _force = true;
        _wake.Set();
    }

    public (int Width, int Height) PixelSize
    {
        get { lock (_gate) return (_w, _h); }
    }

    public long PresentCount => Interlocked.Read(ref _presentCount);

    private void Loop()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop) break;
            lock (_gate)
            {
                if (_rc == 0) continue;
                var flags = MpvNative.mpv_render_context_update(_rc);
                var frame = (flags & MpvConst.RenderUpdateFrame) != 0 || _force;
                _force = false;
                if (frame) RenderOne();
            }
        }
    }

    private void RenderOne()
    {
        using var bb = _sc.GetBuffer<ID3D11Texture2D>(0);
        var fbo = new D3D11Fbo { Tex = bb.NativePointer };
        var noBlock = 0;
        var p = stackalloc MpvRenderParam[3];
        p[0] = new() { Type = MpvConst.RenderD3D11Fbo, Data = (nint)(&fbo) };
        p[1] = new() { Type = MpvConst.RenderBlockForTargetTime, Data = (nint)(&noBlock) };     // Present(1) already paces us
        p[2] = default;
        MpvNative.mpv_render_context_render(_rc, p);
        if (_hold)
        {
            // mpv still gets its frame rendered and reported, or its frame steps would wait on us; the screen keeps what it shows.
            MpvNative.mpv_render_context_report_swap(_rc);
            return;
        }

        var r = _sc.Present(1, PresentFlags.None);
        var now = Stopwatch.GetTimestamp();
        if (r.Failure)
        {
            if (r.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || r.Code == Vortice.DXGI.ResultCode.DeviceReset.Code)
            {
                Interlocked.Increment(ref _removed);
                try { DeviceLost?.Invoke(); } catch (Exception) { /* app handler bug */ }
            }
            else Interlocked.Increment(ref _presentErrors);
        }
        MpvNative.mpv_render_context_report_swap(_rc);
        var n = Interlocked.Increment(ref _presentCount);
        lock (_presentTicks)
        {
            _presentTicks[(int)((n - 1) % StatsWindow)] = now;
            if (_ringCount < StatsWindow) _ringCount++;
        }
    }

    /// <summary>Present intervals over the last ≤ 2048 presents (one pass over the ring; call it for diagnostics, not per frame).</summary>
    public PresentStats Stats()
    {
        long[] ticks;
        long count;
        lock (_presentTicks)
        {
            count = PresentCount;
            var n = _ringCount;
            ticks = new long[n];
            var first = (int)((count - n) % StatsWindow);
            for (var i = 0; i < n; i++) ticks[i] = _presentTicks[(first + i) % StatsWindow];
        }
        var removed = Interlocked.Read(ref _removed);
        var errors = Interlocked.Read(ref _presentErrors);
        if (ticks.Length < 2) return new(count, 0, 0, 0, 0, removed, errors);
        var iv = new double[ticks.Length - 1];
        for (var i = 1; i < ticks.Length; i++) iv[i - 1] = (ticks[i] - ticks[i - 1]) * 1000.0 / Stopwatch.Frequency;
        var sorted = iv.Order().ToArray();
        double Pct(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        return new(count, iv.Average(), Pct(0.5), Pct(0.99), sorted[^1], removed, errors);
    }

    /// <summary>Process-wide local (VRAM) usage on this adapter in MB (NaN when the adapter does not report it).</summary>
    public double GpuLocalMb() =>
        _adapter3 is null ? double.NaN : _adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage / 1048576.0;

    /// <summary>S_OK (0x00000000) unless the device was removed or reset (<c>GetDeviceRemovedReason</c>).</summary>
    public string DeviceRemovedReason() => "0x" + ((uint)_dev.DeviceRemovedReason.Code).ToString("X8");

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(2000);
        Cleanup();
    }

    private void Cleanup()
    {
        lock (_gate)
        {
            _stop = true;
            if (_rc != 0)
            {
                MpvNative.mpv_render_context_set_update_callback(_rc, null, 0);
                MpvNative.mpv_render_context_free(_rc);
                _rc = 0;
            }
            _adapter3?.Dispose();
            _sc.Dispose();
            _ctx.Dispose();
            _dev.Dispose();
        }
        if (_self.IsAllocated) _self.Free();
        _wake.Dispose();
    }
}
