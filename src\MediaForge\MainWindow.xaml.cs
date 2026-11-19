using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge;

public partial class MainWindow : Window
{
    private readonly JobDatabase _db = new();
    private readonly SettingsService _settingsSvc = new();
    private readonly MakeMkvService _makeMkv = new(new ProcessRunner());
    private readonly ProcessConsoleWindow _console = new();
    private readonly Queue<long> _queue = new();
    private readonly HashSet<long> _queuedIds = new();
    private AppSettings _settings;
    private Task? _queueWorker;
    private CancellationTokenSource? _activeJobCts;
    private bool _queuePaused;
    private MediaJob? _activeJob;
    private readonly HashSet<long> _removeAfterStop = new();
    private bool _hammerAnimating;

    public MainWindow()
    {
        InitializeComponent();
        // Load the taskbar/window icon as a PNG resource. Loading the ICO directly
        // through XAML can trigger WPF TypeConverterMarkupExtension/BAML failures.
        Icon = new BitmapImage(new Uri("pack://application:,,,/MediaForge;component/Assets/MediaForgeTaskbar.png", UriKind.Absolute));
        _settings = _settingsSvc.Load();
        ThemeManager.Apply(this, _settings.IsDarkMode);
        ThemeButton.Content = _settings.IsDarkMode ? "Light Mode" : "Dark Mode";
        Refresh();
        MediaForgeLogger.Info("Main window initialized.");
    }

    private void Refresh()
    {
        var selectedIds = JobsGrid.SelectedItems.Cast<MediaJob>().Select(x => x.Id).ToHashSet();
        JobsGrid.ItemsSource = null;
        JobsGrid.ItemsSource = _db.LoadAll();

        foreach (var item in JobsGrid.Items.Cast<object>().OfType<MediaJob>())
        {
            if (selectedIds.Contains(item.Id))
                JobsGrid.SelectedItems.Add(item);
        }

        var queueIndex = 1;
        foreach (var id in _queue)
        {
            var queuedJob = JobsGrid.Items.Cast<object>().OfType<MediaJob>().FirstOrDefault(x => x.Id == id);
            if (queuedJob is not null) queuedJob.QueuePosition = queueIndex++;
        }
        QueueCountText.Text = $" {_queue.Count} pending";
        var pct = Math.Clamp(_activeJob?.Progress ?? 0, 0, 100);
        ForgeProgressBar.Value = pct;
        ForgeProgressText.Text = $"{pct}%";

        if (_activeJob is null)
        {
            ForgeHammerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            ForgeHammerRotate.Angle = 18;
            _hammerAnimating = false;
        }
        else if (!_hammerAnimating)
        {
            _hammerAnimating = true;
            AnimateHammerStrike();
        }
        ApplyQueueColumnSettings();
        PauseQueueButton.Content = _queuePaused ? "Resume Queue" : "Pause Queue";
        CurrentJobText.Text = _activeJob is null ? " None" : $" {_activeJob.SourceName}";
        FooterStatusText.Text = _queueWorker is { IsCompleted: false }
            ? "Queue is running."
            : "Queue is idle.";
    }


