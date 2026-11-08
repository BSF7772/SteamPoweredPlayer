// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using FastEncoder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Services;

namespace SteamPoweredPlayer.Views;

/// <summary>One row of the kept-span list.</summary>
public sealed record KeptSpanRow(ExportSpan Span, string Text)
{
    public KeptSpanRow Self => this;
}

/// <summary>
/// The Player's Export card: what to keep (kept spans, else the A–B range, else the whole recording), the approved quality ladder or a special preset, audio
/// sync, Share and the queue. Speed, mute and volume are the player's own controls: one setting each, heard before exporting.
/// Exports live in <see cref="AppServices.Exports"/>, so they outlive this page.
/// </summary>
public sealed partial class PlayerPage
{
    private const double NudgeSeconds = 0.02;
    private const double SyncLimitSeconds = 5;
    private static readonly TimeSpan SyncApplyDelay = TimeSpan.FromSeconds(1);
    private static bool s_encodersDetected;
    private static bool s_warnedThisRun;

    private readonly ObservableCollection<KeptSpanRow> _kept = [];
    private readonly Dictionary<string, ToggleButton> _choiceButtons = [];
    private readonly Dictionary<LadderEngine, ToggleButton> _engineButtons = [];
    private string _exportChoice = ExportLadder.DefaultId;
    private LadderEngine _engine = LadderEngine.Auto;
    private bool _exportUiReady;
    private bool _exportSubscribed;
    private double _audioDelay;
    private bool _syncUiBusy, _syncPending, _warningOpen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _syncApplyTimer, _syncDoneTimer;
    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _estimateCts;

    // ---- setup ----------------------------------------------------------------------------------------------------------

