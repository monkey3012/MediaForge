using System.IO;
using System.Text.Json;
namespace MediaForge.Services;

public sealed class AppSettings
{
    public string MakeMkvPath { get; set; } = "";
    public string HandBrakeCliPath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string TesseractPath { get; set; } = "";
    public string HandBrakePresetName { get; set; } = "";
    public string TmdbAccessToken { get; set; } = "";
    public string TempPath { get; set; } = Path.Combine(Path.GetTempPath(), "MediaForge");
    public string MkvOutputPath { get; set; } = "";
    public string MoviesPath { get; set; } = "";
    public string TvShowsPath { get; set; } = "";
    public bool DeleteTempAfterSuccess { get; set; } = true;
    public bool IsDarkMode { get; set; } = true;
    public List<string> QueueColumns { get; set; } = new() { "Status", "File Name", "Type", "Title", "Stage", "Progress", "Ripped Size", "Final Size", "Saved" };
}
public sealed class SettingsService
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaForge", "settings.json");
    public AppSettings Load() => !File.Exists(_path) ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
    public void Save(AppSettings settings) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented=true })); }
}
