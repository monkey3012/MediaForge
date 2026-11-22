using System.IO;
namespace MediaForge.Models;
public enum JobStatus { Queued, Ripping, Ripped, Encoding, Encoded, Identify, Organizing, Completed, Failed, Cancelled }
public sealed class MediaJob
{
    public long Id { get; set; }
    public string SourceName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string FileName => string.IsNullOrWhiteSpace(SourcePath) ? "" : Path.GetFileName(SourcePath);
    public int? ChapterStart { get; set; }
    public int? ChapterEnd { get; set; }
    public long? SourceDurationSeconds { get; set; }
    public int? SourceChapterCount { get; set; }
    public List<double> ChapterDurationsSeconds { get; set; } = new();
    public MediaSourceKind SourceKind { get; set; } = MediaSourceKind.Unknown;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string Stage { get; set; } = "Queued";
    public int Progress { get; set; }
    public string? MediaType { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? EpisodeTitle { get; set; }
    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }
    public int QueuePosition { get; set; }
    public int? TmdbId { get; set; }
    public string? TmdbType { get; set; }

    // Actual bytes on disk at the major workflow boundaries.
    // RippedSizeBytes is the MKV size after MakeMKV/import.
    // EncodedSizeBytes is the MP4 size after HandBrake finishes.
    public long? RippedSizeBytes { get; set; }
    public long? EncodedSizeBytes { get; set; }

    public string RippedSizeDisplay => FormatSize(RippedSizeBytes);
    public string EncodedSizeDisplay => FormatSize(EncodedSizeBytes);
    public string SizeSavingsDisplay
    {
        get
        {
            if (RippedSizeBytes is not > 0 || EncodedSizeBytes is not > 0) return "—";
            var saved = RippedSizeBytes.Value - EncodedSizeBytes.Value;
            var percent = saved / (double)RippedSizeBytes.Value * 100.0;
            return saved >= 0
                ? $"{FormatSize(saved)} ({percent:0.0}%)"
                : $"+{FormatSize(-saved)}";
        }
    }

    public static string FormatSize(long? bytes)
    {
        if (bytes is null || bytes <= 0) return "—";
        const double mb = 1_000_000d;
        const double gb = 1_000_000_000d;
        return bytes.Value >= gb
            ? $"{bytes.Value / gb:0.00} GB"
            : $"{bytes.Value / mb:0.00} MB";
    }
}
