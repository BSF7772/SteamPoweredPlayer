// SPDX-License-Identifier: GPL-3.0-or-later
// Window plumbing (title bar, placement, chips, toasts, shimmer) adapted from FastEncoder's MainWindow.xaml.cs (MIT).
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using SteamPoweredPlayer.Services;
using SteamPoweredPlayer.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace SteamPoweredPlayer;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly IntPtr _hwnd;
    private Storyboard? _shimmer;
    private bool _syncingNav;
    private int _busy;

    public IntPtr Hwnd => _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        AppServices.Window = this;
        _hwnd = WindowNative.GetWindowHandle(this);
        Root.RequestedTheme = AppServices.Options.Theme;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SteamPoweredPlayer.ico"));
        PlaceWindow();

        ContentFrame.Navigated += (_, e) => SyncNav(e.SourcePageType);
        Root.Loaded += Root_Loaded;
        AppServices.LibraryCreated += WireSource;
        Closed += (_, _) =>
        {
            PlayerHost.Instance?.Shutdown();      // flush resume positions, dispose renderer then mpv
            // Flushes the index and scan cache (when a data folder is confirmed) and stops the watcher.
            AppServices.Trickplay?.Dispose();
            if (AppServices.Library is IDisposable d) d.Dispose();
        };
    }

    /// <summary>
    /// Lets the page cover the 52 px title bar row (the Player page does it in fullscreen so the video gets the whole window). The title
    /// bar itself stays laid out: collapsing the element registered with <c>SetTitleBar</c> made WinUI fail fast when it came back.
    /// </summary>
    public void SetChromeVisible(bool visible)
    {
        Grid.SetRow(ContentFrame, visible ? 1 : 0);
        Grid.SetRowSpan(ContentFrame, visible ? 1 : 2);
    }

    // =====================================================================================
    //  startup
    // =====================================================================================

    private void PlaceWindow()
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1500 * scale), area.Width - 32);
        int h = Math.Min((int)(940 * scale), area.Height - 32);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            op.PreferredMinimumWidth = (int)(1100 * scale);
            op.PreferredMinimumHeight = (int)(700 * scale);
        }
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        TitleChips.Margin = new Thickness(0, 0, AppWindow.TitleBar.RightInset / scale + 8, 0);
        Root.Focus(FocusState.Programmatic);

        SetBusy(true);
        RefreshChips("Starting…");
        try
        {
            _ = GpuInfo.LoadAsync().ContinueWith(_ => DispatcherQueue.TryEnqueue(() => RefreshChips()), TaskScheduler.Default);
            await AppServices.LoadSteamAsync();
            await AppServices.StartLibraryAsync();
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't start", ex.Message, InfoBarSeverity.Error, 12);
        }
        finally
        {
            SetBusy(false);
        }
        RefreshChips();

        switch (AppServices.Options.Page)
        {
            case "settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
            case "player": ContentFrame.Navigate(typeof(PlayerPage), new PlayerRequest("sample")); break;
            default: ContentFrame.Navigate(typeof(LibraryPage)); break;
        }
    }

    private bool _scanBusy;

    /// <summary>Wired before the source starts, so the first progress events (the index load, "indexing 3/204") are not missed. Events arrive on the UI thread.</summary>
    private void WireSource(ILibrarySource src)
    {
        src.IndexChanged += (_, _) => RefreshChips(_scanBusy ? _scanText : null);
        src.ScanProgress += (_, p) =>
        {
            // One BusyLine reference for the whole scan, however many progress events there are.
            if (p.Active)
            {
                if (!_scanBusy) { _scanBusy = true; SetBusy(true); }
                _scanText = p.Message;
                RefreshChips(p.Message);
            }
            else
            {
                if (_scanBusy) { _scanBusy = false; SetBusy(false); }
                RefreshChips();
            }
        };
    }

    private string? _scanText;

    // =====================================================================================
    //  navigation
    // =====================================================================================

    public void NavigateToPlayer(PlayerRequest request) =>
        ContentFrame.Navigate(typeof(PlayerPage), request, new DrillInNavigationTransitionInfo());

    public void NavigateToLibrary()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        else ContentFrame.Navigate(typeof(LibraryPage));
    }

    private void Nav_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingNav || sender.SelectedItem is not { } item) return;
        var target = item.Tag as string == "settings" ? typeof(SettingsPage) : typeof(LibraryPage);
        if (ContentFrame.CurrentSourcePageType != target)
            ContentFrame.Navigate(target, null, new EntranceNavigationTransitionInfo());
    }

    private void SyncNav(Type page)
    {
        _syncingNav = true;
        Nav.SelectedItem = page == typeof(LibraryPage) ? NavLibrary : page == typeof(SettingsPage) ? NavSettings : null;
        _syncingNav = false;
    }

    // =====================================================================================
    //  title bar chips
    // =====================================================================================

    public void RefreshChips(string? status = null)
    {
        TitleChips.Children.Clear();
        if (status is not null) AddChip(status, Windows.UI.Color.FromArgb(255, 37, 99, 235));

        var index = AppServices.Library?.Current;
        if (index is not null)
        {
            var bytes = index.Recordings.Sum(r => r.SizeBytes);
            AddChip($"{index.Recordings.Count} recordings · {FormatBytes(bytes)}", Windows.UI.Color.FromArgb(255, 34, 197, 94));
        }
        else AddChip("No library yet", Windows.UI.Color.FromArgb(255, 156, 163, 175));

        // The adapter DXGI hands to D3D11 by default; "CPU decoding" when the setting says so.
        AddChip(AppServices.Settings.Decoding == Core.DecodeMode.Cpu ? "CPU decoding" : GpuInfo.Name ?? "GPU decoding", Windows.UI.Color.FromArgb(255, 156, 163, 175));
    }

    private void AddChip(string text, Windows.UI.Color dotColor)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(dotColor), VerticalAlignment = VerticalAlignment.Center };
        TitleChips.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 3, 10, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0x80, 0x80, 0x80)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { dot, new TextBlock { Text = text, FontSize = 12 } },
            },
        });
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        _ => (b / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB",
    };

    // =====================================================================================
    //  toasts
    // =====================================================================================

    public void ShowToast(string title, string? message, InfoBarSeverity severity, int seconds = 6)
    {
        var bar = new InfoBar
        {
            Title = title,
            Message = message ?? "",
            Severity = severity,
            IsOpen = true,
            IsClosable = true,
            Background = Brush("SolidBackgroundFillColorTertiaryBrush"),
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 24),
        };
        bar.Closed += (_, _) => ToastHost.Children.Remove(bar);
        ToastHost.Children.Add(bar);
        while (ToastHost.Children.Count > 3) ToastHost.Children.RemoveAt(0);
        if (seconds > 0)
        {
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromSeconds(seconds);
            t.IsRepeating = false;
            t.Tick += (_, _) => bar.IsOpen = false;
            t.Start();
        }
    }

    // =====================================================================================
    //  BusyLine shimmer (reference counted: several tasks may be busy at once)
    // =====================================================================================

    public void SetBusy(bool busy)
    {
        _busy = Math.Max(0, _busy + (busy ? 1 : -1));
        if (_busy > 0) StartShimmer(); else StopShimmer();
    }

    private void StartShimmer()
    {
        if (_shimmer is not null) return;
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 124, 58, 237), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 124, 58, 237), Offset = 0.35 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 6, 182, 212), Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 6, 182, 212), Offset = 0.65 });
        var shift = new TranslateTransform();
        brush.RelativeTransform = shift;
        BusyLine.Fill = brush;
        BusyLine.Opacity = 1;

        var anim = new DoubleAnimation
        {
            From = -0.7, To = 0.7, Duration = TimeSpan.FromSeconds(1.4), RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, shift);
        Storyboard.SetTargetProperty(anim, "X");
        _shimmer = new Storyboard();
        _shimmer.Children.Add(anim);
        _shimmer.Begin();
    }

    private void StopShimmer()
    {
        _shimmer?.Stop();
        _shimmer = null;
        BusyLine.Opacity = 0;
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var b) && b is Brush br
            ? br
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
