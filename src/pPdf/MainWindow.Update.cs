using System.Windows;
using pPdf.Services;
using pPdf.Views;

namespace pPdf;

/// <summary>Looking for a newer release, downloading it and letting the installer replace the running program.</summary>
public partial class MainWindow
{
    bool _checkingUpdates;

    /// <summary>The check after startup: waits until the window is up so launching stays fast.</summary>
    void ScheduleStartupUpdateCheck()
    {
        if (Environment.GetEnvironmentVariable("PPDF_NO_STARTUP") == "1") return; // UI test harness
        _ = Task.Run(UpdateService.CleanUp);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(3000);
            await CheckForUpdatesAsync(manual: false);
        });
    }

    /// <summary>
    /// Looks for a newer release. The automatic check runs at most once a day, only if enabled,
    /// stays silent on errors and does not offer a version the user skipped.
    /// </summary>
    async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates) return;
        if (!manual && (!_settings.CheckForUpdates
                        || _settings.LastUpdateCheck is { } last && last <= DateTime.Now && DateTime.Now - last < UpdateService.CheckInterval))
            return;
        _checkingUpdates = true;
        try
        {
            UpdateCheck result;
            try
            {
                result = await UpdateService.CheckAsync();
                _settings.LastUpdateCheck = DateTime.Now;
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
                                           or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            {
                if (manual) MessageBox.Show(this, "Could not check for updates.\n\n" + ex.Message, "pPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string current = UpdateService.CurrentVersion.ToString(3);
            if (result.Update is not { } update)
            {
                if (manual)
                {
                    string text = result.NoPublicRelease
                        ? "No published release was found (there is none yet, or the repository is private)."
                        : $"pPdf {current} is the latest version.";
                    MessageBox.Show(this, text, "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            string version = update.Version.ToString(3);
            if (!manual && _settings.SkippedVersion == version) return;
            // never in the middle of an operation: next time instead
            if (!manual && (_busy || !IsActive)) { _settings.LastUpdateCheck = null; return; }
            await OfferUpdateAsync(update, version, current, manual);
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    async Task OfferUpdateAsync(UpdateInfo update, string version, string current, bool manual)
    {
        bool installed = UpdateService.IsInstalled;
        string message = installed
            ? $"pPdf {version} is available (you have {current}).\n\nUpdate now? pPdf closes, installs the update and opens again."
            : $"pPdf {version} is available (you have {current}).\n\nThis copy was not installed with the setup: open the download page?";
        string action = installed ? "Update now" : "Open the page";
        int answer = manual
            ? ChoiceDialog.Ask(this, "Update available", message, action, "Later")
            : ChoiceDialog.Ask(this, "Update available", message, action, "Skip this version", "Later");
        if (answer == 1 && !manual)
        {
            _settings.SkippedVersion = version;
            return;
        }
        if (answer != 0) return;
        if (!installed)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update.PageUrl) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return;
        }

        // the installer cannot replace the program while another pPdf window is open
        if (UpdateService.OtherInstancesRunning())
        {
            MessageBox.Show(this, "pPdf is also open in another window (or for another user signed in to this PC). Close it, then choose \"Check for updates\" again.",
                "pPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // the update closes pPdf: annotations that were never saved first
        bool wasDirty = Viewer.Annotations.IsDirty;
        if (!await ConfirmDiscardAnnotationsAsync()) return;

        string installer;
        SetBusy(true, $"Downloading pPdf {version}...");
        try
        {
            var progress = new Progress<double>(p => BusyText.Text = $"Downloading pPdf {version}... {p:P0}");
            installer = await UpdateService.DownloadAsync(update, progress);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            SetBusy(false);
            MessageBox.Show(this, $"Could not download the update.\n\n{ex.Message}\n\nIt can also be downloaded from {update.PageUrl}", "pPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetBusy(false);

        // annotations added while downloading: ask again
        Viewer.CommitEdits();
        if (!wasDirty && Viewer.Annotations.IsDirty && !await ConfirmDiscardAnnotationsAsync()) return;

        SetBusy(true, $"Installing pPdf {version}...");
        bool started = await CloseForUpdateAsync(installer, _path);
        if (!started)
        {
            SetBusy(false);
            MessageBox.Show(this, $"pPdf {version} was not installed.", "pPdf", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    async Task<bool> CloseForUpdateAsync(string installer, string? reopen)
    {
        // /SILENT shows only the progress; /RELAUNCH and /OPEN make the new version start again on the same
        // file (see [Run] in pPdf.iss). The installer signals the event once it really starts (an all-users
        // install asks for UAC first) and then waits for this process to end.
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, $"pPdf.UpdateReady.{Environment.ProcessId}");
        string args = $"/SILENT /NORESTART /RELAUNCH=1 /NOTIFYPID={Environment.ProcessId}" + (reopen is null ? "" : $" /OPEN=\"{reopen}\"");
        System.Diagnostics.Process? setup;
        try
        {
            setup = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer, args) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show(this, "Could not start the update.\n\n" + ex.Message, "pPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (setup is not null)
        {
            using (setup)
            {
                while (!ready.WaitOne(0) && !setup.HasExited) await Task.Delay(100);
                // setup ended without starting (UAC refused, or cancelled): pPdf stays open
                if (!ready.WaitOne(0)) return false;
            }
        }
        _forceClose = true; // the annotations were already confirmed or discarded
        Close();
        return true;
    }
}
