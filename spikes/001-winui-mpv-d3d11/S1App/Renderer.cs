// SPDX-License-Identifier: GPL-3.0-or-later
// D3D11 device + DXGI composition swap chain bound to a SwapChainPanel, driven by libmpv's D3D11 render API.
// Threading: mpv's update callback wakes ONE render thread. All device-context use (mpv render, probe, Present,
// ResizeBuffers) happens under _gate, because mpv draws on the immediate context and other threads must not touch it.
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace S1App;

public sealed record PresentSummary(int Count, double MeanMs, double P50Ms, double P99Ms, double MaxMs, int Over15x, int Over2x,
    int BlackFrames, int ProbedFrames, int DeviceRemoved, int PresentErrors, string RefreshHistogram, string DeviceReason);

public sealed unsafe class Renderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct D3D11Init { public nint Device; }
    [StructLayout(LayoutKind.Sequential)] struct D3D11Fbo { public nint Tex; }

    readonly SwapChainPanel _panel;
    readonly MpvPlayer _mpv;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    readonly List<long> _presentTicks = new();
    readonly Dictionary<int, int> _refreshDelta = new();
    ID3D11Device _dev = null!;
    ID3D11DeviceContext _ctx = null!;
    IDXGISwapChain1 _sc = null!;
    IDXGIAdapter3? _adapter3;
    ID3D11Texture2D? _staging;
    nint _rc;
    GCHandle _self;
    volatile bool _stop, _force;
    int _w, _h;
    int _blackFrames, _probed, _removed, _presentErrors;
    uint _lastRefresh; bool _statsBroken;
    public volatile bool ProbeBlack;
    public int LastRenderResult { get; private set; }
    public string Adapter { get; private set; } = "";

    public Renderer(SwapChainPanel panel, MpvPlayer mpv)
    {
        _panel = panel; _mpv = mpv;
        _thread = new Thread(Loop) { IsBackground = true, Name = "mpv-render", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Create device/swap chain at the given pixel size, bind to the panel, create the mpv render context. UI thread.</summary>
    public void Initialize(int w, int h, float scaleX, float scaleY)
    {
        _w = Math.Max(w, 16); _h = Math.Max(h, 16);
        // No SingleThreaded flag: mpv enables multithread protection and d3d11va decodes on this device.
        var rc = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _dev!, out _, out _ctx!);
        rc.CheckError();
        using var dxgiDev = _dev.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDev.GetAdapter();
        Adapter = adapter.Description.Description;
        _adapter3 = adapter.QueryInterface<IDXGIAdapter3>();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)_w, Height = (uint)_h, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            BufferUsage = Usage.RenderTargetOutput, BufferCount = 3, SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Ignore,
        };
        _sc = factory.CreateSwapChainForComposition(_dev, desc);
        SetTransform(scaleX, scaleY);
        BindToPanel();

        _self = GCHandle.Alloc(this);
        var init = new D3D11Init { Device = _dev.NativePointer };
        var api = Marshal.StringToCoTaskMemUTF8("d3d11");
        try
        {
            var p = stackalloc MpvRenderParam[3];
            p[0] = new() { Type = MpvConst.RenderApiType, Data = api };
            p[1] = new() { Type = MpvConst.RenderD3D11Init, Data = (nint)(&init) };
            p[2] = default;
            nint ctx;
            var err = Native.mpv_render_context_create(&ctx, _mpv.Handle, p);
            if (err < 0) throw new InvalidOperationException($"mpv_render_context_create (d3d11) failed rc={err}");
            _rc = ctx;
        }
        finally { Marshal.FreeCoTaskMem(api); }
        Native.mpv_render_context_set_update_callback(_rc, &OnUpdate, GCHandle.ToIntPtr(_self));
        _thread.Start();
        _wake.Set();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnUpdate(nint ctx) { if (GCHandle.FromIntPtr(ctx).Target is Renderer r) r._wake.Set(); }

    // ISwapChainPanelNative {63aad0b8-7c24-40ff-85a8-640d944cc325}: vtable slot 3 = SetSwapChain(IDXGISwapChain*)
    void BindToPanel()
    {
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        var unk = WinRT.MarshalInspectable<object>.FromManaged(_panel);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in iid, out var native));
            try
            {
                var vtbl = *(nint**)native;
                var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[3];
                Marshal.ThrowExceptionForHR(setSwapChain(native, _sc.NativePointer));
            }
            finally { Marshal.Release(native); }
        }
        finally { Marshal.Release(unk); }
    }

    void SetTransform(float scaleX, float scaleY)
    {
        // Back buffer is in device pixels, the panel in DIPs: undo the compositor's DPI scale.
        using var sc2 = _sc.QueryInterface<IDXGISwapChain2>();
        sc2.MatrixTransform = Matrix3x2.CreateScale(1f / scaleX, 1f / scaleY);
    }

    /// <summary>Pixel size changed (SizeChanged / DPI / fullscreen). UI thread.</summary>
    public void Resize(int w, int h, float scaleX, float scaleY)
    {
        w = Math.Max(w, 16); h = Math.Max(h, 16);
        lock (_gate)
        {
            if (_sc == null) return;
            if (w != _w || h != _h)
            {
                _staging?.Dispose(); _staging = null;
                _sc.ResizeBuffers(0, (uint)w, (uint)h, Vortice.DXGI.Format.Unknown, SwapChainFlags.None).CheckError();
                _w = w; _h = h;
            }
            SetTransform(scaleX, scaleY);
            _force = true;
        }
        _wake.Set();
    }

    public void RequestRedraw() { _force = true; _wake.Set(); }

    void Loop()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop) break;
            lock (_gate)
            {
                if (_rc == 0) continue;
                var flags = Native.mpv_render_context_update(_rc);
                var frame = (flags & 1) != 0 || _force;
                _force = false;
                if (frame) RenderOne();
            }
        }
    }

    void RenderOne()
    {
        using var bb = _sc.GetBuffer<ID3D11Texture2D>(0);
        var fbo = new D3D11Fbo { Tex = bb.NativePointer };
        int noBlock = 0;
        var p = stackalloc MpvRenderParam[3];
        p[0] = new() { Type = MpvConst.RenderD3D11Fbo, Data = (nint)(&fbo) };
        p[1] = new() { Type = 12 /*BLOCK_FOR_TARGET_TIME*/, Data = (nint)(&noBlock) }; // Present(1) already paces us
        p[2] = default;
        LastRenderResult = Native.mpv_render_context_render(_rc, p);
        if (ProbeBlack) Probe(bb);
        var r = _sc.Present(1, PresentFlags.None);
        var now = Stopwatch.GetTimestamp();
        if (r.Failure)
        {
            if (r.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || r.Code == Vortice.DXGI.ResultCode.DeviceReset.Code) _removed++;
            else _presentErrors++;
        }
        PresentCount++; LastPresentTicks = now;
        Native.mpv_render_context_report_swap(_rc);
        lock (_presentTicks)
        {
            _presentTicks.Add(now);
            if (_presentTicks.Count % 30 == 0) SampleFrameStats();
        }
    }

    // DXGI present statistics: PresentRefreshCount delta between samples = how many vblanks passed per 30 presents.
    void SampleFrameStats()
    {
        if (_statsBroken) return;
        try
        {
            if (_sc.GetFrameStatistics(out var st).Failure) { _statsBroken = true; _refreshDelta[-1] = 1; return; }
            var d = (int)(st.PresentRefreshCount - _lastRefresh);
            _lastRefresh = st.PresentRefreshCount;
            _refreshDelta[d] = _refreshDelta.GetValueOrDefault(d) + 1;
        }
        catch { _statsBroken = true; _refreshDelta[-1] = 1; }
    }

    // 4x4 grid in the central region read back through a 16x1 staging texture; "black" = every sample near zero.
    void Probe(ID3D11Texture2D bb)
    {
        _staging ??= _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = 16, Height = 1, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        for (int i = 0; i < 16; i++)
        {
            int x = (int)(_w * (0.35 + 0.1 * (i % 4))), y = (int)(_h * (0.35 + 0.1 * (i / 4)));
            _ctx.CopySubresourceRegion(_staging, 0, (uint)i, 0, 0, bb, 0, new Box(x, y, 0, x + 1, y + 1, 1));
        }
        var m = _ctx.Map(_staging, 0, MapMode.Read);
        int sum = 0;
        for (int i = 0; i < 16; i++) { var px = (byte*)m.DataPointer + i * 4; sum += px[0] + px[1] + px[2]; }
        _ctx.Unmap(_staging, 0);
        _probed++;
        if (sum < 16 * 3) _blackFrames++;
    }

    public void ResetStats()
    {
        lock (_presentTicks) { _presentTicks.Clear(); _refreshDelta.Clear(); }
        _blackFrames = _probed = _removed = _presentErrors = 0;
    }

    public PresentSummary Summary()
    {
        double[] iv;
        string hist;
        lock (_presentTicks)
        {
            iv = new double[Math.Max(0, _presentTicks.Count - 1)];
            for (int i = 1; i < _presentTicks.Count; i++) iv[i - 1] = MpvPlayer.Ms(_presentTicks[i] - _presentTicks[i - 1]);
            hist = string.Join(",", _refreshDelta.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));
        }
        var dr = DeviceReason();
        if (iv.Length == 0) return new(0, 0, 0, 0, 0, 0, 0, _blackFrames, _probed, _removed, _presentErrors, hist, dr);
        var s = iv.Order().ToArray();
        double Pct(double q) => s[Math.Min(s.Length - 1, (int)(q * s.Length))];
        double med = Pct(0.5);
        return new(iv.Length + 1, iv.Average(), med, Pct(0.99), s[^1], iv.Count(x => x > med * 1.5), iv.Count(x => x > med * 2),
            _blackFrames, _probed, _removed, _presentErrors, hist, dr);
    }

    /// <summary>Process-wide local (VRAM) usage on this adapter in MB.</summary>
    public double GpuLocalMb() => _adapter3 is null ? double.NaN : _adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage / 1048576.0;
    public double GpuNonLocalMb() => _adapter3 is null ? double.NaN : _adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage / 1048576.0;
    public (int W, int H) PixelSize => (_w, _h);
    public long PresentCount { get; private set; }
    public long LastPresentTicks { get; private set; }
    /// <summary>S_OK unless the D3D11 device was removed/reset (GetDeviceRemovedReason).</summary>
    public string DeviceReason() => _dev is null ? "n/a" : "0x" + ((uint)_dev.DeviceRemovedReason.Code).ToString("X8");

    public void Dispose()
    {
        _stop = true; _wake.Set(); _thread.Join(2000);
        lock (_gate)
        {
            if (_rc != 0) { Native.mpv_render_context_set_update_callback(_rc, null, 0); Native.mpv_render_context_free(_rc); _rc = 0; }
            _staging?.Dispose(); _adapter3?.Dispose(); _sc?.Dispose(); _ctx?.Dispose(); _dev?.Dispose();
        }
        if (_self.IsAllocated) _self.Free();
    }
}
