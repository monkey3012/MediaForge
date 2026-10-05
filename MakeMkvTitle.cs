namespace MediaForge.Models;

public class MakeMkvTitle
{
    public int Index { get; set; }
    public string TitleName { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public long SizeBytes { get; set; }
    public int Chapters { get; set; }
    public int VideoTracks { get; set; }
    public int AudioTracks { get; set; }
    public string DisplayTitle => string.IsNullOrWhiteSpace(TitleName) ? $"Title {Index:00}" : TitleName;
    public string DisplaySize => SizeBytes > 0 ? $"{SizeBytes / 1_000_000_000.0:0.0} GB" : "Unknown";
    public string DisplayDuration => Duration == TimeSpan.Zero ? "Unknown" : Duration.ToString(@"hh\:mm\:ss");
    public int MainScore { get; set; }
    public bool IsLikelyMainTitle => MainScore >= 60;
}
