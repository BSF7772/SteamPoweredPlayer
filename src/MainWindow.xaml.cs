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

        ContentFrame.Navigated += (_, e) =>
        {
            SyncNav(e.SourcePageType);
            if (e.SourcePageType == typeof(PlayerPage)) CloseToasts(keepErrors: true);
        };
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

    // F10: three chips built once; an update changes only their text. Digits are tabular and a chip's width only ever grows (never
    // shrinks back between updates), so nothing in the title bar moves while a recording grows or a scan reports progress.
    private Chip? _statusChip, _libraryChip, _gpuChip;

    private sealed record Chip(Border Root, TextBlock Text);

    public void RefreshChips(string? status = null)
    {
        if (_statusChip is null)
        {
            _statusChip = AddChip(Windows.UI.Color.FromArgb(255, 37, 99, 235));
            _libraryChip = AddChip(Windows.UI.Color.FromArgb(255, 34, 197, 94));
            _gpuChip = AddChip(Windows.UI.Color.FromArgb(255, 156, 163, 175));
        }

        SetChip(_statusChip, status);
        var index = AppServices.Library?.Current;
        SetChip(_libraryChip!, index is null
            ? "No library yet"
            : $"{index.Recordings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} recordings · {FormatBytes(index.Recordings.Sum(r => r.SizeBytes))}");
        // The adapter DXGI hands to D3D11 by default; "CPU decoding" when the setting says so.
        SetChip(_gpuChip!, AppServices.Settings.Decoding == Core.DecodeMode.Cpu ? "CPU decoding" : GpuInfo.Name ?? "GPU decoding");
    }

    private static void SetChip(Chip chip, string? text)
    {
        if (text is null)
        {
            chip.Root.Visibility = Visibility.Collapsed;
            chip.Text.MinWidth = 0;                       // a new scan starts narrow again
            return;
        }
        if (chip.Text.Text != text) chip.Text.Text = text;
        chip.Root.Visibility = Visibility.Visible;
        // Ratchet: measure the new text and keep the widest width seen, so a shorter text does not pull the chips around.
        chip.Text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = Math.Ceiling(chip.Text.DesiredSize.Width);
        if (w > chip.Text.MinWidth) chip.Text.MinWidth = w;
    }

    private Chip AddChip(Windows.UI.Color dotColor)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(dotColor), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        var root = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 3, 10, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0x80, 0x80, 0x80)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, text } },
        };
        TitleChips.Children.Add(root);
        return new(root, text);
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

    /// <summary>
    /// F6: toasts sit bottom-left, away from the player controls; on the Player page only errors are shown (anything else would cover
    /// the video), and opening the Player closes the toasts that are still up.
    /// </summary>
    public void ShowToast(string title, string? message, InfoBarSeverity severity, int seconds = 6)
    {
        if (severity != InfoBarSeverity.Error && ContentFrame.Content is PlayerPage) return;
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

    private void CloseToasts(bool keepErrors)
    {
        foreach (var bar in ToastHost.Children.OfType<InfoBar>().ToList())
            if (!keepErrors || bar.Severity != InfoBarSeverity.Error) bar.IsOpen = false;
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
