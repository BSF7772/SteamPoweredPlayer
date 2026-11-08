// SPDX-License-Identifier: GPL-3.0-or-later
using FastEncoder.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Services;

namespace SteamPoweredPlayer.Views;

/// <summary>
/// The Player's Export card: the A–B range (or the whole recording) with a FastEncoder preset. An export keeps running when the user
/// leaves the page; its result arrives as a toast.
/// </summary>
public sealed partial class PlayerPage
{
    private const string DefaultExportPreset = "balanced";
    private static bool s_encodersDetected;
    private CancellationTokenSource? _exportCts;
    private bool _exportUiReady;

    private async void InitExportUi()
    {
        if (_exportUiReady) return;
        _exportUiReady = true;
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
        var offered = Presets.All.Where(p => p.Id != "target").ToList();   // "Fit to size" needs a size box: arrives with the editor
        ExportPresetBox.ItemsSource = offered.Select(p => $"{p.Emoji}  {p.Name}").ToList();
        ExportPresetBox.Tag = offered;
        var wanted = AppServices.Settings.ExportPreset ?? DefaultExportPreset;
        ExportPresetBox.SelectedIndex = Math.Max(0, offered.FindIndex(p => p.Id == wanted));
        UpdateExportUi();
    }

    private Preset? SelectedPreset =>
        ExportPresetBox.Tag is List<Preset> offered && ExportPresetBox.SelectedIndex is >= 0 and var i && i < offered.Count ? offered[i] : null;

    private (double Start, double End)? ExportRange()
    {
        if (_media is null) return null;
        if (_ab.Active) return (_ab.A!.Value, _ab.B!.Value);
        return ExportPlan.Whole(_media) is { } whole ? (whole.SnappedStart, whole.SnappedEnd) : null;
    }

    private void UpdateExportUi()
    {
        if (!_exportUiReady) return;
        var folder = AppServices.Settings.ExportFolder;
        ToolTipService.SetToolTip(ExportFolderButton, folder ?? "Choose the export folder");
        if (_media is null || ExportRange() is not { } range || ExportPlan.For(_media, range.Start, range.End) is not { } plan)
        {
            ExportRangeText.Text = "Nothing to export.";
            if (_exportCts is null) ExportButton.IsEnabled = false;
            return;
        }
        if (_exportCts is null) ExportButton.IsEnabled = true;
        var lossless = SelectedPreset?.Id == "trim";
        var (start, end) = lossless ? (plan.SnappedStart, plan.SnappedEnd) : (plan.Start, plan.End);
        var what = _ab.Active ? "A–B range" : "Whole recording";
        var text = $"{what}  ·  {Pipeline.Time(end - start - plan.GapSeconds)}";
        if (lossless && _ab.Active) text += "  ·  lossless cuts snap to 3 s keyframes";
        if (plan.GapSeconds > 0) text += $"  ·  {Pipeline.Time(plan.GapSeconds)} missing on disk";
        ExportRangeText.Text = text;
    }

    private void ExportPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPreset is { } p && p.Id != AppServices.Settings.ExportPreset)
        {
            AppServices.Settings.ExportPreset = p.Id;
            AppServices.Settings.Save();
        }
        UpdateExportUi();
    }

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

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCts is { } running) { running.Cancel(); return; }
        if (_media is not { } media || ExportRange() is not { } range || SelectedPreset is not { } preset) return;

        var folder = AppServices.Settings.ExportFolder;
        if (folder is null || !Directory.Exists(folder)) folder = await ChooseExportFolderAsync();     // the folder is the user's choice: no default
        if (folder is null) return;

        var job = new ExportJob(media, [new ExportSpan(range.Start, range.End)], preset.Build(), new EditSettings(), folder,
            ExportPaths.SafeStem($"{GameText.Text} {DateTime.Now:yyyy-MM-dd HH-mm-ss}"), AppServices.Steam.RecordingsPath);
        using var cts = new CancellationTokenSource();
        _exportCts = cts;
        ExportButtonText.Text = "Cancel export";
        ExportProgressBar.Value = 0;
        ExportProgressBar.Visibility = Visibility.Visible;
        ExportPresetBox.IsEnabled = false;
        var progress = new Progress<ExportProgress>(p =>
        {
            ExportProgressBar.Value = p.Fraction;
            ExportRangeText.Text = $"{p.Stage}  ·  {p.Fraction:P0}";
        });
        try
        {
            var result = await Task.Run(() => ExportService.RunAsync(job, progress, cts.Token), cts.Token);
            AppServices.Toast("Export finished", $"{Path.GetFileName(result.Path)}  ·  {Pipeline.Size(result.Bytes)}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppServices.Toast("Export cancelled", null, InfoBarSeverity.Informational, 3);
        }
        catch (ChunkGoneException gone)
        {
            AppServices.Toast("Export failed", ExportService.DescribeGone(media, gone), InfoBarSeverity.Error);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            PlayerLog.Write("export failed: " + ex.GetType().Name);
            AppServices.Toast("Export failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _exportCts = null;
            ExportButtonText.Text = "Export";
            ExportProgressBar.Visibility = Visibility.Collapsed;
            ExportPresetBox.IsEnabled = true;
            UpdateExportUi();
        }
    }
}