    private void AnimateHammerStrike()
    {
        // Keep striking for the lifetime of the active job. Progress callbacks can
        // arrive slowly or in bursts, so animation is independent of percentage changes.
        ForgeHammerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        var strike = new DoubleAnimation(18, -10, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = new System.Windows.Media.Animation.CircleEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
            }
        };
        ForgeHammerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, strike);
    }

    private void ApplyQueueColumnSettings()
    {
        var enabled = new HashSet<string>(_settings.QueueColumns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        ColStatus.Visibility = enabled.Contains("Status") ? Visibility.Visible : Visibility.Collapsed;
        ColFileName.Visibility = enabled.Contains("File Name") ? Visibility.Visible : Visibility.Collapsed;
        ColType.Visibility = enabled.Contains("Type") ? Visibility.Visible : Visibility.Collapsed;
        ColTitle.Visibility = enabled.Contains("Title") ? Visibility.Visible : Visibility.Collapsed;
        ColStage.Visibility = enabled.Contains("Stage") ? Visibility.Visible : Visibility.Collapsed;
        ColProgress.Visibility = enabled.Contains("Progress") ? Visibility.Visible : Visibility.Collapsed;
        ColRippedSize.Visibility = enabled.Contains("Ripped Size") ? Visibility.Visible : Visibility.Collapsed;
        ColFinalSize.Visibility = enabled.Contains("Final Size") ? Visibility.Visible : Visibility.Collapsed;
        ColSaved.Visibility = enabled.Contains("Saved") ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_settings) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _settings = _settingsSvc.Load();
            ThemeManager.Apply(this, _settings.IsDarkMode);
            ThemeButton.Content = _settings.IsDarkMode ? "Light Mode" : "Dark Mode";
            Refresh();
        }
    }

    private void ImportDisc_Click(object sender, RoutedEventArgs e)
    {
        var w = new DiscWindow(_makeMkv, _settings, _db, _console) { Owner = this };
        w.ShowDialog();
        Refresh();
    }

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import existing MKV files",
            Filter = "MKV video files (*.mkv)|*.mkv|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var imported = 0;
        var existing = _db.LoadAll();
        foreach (var file in dialog.FileNames)
        {
            if (existing.Any(x => string.Equals(x.SourcePath, file, StringComparison.OrdinalIgnoreCase)))
                continue;

            var job = new MediaJob
            {
                Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SourceName = Path.GetFileName(file),
                SourcePath = file,
                Status = JobStatus.Queued,
                Stage = "Imported",
                RippedSizeBytes = TryGetFileSize(file)
            };

            while (_db.LoadAll().Any(x => x.Id == job.Id))
                job.Id++;

            _db.Upsert(job);
            existing.Add(job);
            imported++;
        }

        Refresh();
        MessageBox.Show(
            imported == 0 ? "No new MKV files were imported." : $"Imported {imported} MKV file(s).",
            "MediaForge");
    }

    private static long? TryGetFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch { return null; }
    }

    private void SourceName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box && box.DataContext is MediaJob job)
        {
            // SourceName is metadata/display text only. Never change SourcePath
            // when the user edits this field. HandBrake must continue reading
            // the actual MKV recorded in SourcePath.
            job.SourceName = box.Text.Trim();
            _db.Upsert(job);
            FooterStatusText.Text = $"Source name saved: {job.SourceName}";
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.IsDarkMode = !_settings.IsDarkMode;
        _settingsSvc.Save(_settings);
        ThemeManager.Apply(this, _settings.IsDarkMode);
        ThemeButton.Content = _settings.IsDarkMode ? "Light Mode" : "Dark Mode";
    }

    private void Identify_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobsGrid.SelectedItems.Cast<MediaJob>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select one or more queued jobs first.", "MediaForge");
            return;
        }

        var w = new IdentifyWindow(selected, _settings, _db, autoIdentify: true) { Owner = this };
        w.ShowDialog();
        Refresh();
    }

    private void Process_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobsGrid.SelectedItems.Cast<MediaJob>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select one or more jobs to add to the processing queue.", "MediaForge");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.HandBrakeCliPath) ||
            string.IsNullOrWhiteSpace(_settings.HandBrakePresetName))
        {
            MessageBox.Show("Configure HandBrakeCLI and your existing HandBrake preset in Settings first.", "MediaForge");
            return;
        }

        var added = 0;
        foreach (var job in selected)
        {
            if (job.Status == JobStatus.Completed || _queuedIds.Contains(job.Id) || ReferenceEquals(_activeJob, job))
                continue;

            job.Status = JobStatus.Queued;
            job.Stage = "Queued";
            job.ErrorMessage = null;
            _db.Upsert(job);
            _queue.Enqueue(job.Id);
            _queuedIds.Add(job.Id);
            added++;
        }

        FooterStatusText.Text = added == 0
            ? "The selected jobs are already completed or already in the queue."
            : $"Added {added} job(s) to the processing queue.";
        Refresh();
        StartQueueWorker();
    }

    private void StartQueueWorker()
    {
        if (_queueWorker is { IsCompleted: false })
            return;

        _queueWorker = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        var wf = new WorkflowService(_db, new HandBrakeService(new ProcessRunner()), new PlexNamingService());

        try
        {
            while (_queue.Count > 0)
            {
                while (_queuePaused)
                    await Task.Delay(150, CancellationToken.None);

                var id = _queue.Dequeue();
                _queuedIds.Remove(id);
                var job = _db.LoadAll().FirstOrDefault(x => x.Id == id);
                if (job is null)
                    continue;

                _activeJob = job;
                _activeJobCts = new CancellationTokenSource();
                Refresh();

                try
                {
                    if (job.Status == JobStatus.Completed)
                        continue;

                    if (job.Status != JobStatus.Encoded)
                    {
                        await wf.EncodeExistingAsync(
                            job,
                            _settings,
                            changed: _ => Dispatcher.InvokeAsync(Refresh),
                            token: _activeJobCts.Token,
                            liveOutput: line => _console.Append(line),
                            processStarted: (exe, args) => _console.StartProcess(exe, args));
                        _console.SetFinished(0, false);
                    }

                    if (job.Status == JobStatus.Encoded &&
                        !string.IsNullOrWhiteSpace(job.Title) &&
                        job.Year != null)
                    {
                        wf.Organize(job, _settings, _ => Dispatcher.InvokeAsync(Refresh));
                    }
                    else if (job.Status == JobStatus.Encoded)
                    {
                        job.Stage = "Encoded - Identification required";
                        _db.Upsert(job);
                    }
                }
                catch (OperationCanceledException)
                {
                    job.Status = JobStatus.Cancelled;
                    job.Stage = "Cancelled by user";
                    _console.SetFinished(-1, true);
                    job.ErrorMessage = "The current process was stopped by the user.";
                    _db.Upsert(job);
                }
                catch (Exception ex)
                {
                    _console.SetFinished(-1, false);
                    job.Status = JobStatus.Failed;
                    job.Stage = "Processing failed";
                    job.ErrorMessage = ex.Message;
                    _db.Upsert(job);
                    MediaForgeLogger.Error($"Queue job failed: {job.SourceName}", ex);
                }
                finally
                {
                    var completedCts = _activeJobCts;
                    var removeAfterStop = _removeAfterStop.Remove(job.Id);
                    _activeJobCts = null;
                    _activeJob = null;
                    completedCts?.Dispose();
                    if (removeAfterStop) _db.Delete(job.Id);
                    Refresh();
                }
            }
        }
        finally
        {
            _queueWorker = null;
            Refresh();
        }
    }

    private void PauseQueue_Click(object sender, RoutedEventArgs e)
    {
        _queuePaused = !_queuePaused;
        FooterStatusText.Text = _queuePaused
            ? "Queue paused. The current process will finish; no next job will start until Resume Queue is clicked."
            : "Queue resumed.";
        Refresh();
    }

    private void MoveSelectedUp_Click(object sender, RoutedEventArgs e)
    {
        var ids = JobsGrid.SelectedItems.Cast<MediaJob>().Select(x => x.Id).Where(_queuedIds.Contains).ToList();
        if (ids.Count == 0) return;
        var reordered = _queue.ToList();
        foreach (var id in ids) { var i = reordered.IndexOf(id); if (i > 0) (reordered[i - 1], reordered[i]) = (reordered[i], reordered[i - 1]); }
        _queue.Clear(); foreach (var id in reordered) _queue.Enqueue(id);
        Refresh();
    }

    private void MoveSelectedDown_Click(object sender, RoutedEventArgs e)
    {
        var ids = JobsGrid.SelectedItems.Cast<MediaJob>().Select(x => x.Id).Where(_queuedIds.Contains).ToList();
        if (ids.Count == 0) return;
        var reordered = _queue.ToList();
        for (var k = ids.Count - 1; k >= 0; k--) { var i = reordered.IndexOf(ids[k]); if (i >= 0 && i < reordered.Count - 1) (reordered[i + 1], reordered[i]) = (reordered[i], reordered[i + 1]); }
        _queue.Clear(); foreach (var id in reordered) _queue.Enqueue(id);
        Refresh();
    }

    private void RemoveSelectedFromQueue_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobsGrid.SelectedItems.Cast<MediaJob>().ToList();
        if (selected.Count == 0) return;
        foreach (var job in selected)
        {
            if (ReferenceEquals(_activeJob, job))
            {
                _removeAfterStop.Add(job.Id);
                _activeJobCts?.Cancel();
                continue;
            }
            var remaining = _queue.Where(id => id != job.Id).ToList();
            _queue.Clear(); foreach (var id in remaining) _queue.Enqueue(id);
            _queuedIds.Remove(job.Id);
            _db.Delete(job.Id);
        }
        FooterStatusText.Text = "Selected item(s) removed from the processing list. Active jobs are stopped first.";
        Refresh();
    }

    private void RetrySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobsGrid.SelectedItems.Cast<MediaJob>().Where(x => x.Status == JobStatus.Failed || x.Status == JobStatus.Cancelled).ToList();
        foreach (var job in selected) { job.Status = JobStatus.Queued; job.Stage = "Queued for retry"; job.ErrorMessage = null; _db.Upsert(job); if (_queuedIds.Add(job.Id)) _queue.Enqueue(job.Id); }
        Refresh();
        if (selected.Count > 0) StartQueueWorker();
    }

    private void StopCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_activeJobCts is null) return;
        FooterStatusText.Text = "Stopping the current MakeMKV/HandBrake process...";
        _activeJobCts.Cancel();
    }

    private void Console_Click(object sender, RoutedEventArgs e)
    {
        if (!_console.IsVisible)
            _console.Show();
        else
            _console.Activate();
    }

    private void Organize_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobsGrid.SelectedItems.Cast<MediaJob>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select one or more encoded jobs first.", "MediaForge");
            return;
        }

        var wf = new WorkflowService(_db, new HandBrakeService(new ProcessRunner()), new PlexNamingService());
        var failed = 0;
        foreach (var j in selected)
        {
            try
            {
                if (j.Status != JobStatus.Encoded)
                {
                    MessageBox.Show($"{j.SourceName} is not encoded yet. Encode it before organizing.", "MediaForge");
                    continue;
                }
                wf.Organize(j, _settings);
            }
            catch (Exception ex)
            {
                failed++;
                j.Status = JobStatus.Failed;
                j.Stage = "Organization failed";
                j.ErrorMessage = ex.Message;
                _db.Upsert(j);
            }
        }

        Refresh();
        if (failed > 0)
            MessageBox.Show($"{failed} job(s) failed during organization. See the queue for details.", "MediaForge");
    }

    protected override void OnClosed(EventArgs e)
    {
        _activeJobCts?.Cancel();
        if (_console.IsVisible)
            _console.Close();
        base.OnClosed(e);
    }
}
