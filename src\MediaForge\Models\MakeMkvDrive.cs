namespace MediaForge.Models;

public sealed class MakeMkvDrive
{
    public int Index { get; set; }
    public string DriveLetter { get; set; } = "";
    public string DriveName { get; set; } = "";
    public string DiscName { get; set; } = "";
    public bool IsVisible { get; set; }
    public bool IsEnabled { get; set; }
    public string DisplayName => string.IsNullOrWhiteSpace(DiscName)
        ? $"{DriveLetter} — {DriveName} (MakeMKV disc:{Index})"
        : $"{DriveLetter} — {DiscName} | {DriveName} (disc:{Index})";
}
