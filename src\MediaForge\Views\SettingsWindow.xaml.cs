using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly global::MediaForge.Services.SettingsService _svc = new();

    public SettingsWindow(AppSettings s)
    {
        InitializeComponent();
        ThemeManager.Apply(this, s.IsDarkMode);
        _s = s ?? throw new ArgumentNullException(nameof(s));
        MakeMkvBox.Text = s.MakeMkvPath;
        HandBrakeBox.Text = s.HandBrakeCliPath;
        FfmpegBox.Text = s.FfmpegPath;
        TesseractBox.Text = s.TesseractPath;
        PresetBox.Text = s.HandBrakePresetName;
        TmdbBox.Password = s.TmdbAccessToken;
        MkvBox.Text = s.MkvOutputPath;
        TempBox.Text = s.TempPath;
        MoviesBox.Text = s.MoviesPath;
        TvBox.Text = s.TvShowsPath;
        var enabledColumns = new HashSet<string>(s.QueueColumns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        ThemeBox.SelectedIndex = s.IsDarkMode ? 0 : 1;
        foreach (var cb in QueueColumnsPanel.Children.OfType<CheckBox>()) cb.IsChecked = cb.Tag is string tag && enabledColumns.Contains(tag);
        Loaded += SettingsWindow_Loaded;
    }

    private async void SettingsWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= SettingsWindow_Loaded;
        if (System.IO.File.Exists(HandBrakeBox.Text.Trim()))
            await LoadPresetsAsync();
    }

    private void BrowseMkv_Click(object sender, RoutedEventArgs e) => BrowseFolder(MkvBox, "Select MKV rip folder");
    private void BrowseTemp_Click(object sender, RoutedEventArgs e) => BrowseFolder(TempBox, "Select temporary encoding folder");
    private void BrowseMovies_Click(object sender, RoutedEventArgs e) => BrowseFolder(MoviesBox, "Select final Movies folder");
    private void BrowseTv_Click(object sender, RoutedEventArgs e) => BrowseFolder(TvBox, "Select final TV Shows folder");

    private void BrowseMakeMkv_Click(object sender, RoutedEventArgs e) => BrowseExecutable(MakeMkvBox, "MakeMKV CLI|makemkvcon*.exe|Executable files|*.exe");
    private void BrowseHandBrake_Click(object sender, RoutedEventArgs e) => BrowseExecutable(HandBrakeBox, "HandBrake CLI|HandBrakeCLI.exe|Executable files|*.exe");
    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e) => BrowseExecutable(FfmpegBox, "FFmpeg|ffmpeg.exe|Executable files|*.exe");
    private void BrowseTesseract_Click(object sender, RoutedEventArgs e) => BrowseExecutable(TesseractBox, "Tesseract OCR|tesseract.exe|Executable files|*.exe");

    private static void BrowseFolder(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (dialog.ShowDialog() == true) target.Text = dialog.FolderName;
    }

    private static void BrowseExecutable(System.Windows.Controls.TextBox target, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select command-line executable",
            Filter = filter + "|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
    }

    private void DetectApplications_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Searching for MakeMKV and HandBrake CLI...";
        try
        {
            var makeMkv = global::MediaForge.Services.CliLocator.FindMakeMkv();
            var handBrake = global::MediaForge.Services.CliLocator.FindHandBrake();
            if (!string.IsNullOrWhiteSpace(makeMkv)) MakeMkvBox.Text = makeMkv;
            if (!string.IsNullOrWhiteSpace(handBrake)) HandBrakeBox.Text = handBrake;
            var found = new List<string>();
            if (!string.IsNullOrWhiteSpace(makeMkv)) found.Add("MakeMKV CLI");
            if (!string.IsNullOrWhiteSpace(handBrake)) found.Add("HandBrakeCLI");
            StatusText.Text = found.Count == 0
                ? "No CLI executables were found. HandBrake GUI and HandBrakeCLI are separate Windows downloads; use Browse or Download HandBrake CLI."
                : $"Detected: {string.Join(", ", found)}.";
        }
        catch (Exception ex) { StatusText.Text = $"CLI detection failed: {ex.Message}"; }
    }

    private void DetectMakeMkv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = global::MediaForge.Services.CliLocator.FindMakeMkv();
            if (!string.IsNullOrWhiteSpace(path)) MakeMkvBox.Text = path;
            StatusText.Text = string.IsNullOrWhiteSpace(path) ? "MakeMKV CLI was not found automatically." : $"Found MakeMKV CLI: {path}";
        }
        catch (Exception ex) { StatusText.Text = $"MakeMKV detection failed: {ex.Message}"; }
    }

    private void DetectFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var path = CliLocator.FindFfmpeg();
        if (!string.IsNullOrWhiteSpace(path)) FfmpegBox.Text = path;
        StatusText.Text = string.IsNullOrWhiteSpace(path) ? "FFmpeg was not found in PATH or common folders." : $"Found FFmpeg: {path}";
    }

    private void DetectTesseract_Click(object sender, RoutedEventArgs e)
    {
        var path = CliLocator.FindTesseract();
        if (!string.IsNullOrWhiteSpace(path)) TesseractBox.Text = path;
        StatusText.Text = string.IsNullOrWhiteSpace(path) ? "Tesseract was not found in PATH or common folders." : $"Found Tesseract: {path}";
    }
    private void DetectHandBrake_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = global::MediaForge.Services.CliLocator.FindHandBrake();
            if (!string.IsNullOrWhiteSpace(path)) HandBrakeBox.Text = path;
            StatusText.Text = string.IsNullOrWhiteSpace(path)
                ? "HandBrakeCLI was not found. Install/download the separate Windows CLI package, then Browse to HandBrakeCLI.exe."
                : $"Found HandBrakeCLI: {path}";
        }
        catch (Exception ex) { StatusText.Text = $"HandBrake detection failed: {ex.Message}"; }
    }

    private void DownloadHandBrakeCli_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://handbrake.fr/downloads2.php",
                UseShellExecute = true
            });
            StatusText.Text = "Opened the official HandBrake downloads page. Under Downloads (Command Line), choose Windows x64.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not open the HandBrake download page: {ex.Message}"; }
    }

    private async void RefreshPresets_Click(object sender, RoutedEventArgs e)
    {
        await LoadPresetsAsync();
    }

    private async Task LoadPresetsAsync()
    {
        var cli = HandBrakeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cli) || !System.IO.File.Exists(cli))
        {
            StatusText.Text = "Select a valid HandBrakeCLI.exe first.";
            return;
        }

        try
        {
            StatusText.Text = "Loading HandBrake presets...";
            var hb = new HandBrakeService(new ProcessRunner());
            var presets = await hb.ListPresetsAsync(cli);
            var current = PresetBox.Text.Trim();
            PresetBox.ItemsSource = presets;
            if (!string.IsNullOrWhiteSpace(current) && presets.Any(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase)))
                PresetBox.SelectedItem = presets.First(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
            else if (!string.IsNullOrWhiteSpace(current))
                PresetBox.Text = current;
            StatusText.Text = $"Loaded {presets.Count} HandBrake preset(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load HandBrake presets: {ex.Message}";
        }
    }

    private void DownloadMakeMkv_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.makemkv.com/download/") { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Unable to open the MakeMKV download page: " + ex.Message; }
    }


    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ThemeBox.SelectedIndex < 0) return;
        ThemeManager.Apply(this, ThemeBox.SelectedIndex == 0);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.MakeMkvPath = MakeMkvBox.Text.Trim();
        _s.HandBrakeCliPath = HandBrakeBox.Text.Trim();
        _s.FfmpegPath = FfmpegBox.Text.Trim();
        _s.TesseractPath = TesseractBox.Text.Trim();
        _s.HandBrakePresetName = PresetBox.Text.Trim();
        _s.TmdbAccessToken = TmdbBox.Password.Trim();
        _s.MkvOutputPath = MkvBox.Text.Trim();
        _s.TempPath = TempBox.Text.Trim();
        _s.MoviesPath = MoviesBox.Text.Trim();
        _s.TvShowsPath = TvBox.Text.Trim();
        _s.IsDarkMode = ThemeBox.SelectedIndex == 0;
        _s.QueueColumns = QueueColumnsPanel.Children.OfType<CheckBox>().Where(x => x.IsChecked == true && x.Tag is string).Select(x => (string)x.Tag!).ToList();
        _svc.Save(_s);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
