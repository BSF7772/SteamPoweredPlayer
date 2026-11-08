// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using FastEncoder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SteamPoweredPlayer.Views;

/// <summary>One row of the kept-span list.</summary>
public sealed record KeptSpanRow(ExportSpan Span, string Text)
{
    public KeptSpanRow Self => this;
}

/// <summary>
/// The Player's Export card: what to keep (kept spans, else the A–B range, else the whole recording), the approved quality ladder or a special preset, speed
/// and sound, audio sync, and the queue. Exports live in <see cref="AppServices.Exports"/>, so they outlive this page.
/// </summary>
public sealed partial class PlayerPage
{
    private const double NudgeSeconds = 0.02;
    private static readonly double[] ExportSpeeds = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];
    private static bool s_encodersDetected;

    private readonly ObservableCollection<KeptSpanRow> _kept = [];
    private readonly Dictionary<string, ToggleButton> _choiceButtons = [];
    private readonly Dictionary<LadderEngine, ToggleButton> _engineButtons = [];
    private string _exportChoice = ExportLadder.DefaultId;
    private LadderEngine _engine = LadderEngine.Auto;
    private bool _exportUiReady;
    private bool _exportSubscribed;
    private double _audioDelay;
    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _estimateCts;

    // ---- setup ----------------------------------------------------------------------------------------------------------

    private async void InitExportUi()
    {
        SubscribeExports();
        if (_exportUiReady) return;
        _exportUiReady = true;
        KeptList.ItemsSource = _kept;
        SpeedBox.ItemsSource = ExportSpeeds.Select(s => s.ToString("0.##", CultureInfo.InvariantCulture) + "×").ToList();
        SpeedBox.SelectedIndex = Array.IndexOf(ExportSpeeds, 1.0);
        if (!s_encodersDetected)
        {
            ExportButton.IsEnabled = false;
            try
            {
                if (!Ff.Available) Ff.Locate();
                if (Ff.Available) await Encoders.DetectAsync();          // each GPU encoder is proven by a test encode before it's offered
                s_encodersDetected = true;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
            ExportButton.IsEnabled = true;
        }
        if (ExportLadder.IsKnown(AppServices.Settings.ExportPreset)) _exportChoice = AppServices.Settings.ExportPreset!;
        if (Enum.TryParse<LadderEngine>(AppServices.Settings.ExportEngine, out var engine)) _engine = engine;
        BuildChoiceTiles();
        SizeChanged += (_, _) => FitExportLayout();
        FitExportLayout();
        UpdateExportUi();
    }

    private static ToggleButton ChoiceTile(string automationId, params (string Text, double Size, bool Strong)[] lines)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (text, size, strong) in lines)
            panel.Children.Add(new TextBlock
            {
                Text = text, FontSize = size, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            });
        var tile = new ToggleButton { Content = panel, Padding = new Thickness(3, 6, 3, 6), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsTabStop = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tile, automationId);
        return tile;
    }

    /// <summary>The approved mockup: five tiers from quality to speed, the engine switch, and the special presets.</summary>
    private void BuildChoiceTiles()
    {
        for (var i = 0; i < ExportLadder.Tiers.Count; i++)
        {
            var tier = ExportLadder.Tiers[i];
            TierGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var lines = new List<(string, double, bool)> { (tier.Name, 11, true), (tier.Tagline, 10, false) };
            if (tier.CpuOnly) lines.Add(("CPU only", 10, false));
            if (tier.Id == ExportLadder.DefaultId) lines.Add(("default", 10, true));
            var tile = ChoiceTile("ExportTier-" + tier.Id, [.. lines]);
            tile.Click += (_, _) => SelectExportChoice(tier.Id);
            Grid.SetColumn(tile, i);
            TierGrid.Children.Add(tile);
            _choiceButtons[tier.Id] = tile;
        }
        (LadderEngine Engine, string Label)[] engines = [(LadderEngine.Auto, "Auto (GPU if present)"), (LadderEngine.Cpu, "CPU"), (LadderEngine.Gpu, "GPU")];
        EngineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        EngineGrid.ColumnDefinitions.Add(new ColumnDefinition());
        EngineGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < engines.Length; i++)
        {
            var engine = engines[i].Engine;
            var tile = ChoiceTile("ExportEngine-" + engine, (engines[i].Label, 11, false));
            tile.Click += (_, _) => { _engine = engine; AppServices.Settings.ExportEngine = engine.ToString(); AppServices.Settings.Save(); OptionChanged(); };
            Grid.SetColumn(tile, i);
            EngineGrid.Children.Add(tile);
            _engineButtons[engine] = tile;
        }
        SpecialGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SpecialGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SpecialGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SpecialGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SpecialGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < ExportLadder.SpecialIds.Count; i++)
        {
            var id = ExportLadder.SpecialIds[i];
            if (Presets.Get(id) is not { } preset) continue;
            var tile = ChoiceTile("ExportSpecial-" + id, ($"{preset.Emoji} {preset.Name}", 11, false));
            ToolTipService.SetToolTip(tile, preset.Tagline);
            tile.Click += (_, _) => SelectExportChoice(id);
            Grid.SetColumn(tile, i % 3);
            Grid.SetRow(tile, i / 3);
            SpecialGrid.Children.Add(tile);
            _choiceButtons[id] = tile;
        }
    }

    private void SelectExportChoice(string id)
    {
        _exportChoice = id;
        AppServices.Settings.ExportPreset = id;
        AppServices.Settings.Save();
        OptionChanged();
    }

    /// <summary>The edits leave a short window to the video, and the export card never squeezes the Events list away.</summary>
    private void FitExportLayout()
    {
        EditsCard.Visibility = !_fullscreen && ActualHeight is 0 or >= 680 ? Visibility.Visible : Visibility.Collapsed;
        if (RightPanel.ActualHeight > 0) ExportScroll.MaxHeight = Math.Max(150, RightPanel.ActualHeight - 120 - 12 - 190);
    }

    private void SubscribeExports()
    {
        if (_exportSubscribed) return;
        _exportSubscribed = true;
        AppServices.ExportsChanged += OnExportsChanged;
        Unloaded += (_, _) =>
        {
            AppServices.ExportsChanged -= OnExportsChanged;
            _exportSubscribed = false;
            _syncCts?.Cancel();
            _estimateCts?.Cancel();
        };
        OnExportsChanged();
    }

    /// <summary>A new recording: its cuts don't apply, and the sync starts from what was applied to its game.</summary>
    private void ResetExportEdits()
    {
        _kept.Clear();
        Timeline.SetKept([]);
        _syncCts?.Cancel();
        SetSyncStatus("");
        EstimateText.Text = "";
        var game = _entry?.GameId;
        SetAudioDelay(game is not null && AppServices.Settings.GameAudioDelays.TryGetValue(game, out var d) ? d : 0);
        UpdateExportUi();
    }

    // ---- what the job would be ------------------------------------------------------------------------------------------

    private IReadOnlyList<ExportSpan> ExportSelection()
    {
        if (_media is null) return [];
        if (_kept.Count > 0) return [.. _kept.Select(k => k.Span)];
        if (_ab.Active) return [new ExportSpan(_ab.A!.Value, _ab.B!.Value)];
        return ExportPlan.Whole(_media) is { } whole ? [new ExportSpan(whole.SnappedStart, whole.SnappedEnd)] : [];
    }

    private EncodeSettings BuildSettings(string choice)
    {
        var s = ExportLadder.Tier(choice) is { } tier ? ExportLadder.Build(tier, _engine, Encoders.BestGpu) : Presets.Get(choice)!.Build();
        if (s.TargetSize && double.IsFinite(TargetSizeBox.Value) && TargetSizeBox.Value > 0) s.TargetMB = TargetSizeBox.Value;
        return s;
    }

    private EditSettings BuildEdit() => new()
    {
        Speed = SpeedBox.SelectedIndex >= 0 ? ExportSpeeds[SpeedBox.SelectedIndex] : 1,
        Volume = VolumeEditSlider.Value / 100,
        Mute = MuteEditToggle.IsChecked == true,
        FixSync = FixDriftToggle.IsChecked == true,
    };

    private ExportJob? BuildJob(string choice, string folder)
    {
        if (_media is null) return null;
        var spans = ExportSelection();
        if (spans.Count == 0) return null;
        var chapters = ChaptersToggle.IsOn ? _lane.Select(l => new ExportChapter(l.Source.StartSeconds, l.Title)).ToList() : [];
        return new ExportJob(_media, spans, BuildSettings(choice), BuildEdit(), folder,
            ExportPaths.SafeStem($"{GameText.Text} {DateTime.Now:yyyy-MM-dd HH-mm-ss}"), AppServices.Steam.RecordingsPath, _audioDelay, chapters);
    }

    private void UpdateExportUi()
    {
        if (!_exportUiReady) return;
        ToolTipService.SetToolTip(ExportFolderButton, AppServices.Settings.ExportFolder ?? "Choose the export folder");
        KeepAbButton.IsEnabled = _ab.Active;
        ClearKeptButton.Visibility = _kept.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HighlightsButton.IsEnabled = _lane.Count > 0;

        var lossless = _exportChoice == "trim";
        var gpu = Encoders.BestGpu;
        var tier = ExportLadder.Tier(_exportChoice);
        foreach (var (id, button) in _choiceButtons) button.IsChecked = id == _exportChoice;
        foreach (var (engine, button) in _engineButtons)
        {
            button.IsChecked = engine == _engine;
            button.IsEnabled = tier is not null && (engine != LadderEngine.Gpu || (gpu is not null && !tier.CpuOnly));
        }
        if (tier is not null)
        {
            var usesGpu = ExportLadder.UsesGpu(tier, _engine, gpu);
            var nvenc = gpu?.Id.EndsWith("_nvenc", StringComparison.Ordinal) == true;
            ChoiceInfoText.Text = $"{tier.Name} · {(usesGpu ? "GPU" : "CPU")}: " + (usesGpu ? (nvenc ? tier.GpuInfo : gpu!.Label) : tier.CpuInfo);
        }
        else if (Presets.Get(_exportChoice) is { } special) ChoiceInfoText.Text = $"{special.Name}: {special.Tagline}";
        TargetSizeBox.Visibility = _exportChoice == "target" ? Visibility.Visible : Visibility.Collapsed;
        SpeedBox.IsEnabled = VolumeEditSlider.IsEnabled = MuteEditToggle.IsEnabled = FixDriftToggle.IsEnabled = !lossless;
        EditsTitle.Text = lossless ? "Export edits (sync only with Trim only)" : "Export edits";

        var plans = _media is null ? [] : ExportSpans.Plans(_media, ExportSelection(), lossless);
        var ready = plans.Count > 0;
        ExportButton.IsEnabled = ready;
        SaveClipButton.IsEnabled = ready;
        DetectButton.IsEnabled = ready;
        EstimateButton.IsEnabled = ready;
        if (!ready)
        {
            ExportRangeText.Text = "Nothing to export.";
            return;
        }
        var what = _kept.Count > 0 ? $"{plans.Count} kept part{(plans.Count == 1 ? "" : "s")}" : _ab.Active ? "A–B range" : "Whole recording";
        var length = ExportSpans.OutputSeconds(plans, lossless ? 1 : BuildEdit().Speed);
        var text = $"{what}  ·  {Pipeline.Time(length)}";
        if (lossless && (_ab.Active || _kept.Count > 0)) text += "  ·  lossless cuts snap to 3 s keyframes";
        var gaps = plans.Sum(p => p.GapSeconds);
        if (gaps > 0) text += $"  ·  {Pipeline.Time(gaps)} missing on disk";
        ExportRangeText.Text = text;
    }

    private void ExportOption_Changed(object sender, RoutedEventArgs e) => OptionChanged();

    private void ExportOption_SelectionChanged(object sender, SelectionChangedEventArgs e) => OptionChanged();

    private void OptionChanged()
    {
        if (!_exportUiReady) return;
        EstimateText.Text = "";
        UpdateExportUi();
    }

    private void TargetSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => OptionChanged();

    private void VolumeEdit_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => OptionChanged();

    // ---- cuts -----------------------------------------------------------------------------------------------------------

    private void SetKept(IEnumerable<ExportSpan> spans)
    {
        _kept.Clear();
        foreach (var s in ExportSpans.Normalize(spans))
        {
            var ui = _session is { } session ? (session.MediaToUi(s.Start), session.MediaToUi(s.End)) : (s.Start, s.End);
            _kept.Add(new KeptSpanRow(s, $"{Pipeline.Time(ui.Item1)}  →  {Pipeline.Time(ui.Item2)}"));
        }
        DrawKept();
        OptionChanged();
    }

    private void DrawKept() =>
        Timeline.SetKept(_session is { } session ? [.. _kept.Select(k => (session.MediaToUi(k.Span.Start), session.MediaToUi(k.Span.End)))] : []);

    private void KeepAb_Click(object sender, RoutedEventArgs e)
    {
        if (!_ab.Active) return;
        var span = new ExportSpan(_ab.A!.Value, _ab.B!.Value);
        SetKept(_kept.Select(k => k.Span).Append(span));
        _ab.Clear();
        ApplyAbLoop();
        UpdateLoopUi();                 // ready for the next range
    }

    private void RemoveKept_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: KeptSpanRow row }) SetKept(_kept.Where(k => k != row).Select(k => k.Span));
    }

    private void ClearKept_Click(object sender, RoutedEventArgs e) => SetKept([]);

    private void Highlights_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null) return;
        SetKept(ExportSpans.Highlights(_media, _lane.Select(l => l.Source.StartSeconds)));
    }

    // ---- audio sync -----------------------------------------------------------------------------------------------------

    private void SetAudioDelay(double seconds)
    {
        _audioDelay = Math.Round(seconds, 3);
        AudioDelayText.Text = string.Create(CultureInfo.InvariantCulture, $"Sync {_audioDelay * 1000:+0;-0;0} ms");
        if (Math.Abs(SyncSlider.Value - _audioDelay * 1000) > 0.5) SyncSlider.Value = _audioDelay * 1000;
        _host?.Mpv?.SetAudioDelay(_audioDelay);                         // hear it live
        var game = _entry?.GameId;
        ApplyToGameToggle.IsChecked = game is not null && AppServices.Settings.GameAudioDelays.TryGetValue(game, out var saved) && Math.Abs(saved - _audioDelay) < 0.0005;
    }

    private void SyncSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (Math.Abs(e.NewValue / 1000 - _audioDelay) > 0.0005) SetAudioDelay(e.NewValue / 1000);
    }

    private void SetSyncStatus(string text)
    {
        SyncStatusText.Text = text;
        SyncStatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NudgeEarlier_Click(object sender, RoutedEventArgs e) => SetAudioDelay(_audioDelay - NudgeSeconds);

    private void NudgeLater_Click(object sender, RoutedEventArgs e) => SetAudioDelay(_audioDelay + NudgeSeconds);

    private void NudgeReset_Click(object sender, RoutedEventArgs e) => SetAudioDelay(0);

    private void ApplyToGame_Toggled(object sender, RoutedEventArgs e)
    {
        if (_entry?.GameId is not { } game) return;
        var delays = AppServices.Settings.GameAudioDelays;
        if (ApplyToGameToggle.IsChecked == true) delays[game] = _audioDelay;
        else delays.Remove(game);
        AppServices.Settings.Save();
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (_syncCts is { } running) { running.Cancel(); return; }
        if (_media is not { } media || ExportSelection() is not { Count: > 0 } spans) return;
        var span = spans.MaxBy(s => s.Length);
        using var cts = new CancellationTokenSource();
        _syncCts = cts;
        DetectButton.Content = "Cancel";
        SetSyncStatus("Analysing…");
        var progress = new Progress<double>(f => SetSyncStatus($"Analysing…  {f:P0}"));
        try
        {
            var result = await Task.Run(() => ExportService.DetectSyncAsync(media, span, f => ((IProgress<double>)progress).Report(f), cts.Token), cts.Token);
            var advice = SyncAdvice.From(result);
            switch (advice.Verdict)
            {
                case SyncVerdict.Fix:
                    SetAudioDelay(advice.AudioDelay);
                    SetSyncStatus(string.Create(CultureInfo.InvariantCulture, $"Sound was {advice.AudioLate * 1000:+0;-0} ms off: corrected. Listen, then nudge if needed."));
                    break;
                case SyncVerdict.InSync:
                    SetSyncStatus("In sync: nothing to change.");
                    break;
                default:
                    SetSyncStatus("Low confidence: not applied. Nudge by ear if it sounds off.");
                    break;
            }
        }
        catch (OperationCanceledException) { SetSyncStatus(""); }
        catch (ChunkGoneException gone) { SetSyncStatus(ExportService.DescribeGone(media, gone)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            PlayerLog.Write("sync detect failed: " + ex.GetType().Name);
            SetSyncStatus("Couldn't analyse this range: " + ex.Message);
        }
        finally
        {
            _syncCts = null;
            DetectButton.Content = "Detect";
        }
    }

    // ---- estimate -------------------------------------------------------------------------------------------------------

    private async void Estimate_Click(object sender, RoutedEventArgs e)
    {
        if (_estimateCts is { } running) { running.Cancel(); return; }
        if (BuildJob(_exportChoice, Path.GetTempPath()) is not { } job) return;
        using var cts = new CancellationTokenSource();
        _estimateCts = cts;
        EstimateButton.Content = "Cancel";
        EstimateText.Text = "Estimating…";
        try
        {
            var bytes = await Task.Run(() => ExportService.EstimateAsync(job, cts.Token), cts.Token);
            EstimateText.Text = job.Lossless ? Pipeline.Size(bytes) : "≈ " + Pipeline.Size(bytes);
        }
        catch (OperationCanceledException) { EstimateText.Text = ""; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            PlayerLog.Write("estimate failed: " + ex.GetType().Name);
            EstimateText.Text = "Couldn't estimate this range.";
        }
        finally
        {
            _estimateCts = null;
            EstimateButton.Content = "Estimate size";
        }
    }

    // ---- folder, export, save clip --------------------------------------------------------------------------------------

    private async void ExportFolder_Click(object sender, RoutedEventArgs e) => await ChooseExportFolderAsync();

    private async Task<string?> ChooseExportFolderAsync()
    {
        var path = await FirstRunCard.PickFolderAsync();
        if (path is null) return null;
        if (ExportPaths.IsInside(path, AppServices.Steam.RecordingsPath))
        {
            AppServices.Toast("Choose another folder", "Exports can't be saved inside Steam's recording folder.", InfoBarSeverity.Warning);
            return null;
        }
        AppServices.Settings.ExportFolder = path;
        AppServices.Settings.Save();
        UpdateExportUi();
        return path;
    }

    private async Task<string?> ExportFolderAsync()
    {
        var folder = AppServices.Settings.ExportFolder;
        return folder is not null && Directory.Exists(folder) ? folder : await ChooseExportFolderAsync();     // the user's choice: no default
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        await EnqueueAsync(_exportChoice);
    }

    /// <summary>Save clip: lossless, with the markers as chapters, whatever the preset box says.</summary>
    private async void SaveClip_Click(object sender, RoutedEventArgs e)
    {
        await EnqueueAsync("trim");
    }

    private async Task EnqueueAsync(string choice)
    {
        if (await ExportFolderAsync() is not { } folder || BuildJob(choice, folder) is not { } job) return;
        AppServices.Exports.Enqueue(job, job.Stem);
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => AppServices.Exports.CancelAll();

    private void OnExportsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(OnExportsChanged); return; }
        var queue = AppServices.Exports;
        var items = queue.Items;
        var running = items.FirstOrDefault(i => i.State == ExportItemState.Running);
        var waiting = items.Count(i => i.State == ExportItemState.Waiting);
        var busy = running is not null || waiting > 0;
        ExportProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelExportButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ExportProgressBar.Value = running?.Fraction ?? 0;
        ExportQueueText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            var stage = running is null ? "Starting" : $"{running.Stage}  ·  {running.Fraction:P0}";
            ExportQueueText.Text = waiting > 0 ? $"{stage}  ·  {waiting} waiting" : stage;
        }
        var last = items.LastOrDefault(i => i.State == ExportItemState.Done && File.Exists(i.Result?.Path));
        OpenFastEncoderButton.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- open in FastEncoder --------------------------------------------------------------------------------------------

    private async void OpenFastEncoder_Click(object sender, RoutedEventArgs e)
    {
        var last = AppServices.Exports.Items.LastOrDefault(i => i.State == ExportItemState.Done && File.Exists(i.Result?.Path));
        if (last?.Result is not { } result) return;
        var exe = AppServices.Settings.FastEncoderPath;
        if (exe is null || !File.Exists(exe))
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, AppServices.Window!.Hwnd);
            picker.FileTypeFilter.Add(".exe");
            exe = (await picker.PickSingleFileAsync())?.Path;
            if (exe is null) return;
            AppServices.Settings.FastEncoderPath = exe;
            AppServices.Settings.Save();
        }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add(result.Path);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppServices.Toast("Couldn't open FastEncoder", ex.Message, InfoBarSeverity.Error);
        }
    }

    // ---- screenshot -----------------------------------------------------------------------------------------------------

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        await TakeScreenshotAsync();
        Focus(FocusState.Programmatic);
    }

    private async Task TakeScreenshotAsync()
    {
        if (_host?.Mpv is not { } mpv || await ExportFolderAsync() is not { } folder) return;
        var path = ExportPaths.Unique(folder, ExportPaths.SafeStem($"{GameText.Text} {DateTime.Now:yyyy-MM-dd HH-mm-ss}"), ".png");
        try
        {
            await mpv.ScreenshotToFileAsync(path);
            AppServices.Toast("Screenshot saved", Path.GetFileName(path), InfoBarSeverity.Success, 3);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or MpvException or ObjectDisposedException)
        {
            PlayerLog.Write("screenshot failed: " + ex.GetType().Name);
            AppServices.Toast("Couldn't save the screenshot", ex.Message, InfoBarSeverity.Error);
        }
    }
}
