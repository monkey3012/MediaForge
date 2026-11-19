using System.Globalization;
using System.Text.RegularExpressions;
using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>Extracts a frame at each title-card timestamp and reads it locally with FFmpeg and Tesseract.</summary>
public sealed class TitleCardOcrService
{
    private readonly ProcessRunner _runner;
    public TitleCardOcrService(ProcessRunner runner) => _runner = runner;

    /// <summary>Samples 45 seconds into each chapter-derived segment, matching the title-card position used by Paw Patrol DVDs.</summary>
    public static IReadOnlyList<double> PlanSampleTimes(
        IReadOnlyList<double> chapterDurationsSeconds, IReadOnlyList<ChapterRangeSuggestion> ranges)
    {
        var starts = new List<double>();
        foreach (var range in ranges)
        {
            var firstChapter = Math.Clamp(range.Start - 1, 0, chapterDurationsSeconds.Count);
            var segmentStart = chapterDurationsSeconds.Take(firstChapter).Sum();
            var segmentDuration = chapterDurationsSeconds.Skip(firstChapter)
                .Take(Math.Max(0, range.End - range.Start + 1)).Sum();
            var offset = segmentDuration > 0 ? Math.Min(45, Math.Max(0, segmentDuration - 1)) : 45;
            starts.Add(segmentStart + offset);
        }
        return starts;
    }

    public async Task<IReadOnlyList<TitleCardSample>> ReadFramesAsync(
        MediaJob job, IReadOnlyList<double> timestampsSeconds, string ffmpegPath, string tesseractPath,
        CancellationToken token = default)
    {
        if (!File.Exists(job.SourcePath)) throw new FileNotFoundException("Video source was not found.", job.SourcePath);
        if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("FFmpeg was not found.", ffmpegPath);
        if (!File.Exists(tesseractPath)) throw new FileNotFoundException("Tesseract was not found.", tesseractPath);

        var workRoot = Path.Combine(Path.GetTempPath(), "MediaForge", "title-card-ocr");
        Directory.CreateDirectory(workRoot);
        var work = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var output = new List<TitleCardSample>();
            for (var i = 0; i < timestampsSeconds.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var timestamp = Math.Max(0, timestampsSeconds[i]);
                var imagePath = Path.Combine(work, $"frame-{i:000}.png");
                var seconds = timestamp.ToString("0.###", CultureInfo.InvariantCulture);
                var extract = await _runner.RunAsync(ffmpegPath,
                    $"-hide_banner -loglevel error -y -ss {seconds} -i {Quote(job.SourcePath)} -frames:v 1 -vf scale=1280:-2 {Quote(imagePath)}",
                    cancellationToken: token).ConfigureAwait(false);
                if (!extract.Success || !File.Exists(imagePath))
                {
                    output.Add(new TitleCardSample(timestamp, "", "FFmpeg could not extract a frame at this time."));
                    continue;
                }

                var ocr = await _runner.RunAsync(tesseractPath,
                    $"{Quote(imagePath)} stdout -l eng --psm 6", cancellationToken: token).ConfigureAwait(false);
                output.Add(new TitleCardSample(timestamp, ocr.Success ? ocr.StdOut.Trim() : "",
                    ocr.Success ? "" : ocr.StdErr.Trim()));
            }
            return output;
        }
        finally
        {
            // work is a unique child of this dedicated temp root.
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

public sealed record TitleCardSample(double TimestampSeconds, string Text, string Error);

/// <summary>Maps title-card OCR to episode titles, requiring a strong unique match.</summary>
public sealed class EpisodeTitleCardMatcher
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "for", "with", "from", "episode", "part", "pups", "save", "saves", "stop", "stops" };

    public EpisodeTitleCardMatch? Match(string ocrText, IReadOnlyList<TmdbEpisode> episodes)
    {
        var observed = Tokens(ocrText);
        if (observed.Count < 2 || episodes.Count == 0) return null;
        var ranked = episodes.Select(episode =>
        {
            var title = Tokens(episode.Name);
            var overlap = observed.Intersect(title, StringComparer.OrdinalIgnoreCase).Count();
            var precision = overlap / (double)observed.Count;
            var recall = overlap / (double)Math.Max(1, title.Count);
            var score = 100 * (0.55 * precision + 0.45 * recall);
            return (Episode: episode, Score: (int)Math.Round(score), Overlap: overlap);
        }).OrderByDescending(x => x.Score).ToList();
        var best = ranked[0];
        var margin = best.Score - (ranked.Count > 1 ? ranked[1].Score : 0);
        return best.Overlap >= 2 && best.Score >= 72 && margin >= 12
            ? new EpisodeTitleCardMatch(best.Episode, best.Score, margin, ocrText.Trim())
            : null;
    }

    private static HashSet<string> Tokens(string text) => Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]{2,}")
        .Select(match => match.Value).Where(token => !Ignored.Contains(token)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed record EpisodeTitleCardMatch(TmdbEpisode Episode, int Confidence, int Margin, string RecognizedText);
