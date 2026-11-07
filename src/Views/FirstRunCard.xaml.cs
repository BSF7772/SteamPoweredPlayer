// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SteamPoweredPlayer.Views;

/// <summary>First-run card (plan D12): propose the data folder next to Steam's recordings, confirm or choose another.</summary>
public sealed partial class FirstRunCard : UserControl
{
    private string? _candidate;

    /// <summary>Raised after the folder is confirmed and saved.</summary>
    public event EventHandler? Confirmed;

    public FirstRunCard()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var brp = AppServices.Steam.RecordingsPath;
        NoRecordings.IsOpen = brp is null;
        if (brp is null)
        {
            PathText.Text = "—";
            ConfirmButton.IsEnabled = false;
            Problem.IsOpen = false;
            return;
        }
        var proposal = DataFolder.Propose(brp);
        _candidate = proposal.Path;
        Show(proposal);
    }

    private void Show(DataFolderCheck check)
    {
        PathText.Text = check.Path ?? "No folder proposed";
        ConfirmButton.IsEnabled = check.IsValid;
        Problem.IsOpen = !check.IsValid;
        Problem.Message = check.IsValid ? "" : Explain(check.Problem);
        Problem.Title = check.IsValid ? "" : "Can't use this folder";
    }

    internal static string Explain(DataFolderProblem p) => p switch
    {
        DataFolderProblem.InvalidPath => "That is not a valid, full folder path.",
        DataFolderProblem.InsideRecordingPath => "That folder is inside Steam's recording folder. SteamPoweredPlayer never writes there, because Steam trims it on its own.",
        DataFolderProblem.ContainsRecordingPath => "That folder contains Steam's recording folder, which would put Steam's files inside ours.",
        DataFolderProblem.RecordingPathIsRoot => "Steam's recordings sit at the top of a drive, so there is no folder next to them. Choose a folder somewhere else.",
        _ => "",
    };

    internal static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        // Unpackaged apps must tell the picker which window owns it.
        InitializeWithWindow.Initialize(picker, AppServices.Window!.Hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var brp = AppServices.Steam.RecordingsPath;
        var path = await PickFolderAsync();
        if (path is null) return;
        if (brp is null)
        {
            _candidate = path;
            PathText.Text = path;
            ConfirmButton.IsEnabled = true;
            return;
        }
        // The picked folder itself is the data folder: validate it against Steam's recording path (Core's rule).
        var check = DataFolder.Validate(path, brp);
        _candidate = check.IsValid ? check.Path : null;
        Show(check);
    }

    private async void ChooseRecordings_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (path is null) return;
        AppServices.Settings.RecordingsFolder = path;
        AppServices.Settings.Save();
        await AppServices.LoadSteamAsync();
        await AppServices.StartLibraryAsync();     // the running source was created without a recordings folder
        Refresh();
        Confirmed?.Invoke(this, EventArgs.Empty);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var brp = AppServices.Steam.RecordingsPath;
        if (_candidate is null || brp is null) return;
        var check = DataFolder.Validate(_candidate, brp);
        if (!check.IsValid) { Show(check); return; }

        // The library is already running in memory: attaching the folder makes it start saving there, no restart.
        AppServices.ConfirmDataFolder(check.Path!);
        AppServices.Window?.RefreshChips();
        AppServices.Toast("Library folder set", "The first scan will fill it.", InfoBarSeverity.Success, 4);
        Confirmed?.Invoke(this, EventArgs.Empty);
    }
}