    private async void InitExportUi()
    {
        SubscribeExports();
        if (_exportUiReady) return;
        _exportUiReady = true;
        KeptList.ItemsSource = _kept;
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
        Lower.SizeChanged += (_, _) => FitExportLayout();
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
        var tile = new ToggleButton { Content = panel, Padding = new Thickness(1, 6, 1, 6), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsTabStop = false };
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
            var lines = new List<(string, double, bool)> { (tier.Name, 10, true), (tier.Tagline, 9, false) };
            if (tier.CpuOnly) lines.Add(("CPU only", 9, false));
            if (tier.Id == ExportLadder.DefaultId) lines.Add(("default", 9, true));
            var tile = ChoiceTile("ExportTier-" + tier.Id, [.. lines]);
            WireTile(tile, () => _exportChoice == tier.Id, () => SelectExportChoice(tier.Id));
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
            WireTile(tile, () => _engine == engine, () => { _engine = engine; AppServices.Settings.ExportEngine = engine.ToString(); AppServices.Settings.Save(); OptionChanged(); });
            Grid.SetColumn(tile, i);
            EngineGrid.Children.Add(tile);
            _engineButtons[engine] = tile;
        }
        SpecialGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SpecialGrid.ColumnDefinitions.Add(new ColumnDefinition());
        SpecialGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < ExportLadder.SpecialIds.Count; i++)
        {
            var id = ExportLadder.SpecialIds[i];
            if (Presets.Get(id) is not { } preset) continue;
            var tile = ChoiceTile("ExportSpecial-" + id, ($"{preset.Emoji} {preset.Name}", 11, false));
            ToolTipService.SetToolTip(tile, preset.Tagline);
            WireTile(tile, () => _exportChoice == id, () => SelectExportChoice(id));
            Grid.SetColumn(tile, i);
            SpecialGrid.Children.Add(tile);
            _choiceButtons[id] = tile;
        }
    }

    /// <summary>Radio behaviour on a toggle: checking selects; unchecking the selected tile puts its check back.</summary>
    private static void WireTile(ToggleButton tile, Func<bool> isSelected, Action select)
    {
        tile.Checked += (_, _) => { if (!isSelected()) select(); };
        tile.Unchecked += (_, _) => { if (isSelected()) tile.IsChecked = true; };
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
        SyncCard.Visibility = !_fullscreen && ActualHeight is 0 or >= 680 ? Visibility.Visible : Visibility.Collapsed;
        SyncSlider.Visibility = Lower.ActualWidth is 0 or >= 680 ? Visibility.Visible : Visibility.Collapsed;   // the typed value and the nudges stay
        if (RightPanel.ActualHeight > 0) ExportScroll.MaxHeight = Math.Max(150, RightPanel.ActualHeight - 120 - 12 - 240);
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
            FlushSync();
            _syncDoneTimer?.Stop();
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
        _syncApplyTimer?.Stop();
        _syncPending = false;
        ShowSyncState(busy: false, applied: null);
        ShowAudioDelay(game is not null && AppServices.Settings.GameAudioDelays.TryGetValue(game, out var d) ? d : 0);
        _host?.Mpv?.SetAudioDelay(_audioDelay);
        UpdateExportUi();
    }

    // ---- what the job would be ------------------------------------------------------------------------------------------

    private IReadOnlyList<ExportSpan> ExportSelection()
    {
        if (_media is null) return [];
        if (_kept.Count > 0) return [.. _kept.Select(k => k.Span)];
        if (_ab.Active) return [new ExportSpan(_ab.A!.Value, _ab.B!.Value)];
        return ExportPlan.Whole(_media) is { } whole ? [new ExportSpan(whole.SnappedStart, whole.End)] : [];
    }

    private EncodeSettings BuildSettings(string choice)
    {
        var s = ExportLadder.Tier(choice) is { } tier ? ExportLadder.Build(tier, _engine, Encoders.BestGpu, Encoders.BestHardware(Family.H264)) : Presets.Get(choice)!.Build();
        if (s.TargetSize && double.IsFinite(TargetSizeBox.Value) && TargetSizeBox.Value > 0) s.TargetMB = TargetSizeBox.Value;
        return s;
    }

    /// <summary>The edits are the player's settings at this moment: what you hear is what you export.</summary>
    private EditSettings BuildEdit()
    {
        var mpv = _host?.Mpv;
        return new EditSettings
        {
            Speed = Speeds[_speedIndex],
            Volume = mpv is null ? 1 : Math.Clamp(mpv.Volume / 100, 0, 1),
            Mute = mpv?.Muted == true,
            FixSync = FixDriftToggle.IsOn,
        };
    }

    /// <summary>"2×, no sound"; empty when the player is at its neutral settings.</summary>
    private static string PlayerSettingsText(EditSettings edit)
    {
        var parts = new List<string>();
        if (Math.Abs(edit.Speed - 1) > 0.001) parts.Add(edit.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×");
        if (edit.Mute) parts.Add("no sound");
        else if (edit.Volume < 0.995) parts.Add(edit.Volume.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "") + " volume");
        return string.Join(", ", parts);
    }

    private ExportJob? BuildJob(string choice, string folder)
    {
        if (_media is null) return null;
        FlushSync();
        var spans = ExportSelection();
        if (spans.Count == 0) return null;
        var chapters = ChaptersToggle.IsOn ? _lane.Select(l => new ExportChapter(l.Source.StartSeconds, l.Title)).ToList() : [];
        return new ExportJob(_media, spans, BuildSettings(choice), BuildEdit(), folder,
            ExportPaths.SafeStem($"{GameText.Text} {DateTime.Now:yyyy-MM-dd HH-mm-ss}") + ExportLadder.FileSuffix(choice), AppServices.Steam.RecordingsPath, _audioDelay, chapters);
    }

    private void UpdateExportUi()
    {
        if (!_exportUiReady) return;
        var folder = AppServices.Settings.ExportFolder;
        ExportFolderText.Text = folder is null ? "Saves to: asked on your first export" : "Saves to " + folder;
        ToolTipService.SetToolTip(ExportFolderText, folder ?? "No folder chosen yet");
        KeepAbButton.IsEnabled = _ab.Active;
        ClearKeptButton.Visibility = _kept.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HighlightsButton.IsEnabled = _lane.Count > 0;

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
            var shown = tier.Id == ExportLadder.ShareId ? Encoders.BestHardware(Family.H264) ?? gpu : gpu;
            ChoiceInfoText.Text = $"{tier.Name} · {(usesGpu ? "GPU" : "CPU")}: " + (usesGpu ? (nvenc ? tier.GpuInfo : shown!.Label) : tier.CpuInfo);
        }
        else if (Presets.Get(_exportChoice) is { } special) ChoiceInfoText.Text = $"{special.Name}: {special.Tagline}";
        TargetSizeBox.Visibility = _exportChoice == "target" ? Visibility.Visible : Visibility.Collapsed;
        SpecialExpander.Header = Presets.Get(_exportChoice) is { } picked && tier is null ? "Special purpose · " + picked.Name : "Special purpose";
        if (tier is null) SpecialExpander.IsExpanded = true;

        var plans = _media is null ? [] : ExportSpans.Plans(_media, ExportSelection(), lossless: false);
        var ready = plans.Count > 0;
        ExportButton.IsEnabled = ready;
        SaveClipButton.IsEnabled = ready;
        ShareButton.IsEnabled = ready;
        DetectButton.IsEnabled = ready;
        EstimateButton.IsEnabled = ready;
        if (!ready)
        {
            ExportRangeText.Text = "Nothing to export.";
            return;
        }
        var what = _kept.Count > 0 ? $"{plans.Count} kept part{(plans.Count == 1 ? "" : "s")}" : _ab.Active ? "A–B range" : "Whole recording";
        var edit = BuildEdit();
        var text = $"{what}  ·  {Pipeline.Time(ExportSpans.OutputSeconds(plans, edit.Speed))}";
        if (PlayerSettingsText(edit) is { Length: > 0 } player) text += "  ·  player: " + player;
        var gaps = plans.Sum(p => p.GapSeconds);
        if (gaps > 0) text += $"  ·  {Pipeline.Time(gaps)} missing on disk";
        ExportRangeText.Text = text;
    }

    private void ExportOption_Changed(object sender, RoutedEventArgs e) => OptionChanged();

    private void OptionChanged()
    {
        if (!_exportUiReady) return;
        EstimateText.Text = "";
        UpdateExportUi();
    }

    private void TargetSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => OptionChanged();

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

    /// <summary>
    /// A new sync value: exports use it at once; playback and the game's saved value follow 1 s after the last change, so dragging or
    /// holding a nudge doesn't make mpv re-sync on every step. The ring shows it's on its way, the check mark that it arrived.
    /// </summary>
    private void SetAudioDelay(double seconds)
    {
        ShowAudioDelay(seconds);
        _syncPending = true;
        ShowSyncState(busy: true, applied: null);
        if (_syncApplyTimer is null)
        {
            _syncApplyTimer = DispatcherQueue.CreateTimer();
            _syncApplyTimer.IsRepeating = false;
            _syncApplyTimer.Interval = SyncApplyDelay;
            _syncApplyTimer.Tick += (_, _) => FlushSync();
        }
        _syncApplyTimer.Stop();
        _syncApplyTimer.Start();
    }

    private void ShowAudioDelay(double seconds)
    {
        _audioDelay = Math.Round(Math.Clamp(seconds, -SyncLimitSeconds, SyncLimitSeconds), 3);
        _syncUiBusy = true;
        var ms = _audioDelay * 1000;
        if (Math.Abs(SyncSlider.Value - ms) > 0.5) SyncSlider.Value = ms;
        if (double.IsNaN(SyncBox.Value) || Math.Abs(SyncBox.Value - ms) > 0.5) SyncBox.Value = ms;
        _syncUiBusy = false;
    }

    /// <summary>Applies a waiting sync value now: to playback, and to this game's next recordings (0 forgets it).</summary>
    private void FlushSync()
    {
        _syncApplyTimer?.Stop();
        if (!_syncPending) return;
        _syncPending = false;
        var mpv = _host?.Mpv;
        mpv?.SetAudioDelay(_audioDelay);
        if (_entry?.GameId is { } game)
        {
            var delays = AppServices.Settings.GameAudioDelays;
            if (Math.Abs(_audioDelay) < 0.0005) delays.Remove(game);
            else delays[game] = _audioDelay;
            AppServices.Settings.Save();
        }
        // Asks mpv now: its AudioDelay property follows by an event, a moment later.
        ShowSyncState(busy: false, applied: mpv is null || Math.Abs(mpv.GetDouble("audio-delay") - _audioDelay) < 0.002);
    }

    /// <param name="applied">True shows a check mark for a moment, false a warning that stays until the next change, null nothing.</param>
    private void ShowSyncState(bool busy, bool? applied)
    {
        SyncBusyRing.IsActive = busy;
        SyncBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SyncAppliedIcon.Visibility = applied is null ? Visibility.Collapsed : Visibility.Visible;
        _syncDoneTimer?.Stop();
        if (applied is null) return;
        SyncAppliedIcon.Glyph = applied.Value ? "\uE73E" : "\uE7BA";
        ToolTipService.SetToolTip(SyncAppliedIcon, applied.Value ? "Applied" : "Playback didn't take this value");
        if (!applied.Value) return;
        if (_syncDoneTimer is null)
        {
            _syncDoneTimer = DispatcherQueue.CreateTimer();
            _syncDoneTimer.IsRepeating = false;
            _syncDoneTimer.Interval = TimeSpan.FromSeconds(1.5);
            _syncDoneTimer.Tick += (_, _) => SyncAppliedIcon.Visibility = Visibility.Collapsed;
        }
        _syncDoneTimer.Start();
    }

    private void SyncSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncUiBusy && Math.Abs(e.NewValue / 1000 - _audioDelay) > 0.0005) SetAudioDelay(e.NewValue / 1000);
    }

    private void SyncBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncUiBusy) return;
        if (double.IsNaN(args.NewValue)) { ShowAudioDelay(_audioDelay); return; }       // an emptied box goes back to the value in use
        if (Math.Abs(args.NewValue / 1000 - _audioDelay) > 0.0005) SetAudioDelay(args.NewValue / 1000);
    }

    private void SetSyncStatus(string text)
    {
        SyncStatusText.Text = text;
        SyncStatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NudgeEarlier_Click(object sender, RoutedEventArgs e) => SetAudioDelay(_audioDelay - NudgeSeconds);

    private void NudgeLater_Click(object sender, RoutedEventArgs e) => SetAudioDelay(_audioDelay + NudgeSeconds);

    private void NudgeReset_Click(object sender, RoutedEventArgs e) => SetAudioDelay(0);

    /// <summary>For <c>--uicheck</c>.</summary>
    internal string? CurrentGameId => _entry?.GameId;

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
        await EnqueueAsync(SaveClipChoice);
    }

    private async Task EnqueueAsync(string choice)
    {
        if (choice != SaveClipChoice && !await ConfirmPlayerSettingsAsync()) return;
        if (await ExportFolderAsync() is not { } folder || BuildJob(choice, folder) is not { } job) return;
        AppServices.Exports.Enqueue(job, job.Stem);
    }

    /// <summary>
    /// Share: the sharing tier into a private temp folder, then Windows' share window (<see cref="AppServices.Share"/>), then the copy goes.
    /// No chapters: a data track is one more thing a social network can refuse.
    /// </summary>
    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmPlayerSettingsAsync()) return;
        if (BuildJob(ExportLadder.ShareId, ShareSheet.NewFolder()) is not { } job) return;
        AppServices.Share(job with { Chapters = null, Stem = ExportPaths.SafeStem($"{GameText.Text} {DateTime.Now:yyyy-MM-dd HH-mm-ss}") });
    }

    /// <summary>The first export of a run says that the player's settings go into the file, until the user ticks "Don't show this again".</summary>
    private async Task<bool> ConfirmPlayerSettingsAsync()
    {
        if (s_warnedThisRun || AppServices.Settings.SkipPlayerSettingsWarning) return true;
        if (_warningOpen) return false;
        _warningOpen = true;
        var again = new CheckBox { Content = "Don't show this again" };
        var now = PlayerSettingsText(BuildEdit());
        var dialog = new ContentDialog
        {
            Title = "The player's settings go into the export",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Speed, mute and volume on the player's controls also apply to the exported file, and so does the audio sync. Only Save clip keeps the original speed and sound." },
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = now.Length > 0 ? "Right now: " + now + "." : "Right now they are at normal speed and full volume." },
                    again,
                },
            },
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        }
        finally { _warningOpen = false; }
        s_warnedThisRun = true;
        if (again.IsChecked == true)
        {
            AppServices.Settings.SkipPlayerSettingsWarning = true;
            AppServices.Settings.Save();
        }
        return true;
    }

    /// <summary>FastEncoder's stream copy: what Save clip is made of.</summary>
    private const string SaveClipChoice = "trim";

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
            var stage = running is null ? "Starting" : (AppServices.IsShare(running) ? "Preparing to share  ·  " : "") + $"{running.Stage}  ·  {running.Fraction:P0}";
            ExportQueueText.Text = waiting > 0 ? $"{stage}  ·  {waiting} waiting" : stage;
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
            AppServices.Toast("Screenshot saved", Path.GetFileName(path), InfoBarSeverity.Success, 3, overPlayer: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or MpvException or ObjectDisposedException)
        {
            PlayerLog.Write("screenshot failed: " + ex.GetType().Name);
            AppServices.Toast("Couldn't save the screenshot", ex.Message, InfoBarSeverity.Error);
        }
    }
}
