using System.Collections.ObjectModel;
using System.Text;
using System.IO;
using System.Windows;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge;

public partial class DiscWindow : Window
{
    private readonly MakeMkvService _makeMkv;
    private readonly AppSettings _settings;
    private readonly JobDatabase _db;
    private readonly ProcessConsoleWindow _console;
    private readonly ObservableCollection<SelectableTitle> _titles = new();
    private CancellationTokenSource? _operationCts;
    private bool _busy;
    private readonly StringBuilder _liveBuffer = new();

    public DiscWindow(MakeMkvService makeMkv, AppSettings settings, JobDatabase db, ProcessConsoleWindow console)
    {
        InitializeComponent();
        ThemeManager.Apply(this, settings.IsDarkMode);

        _makeMkv = makeMkv ?? throw new ArgumentNullException(nameof(makeMkv));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _console = console ?? throw new ArgumentNullException(nameof(console));

        TitlesGrid.ItemsSource = _titles;
        Loaded += DiscWindow_Loaded;
        Closed += DiscWindow_Closed;
    }

    private async void DiscWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= DiscWindow_Loaded;
        await LoadDrivesAsync();
    }

    private async Task LoadDrivesAsync()
    {
        if (_busy)
            return;

        if (string.IsNullOrWhiteSpace(_settings.MakeMkvPath))
        {
            StatusText.Text = "Configure MakeMKV in Settings first.";
            return;
        }

        if (!File.Exists(_settings.MakeMkvPath))
        {
            StatusText.Text = "The configured MakeMKV CLI was not found. Check Settings.";
            return;
        }

        SetBusy(true);
        StatusText.Text = "Detecting optical drives through MakeMKV...";

        CancellationTokenSource? cts = null;
        try
        {
            cts = CreateOperationTokenSource();
            var args = "-r info disc:9999";
            StartLiveProcess(_settings.MakeMkvPath, args);
            var drives = await _makeMkv.GetOpticalDrivesAsync(
                _settings.MakeMkvPath,
                cts.Token,
                line => AppendLive(line));
            _console.SetFinished(0, false);

            DriveBox.ItemsSource = drives;

            if (drives.Count > 0)
            {
                DriveBox.SelectedIndex = 0;
                StatusText.Text = $"{drives.Count} optical drive(s) detected. Select a drive and scan.";
            }
            else
            {
                StatusText.Text = "MakeMKV did not report any optical drives. Insert a disc and click Refresh Drives.";
            }
        }
        catch (OperationCanceledException)
        {
            _console.SetFinished(-1, true);
            StatusText.Text = "Drive detection cancelled.";
        }
        catch (Exception ex)
        {
            MediaForgeLogger.Error("Drive detection failed.", ex);
            StatusText.Text = "Drive detection failed: " + FormatException(ex) + $"  Log: {MediaForgeLogger.MainLogPath}";
        }
        finally
        {
            SetBusy(false);
            if (cts is not null)
                DisposeOperationTokenSource(cts);
        }
    }

    private async void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        await LoadDrivesAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (DriveBox.SelectedItem is not MakeMkvDrive drive)
        {
            StatusText.Text = "Select an optical drive.";
            return;
        }

        SetBusy(true);
        _titles.Clear();
        StatusText.Text = $"Reading disc from {drive.DriveLetter} using MakeMKV dev:{drive.DriveLetter}...";
        MediaForgeLogger.Info($"Disc scan requested: drive={drive.DisplayName}");

        CancellationTokenSource? cts = null;
        try
        {
            cts = CreateOperationTokenSource();
            var args = $"-r info dev:{drive.DriveLetter}:";
            StartLiveProcess(_settings.MakeMkvPath, args);
            var titles = await _makeMkv.ScanDiscAsync(
                _settings.MakeMkvPath,
                drive,
                cts.Token,
                line => AppendLive(line));
            _console.SetFinished(0, false);

            foreach (var title in titles)
                _titles.Add(new SelectableTitle(title) { IsSelected = title.IsLikelyMainTitle });

            StatusText.Text = $"{_titles.Count} titles found. Likely main titles are preselected.";
        }
        catch (OperationCanceledException)
        {
            _console.SetFinished(-1, true);
            StatusText.Text = "Disc scan cancelled.";
        }
        catch (Exception ex)
        {
            MediaForgeLogger.Error("Disc scan failed.", ex);
            StatusText.Text = "Disc scan failed: " + FormatException(ex) + $"  Log: {MediaForgeLogger.MainLogPath}";
        }
        finally
        {
            SetBusy(false);
            if (cts is not null)
                DisposeOperationTokenSource(cts);
        }
    }

    private async void Rip_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (DriveBox.SelectedItem is not MakeMkvDrive drive)
        {
            StatusText.Text = "Select an optical drive.";
            return;
        }

        var selected = _titles.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select at least one title.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.MkvOutputPath))
        {
            StatusText.Text = "Configure the MKV output folder in Settings first.";
            return;
        }

        SetBusy(true);

        CancellationTokenSource? cts = null;
        try
        {
            cts = CreateOperationTokenSource();
            var discName = SanitizeDiscFolderName(drive.DiscName);
            if (string.IsNullOrWhiteSpace(discName)) discName = $"disc-{DateTime.Now:yyyyMMdd-HHmmss}";
            var discFolder = GetUniqueDirectory(Path.Combine(_settings.MkvOutputPath, discName));
            Directory.CreateDirectory(discFolder);

            // Keep every title from this disc together in a single folder.
            // MakeMKV gives each title a distinct output filename (for example
            // District 9_t00.mkv), so separate title folders are unnecessary and
            // make the downstream workflow harder to manage.
            var folder = discFolder;

            foreach (var item in selected)
            {
                cts.Token.ThrowIfCancellationRequested();

                StatusText.Text = $"Ripping title {item.Index} from {drive.DriveLetter}...";

                // Snapshot the folder so this title only creates one job for
                // files produced by this MakeMKV operation.
                var filesBefore = new HashSet<string>(
                    Directory.GetFiles(folder, "*.mkv", SearchOption.AllDirectories),
                    StringComparer.OrdinalIgnoreCase);

                var commandArgs = $"-r mkv dev:{drive.DriveLetter}: {item.Index} \"{folder}\"";
                StartLiveProcess(_settings.MakeMkvPath, commandArgs);
                var code = await _makeMkv.RipAsync(
                    _settings.MakeMkvPath,
                    drive,
                    item.Index,
                    folder,
                    log: message => Dispatcher.Invoke(() =>
                        StatusText.Text = message.Length > 250
                            ? message[^250..]
                            : message),
                    token: cts.Token,
                    liveOutput: line => AppendLive(line));

                _console.SetFinished(code, false);
                if (code != 0)
                {
                    throw new InvalidOperationException(
                        $"MakeMKV failed while ripping title {item.Index} (exit code {code}).");
                }

                var files = Directory.GetFiles(
                    folder,
                    "*.mkv",
                    SearchOption.AllDirectories)
                    .Where(file => !filesBefore.Contains(file))
                    .ToList();

                if (files.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"MakeMKV reported success for title {item.Index}, but no new MKV file was created in '{folder}'.");
                }

                foreach (var file in files)
                {
                    var job = new MediaJob
                    {
                        Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SourceName = Path.GetFileName(file),
                        SourcePath = file,
                        Status = JobStatus.Queued,
                        Stage = "Ripped",
                        RippedSizeBytes = TryGetFileSize(file)
                    };

                    // Avoid an extremely unlikely millisecond ID collision.
                    while (_db.LoadAll().Any(x => x.Id == job.Id))
                        job.Id++;

                    _db.Upsert(job);
                    item.Jobs.Add(job);
                }
            }

            StatusText.Text = "Disc import complete. Select a title and use Identify, or close the window to return to the queue.";
        }
        catch (OperationCanceledException)
        {
            _console.SetFinished(-1, true);
            StatusText.Text = "Disc import cancelled.";
        }
        catch (Exception ex)
        {
            MediaForgeLogger.Error("Disc import failed.", ex);
            StatusText.Text = "Disc import failed: " + FormatException(ex) + $"  Log: {MediaForgeLogger.MainLogPath}";
        }
        finally
        {
            SetBusy(false);
            if (cts is not null)
                DisposeOperationTokenSource(cts);
        }
    }

    private void Eject_Click(object sender, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not MakeMkvDrive drive || string.IsNullOrWhiteSpace(drive.DriveLetter)) { StatusText.Text = "Select an optical drive first."; return; }
        try
        {
            var letter = drive.DriveLetter.TrimEnd(':').ToUpperInvariant();
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe") { CreateNoWindow=true, UseShellExecute=false };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add($"(New-Object -ComObject Shell.Application).NameSpace(17).ParseName('{letter}:').InvokeVerb('Eject')");
            process.Start();
            StatusText.Text = $"Eject requested for {letter}:";
        }
        catch (Exception ex) { StatusText.Text = "Eject failed: " + ex.Message; }
    }

    private void IdentifySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = TitlesGrid.SelectedItems.Cast<SelectableTitle>().ToList();
        if (selected.Count == 0)
            selected = _titles.Where(x => x.IsSelected).ToList();

        var jobs = selected
            .SelectMany(x => x.Jobs)
            .GroupBy(x => x.Id)
            .Select(g => g.First())
            .ToList();

        if (jobs.Count == 0)
        {
            StatusText.Text = "Rip the selected title(s) first, then identify them here.";
            return;
        }

        var w = new IdentifyWindow(jobs, _settings, _db, autoIdentify: true) { Owner = this };
        w.ShowDialog();
        TitlesGrid.Items.Refresh();
        StatusText.Text = $"Identification updated for {jobs.Count} job(s).";
    }

    private static string SanitizeDiscFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = new string(Path.GetInvalidFileNameChars());
        var value = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return value;
    }

    private static string GetUniqueDirectory(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        for (var i = 2; i < 10000; i++)
        {
            var candidate = path + $" ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to create a unique disc folder name.");
    }

    private static long? TryGetFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch { return null; }
    }

    private void StartLiveProcess(string executable, string arguments)
    {
        _liveBuffer.Clear();
        DiscConsoleText.Clear();
        LiveCommandText.Text = $"$ {executable} {arguments}";
        _console.StartProcess(executable, arguments);
    }

    private void AppendLive(ProcessOutputLine line)
    {
        _console.Append(line);
        Dispatcher.Invoke(() =>
        {
            var prefix = line.Stream == "ERR" ? "ERR" : line.Stream == "SYS" ? "SYS" : "OUT";
            _liveBuffer.Append('[').Append(line.Timestamp.ToString("HH:mm:ss")).Append("] ")
                .Append(prefix).Append(" | ").AppendLine(line.Text);
            DiscConsoleText.Text = _liveBuffer.ToString();
            DiscConsoleText.ScrollToEnd();
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _operationCts?.Cancel();
            return;
        }

        DialogResult = false;
    }

    private CancellationTokenSource CreateOperationTokenSource()
    {
        if (_operationCts is not null)
        {
            try { _operationCts.Cancel(); } catch (ObjectDisposedException) { }
            _operationCts.Dispose();
        }

        _operationCts = new CancellationTokenSource();
        return _operationCts;
    }

    private void DisposeOperationTokenSource(CancellationTokenSource operationCts)
    {
        if (!ReferenceEquals(_operationCts, operationCts))
            return;

        _operationCts = null;
        operationCts.Dispose();
    }

    private void DiscWindow_Closed(object? sender, EventArgs e)
    {
        var cts = _operationCts;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }

        // Active operations dispose their CTS in their finally blocks. If the
        // window closes while idle, there is no async operation that owns it.
        if (!_busy)
        {
            _operationCts = null;
            cts.Dispose();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DriveBox.IsEnabled = !busy;

        // The XAML does not currently give these buttons names, so their
        // enabled state is controlled by the operation guard above.
    }

    private static string FormatException(Exception ex)
    {
        if (ex is AggregateException aggregate)
            ex = aggregate.GetBaseException();

        return string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : ex.Message.Replace("\r", " ").Replace("\n", " ");
    }

    public sealed class SelectableTitle : MakeMkvTitle
    {
        public SelectableTitle() { }

        public SelectableTitle(MakeMkvTitle source)
        {
            Index = source.Index;
            TitleName = source.TitleName;
            SourceFile = source.SourceFile;
            Duration = source.Duration;
            SizeBytes = source.SizeBytes;
            Chapters = source.Chapters;
            VideoTracks = source.VideoTracks;
            AudioTracks = source.AudioTracks;
            MainScore = source.MainScore;
        }

        public bool IsSelected { get; set; }
        public List<MediaJob> Jobs { get; } = new();
        public string JobStatusDisplay => Jobs.Count == 0
            ? "Not ripped"
            : Jobs.Count == 1
                ? $"{Jobs[0].Status}"
                : $"{Jobs.Count} files";
    }
}
