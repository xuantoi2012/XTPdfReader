using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Workspace;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    private readonly DispatcherTimer _recoveryTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private Task _recoveryWrite = Task.CompletedTask;
    private bool _recoveryReady, _recoveryStopped, _recoveryWarningShown;

    private void InitializeRecovery()
    {
        _recoveryTimer.Tick += async (_, _) =>
        {
            _recoveryTimer.Stop();
            if (!_recoveryReady || _recoveryStopped) return;
            if (!_recoveryWrite.IsCompleted) { ScheduleRecovery(); return; }
            try
            {
                var snapshot = CaptureSession();
                _recoveryWrite = Task.Run(() => SessionRecoveryStore.Save(snapshot));
                await _recoveryWrite;
            }
            catch (Exception ex)
            {
                if (!_recoveryWarningShown && !_recoveryStopped)
                {
                    _recoveryWarningShown = true;
                    XTPdfMergeApp.Services.Growl.Error("Could not save the recovery checkpoint: " + ex.Message, this);
                }
            }
        };
        Session.StatusChanged += ScheduleRecovery;
        AnnotationStore.PendingChanged += RecoveryAnnotationsChanged;
    }

    private void RecoveryAnnotationsChanged(string _) => ScheduleRecovery();
    private void ScheduleRecovery()
    {
        if (!_recoveryReady || _recoveryStopped) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(ScheduleRecovery); return; }
        // Keep the first deadline so continuous editing still gets a checkpoint every second.
        if (!_recoveryTimer.IsEnabled) _recoveryTimer.Start();
    }
    private SavedSession CaptureSession() => SessionRecoveryStore.Capture(_groups, _mergeWindow?.View.CaptureDraft() ?? new(),
        _readerGroup == null ? -1 : _groups.IndexOf(_readerGroup),
        _readerGroup == null || _readerPage == null ? 0 : _readerGroup.Pages.IndexOf(_readerPage), Session.RecoveryStamp);

    internal async Task StartSessionAsync(string[] incoming)
    {
        try
        {
            var saved = await Task.Run(() => SessionRecoveryStore.Load());
            if (saved != null && (saved.Documents.Count > 0 || saved.Draft.Count > 0))
            {
                var answer = AppDialog.Show(this, "The previous session did not close normally. Restore its pages, merge draft and unsaved annotations?\n\nPDF passwords must be entered again.",
                    "Restore unfinished session", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    var result = await Session.RestoreSourcesAsync(saved);
                    if (saved.Draft.Count > 0)
                    {
                        var window = EnsureMergeWindow();
                        var workspace = new PdfWorkspace();
                        window.View.RestoreDraft(saved.Draft.Select(d => (SessionRecoveryStore.RestoreDocument(d, workspace, result.Paths, result.Counts), d.Temporary)));
                        window.Show();
                    }
                    int active = Math.Clamp(saved.ActiveDocument, 0, Math.Max(0, _groups.Count - 1));
                    if (_groups.Count > 0 && _groups[active].Pages.Count > 0)
                    {
                        ShowStart(false);
                        var group = _groups[active];
                        await ShowPageAsync(group, group.Pages[Math.Clamp(saved.ActivePage, 0, group.Pages.Count - 1)], preserveZoomMode: true);
                    }
                    if (result.Skipped.Count > 0)
                    {
                        File.Copy(SessionRecoveryStore.Checkpoint, Path.Combine(SessionRecoveryStore.Folder, "session-unresolved.dat"), true);
                        AppDialog.Show(this, "Some sources changed, are missing, or could not be opened. Their old edits were skipped:\n\n" + string.Join("\n", result.Skipped)
                            + "\n\nThe original checkpoint has been kept as session-unresolved.dat.", "Session recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else if (answer == MessageBoxResult.No) SessionRecoveryStore.Clear();
                else { return; } // Keep the prior checkpoint until an explicit restore/discard decision.
            }
            _recoveryReady = true;
            ScheduleRecovery();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, "Could not restore the session. The checkpoint has been kept.\n\n" + ex.Message,
                "Session recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { if (incoming.Length > 0) await Session.OpenFilesInReaderAsync(incoming); }
    }

    private void CompleteRecoveryClose()
    {
        _recoveryStopped = true;
        _recoveryTimer.Stop();
        AnnotationStore.PendingChanged -= RecoveryAnnotationsChanged;
        try { _recoveryWrite.GetAwaiter().GetResult(); }
        catch { /* A failed checkpoint must not prevent normal closing. */ }
        if (_recoveryReady) { try { SessionRecoveryStore.Clear(); } catch { } }
    }
}
