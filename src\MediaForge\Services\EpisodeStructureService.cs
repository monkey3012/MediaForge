using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>
/// Detects likely multi-episode TV titles without using raw file size as the decision maker.
/// Runtime-to-TMDB-runtime is the primary signal. Chapter count is used to build a reviewable
/// split suggestion; size is deliberately not used to decide the episode count.
/// </summary>
public sealed class EpisodeStructureService
{
    public EpisodeStructureAnalysis Analyze(MediaJob job, IReadOnlyList<TmdbEpisode> episodes)
    {
        var durationSeconds = job.SourceDurationSeconds;
        var runtimeValues = episodes
            .Where(x => x.RuntimeMinutes is > 0)
            .Select(x => (double)x.RuntimeMinutes!.Value)
            .OrderBy(x => x)
            .ToList();

        if (durationSeconds is not > 0 || runtimeValues.Count == 0)
        {
            return new EpisodeStructureAnalysis(
                false, 1, 0, "Single episode / review", "Not enough runtime metadata to safely determine whether this title contains multiple episodes.", Array.Empty<ChapterRangeSuggestion>());
        }

        var sourceMinutes = durationSeconds.Value / 60.0;
        var median = Median(runtimeValues);
        var closestRuntimeValue = runtimeValues.OrderBy(x => Math.Abs(x - sourceMinutes)).First();
        var singleEpisodeDelta = Math.Abs(sourceMinutes - closestRuntimeValue) / Math.Max(closestRuntimeValue, 1.0);
        if (singleEpisodeDelta <= 0.15)
        {
            return new EpisodeStructureAnalysis(
                false,
                1,
                96,
                "Single episode",
                $"Source runtime {FormatMinutes(sourceMinutes)} closely matches a TMDB episode runtime of {FormatMinutes(closestRuntimeValue)}. It is not treated as a multi-episode title even if the file is large.",
                Array.Empty<ChapterRangeSuggestion>());
        }
        if (median <= 0)
            return new EpisodeStructureAnalysis(false, 1, 0, "Single episode / review", "TMDB did not provide usable episode runtimes.", Array.Empty<ChapterRangeSuggestion>());

        var ratio = sourceMinutes / median;
        var estimated = (int)Math.Round(ratio, MidpointRounding.AwayFromZero);

        // Do not auto-split a source that is only modestly longer than one episode.
        // The 1.60 threshold avoids turning long single episodes/specials into two files.
        if (estimated < 2 || ratio < 1.60 || estimated > 8 || Math.Abs(ratio - estimated) > 0.24)
        {
            var singleConfidence = ratio < 1.35 ? 94 : 72;
            return new EpisodeStructureAnalysis(
                false,
                1,
                singleConfidence,
                "Single episode",
                $"Source runtime {FormatMinutes(sourceMinutes)} vs TMDB median episode runtime {FormatMinutes(median)} ({ratio:0.00}×). No reliable multi-episode pattern detected.",
                Array.Empty<ChapterRangeSuggestion>());
        }

        var closeness = Math.Abs(ratio - estimated) / estimated;
        var confidence = 100 - (int)Math.Round(closeness * 100.0);
        confidence += job.SourceKind switch
        {
            MediaSourceKind.Dvd => 4,
            MediaSourceKind.BluRay => 2,
            _ => 0
        };
        if (job.SourceChapterCount.HasValue && job.SourceChapterCount.Value >= 2 * estimated) confidence += 3;
        confidence = Math.Clamp(confidence, 60, 98);

        var ranges = BuildChapterSuggestions(job, estimated, sourceMinutes, out var chapterPatternConfidence);
        if (ranges.Count == estimated)
            confidence = Math.Min(confidence, chapterPatternConfidence);
        var structure = ranges.Count == estimated
            ? $"Likely {estimated} episodes"
            : $"Likely {estimated} episodes — chapter boundaries need review";
        var detail = $"Source runtime {FormatMinutes(sourceMinutes)} is about {ratio:0.00}× the TMDB median episode runtime {FormatMinutes(median)}. {estimated} episode(s) fit the runtime closely.";
        if (ranges.Count == estimated)
            detail += $" Repeated chapter-duration patterns support {estimated} segment boundaries (pattern confidence {chapterPatternConfidence}%).";
        else if (job.SourceChapterCount is > 0)
            detail += $" Source has {job.SourceChapterCount} chapters, but no repeatable chapter-duration pattern supported safe boundaries.";
        detail += " File size is not used to make this decision.";

        return new EpisodeStructureAnalysis(true, estimated, confidence, structure, detail, ranges);
    }

    private static IReadOnlyList<ChapterRangeSuggestion> BuildChapterSuggestions(
        MediaJob job, int episodeCount, double sourceMinutes, out int confidence)
    {
        confidence = 0;
        var durations = job.ChapterDurationsSeconds;
        if (durations.Count == 0 || episodeCount < 2 || durations.Any(x => x <= 0))
            return Array.Empty<ChapterRangeSuggestion>();

        var averageChapters = durations.Count / (double)episodeCount;
        var minSize = Math.Max(2, (int)Math.Floor(averageChapters) - 1);
        var maxSize = (int)Math.Ceiling(averageChapters) + 1;
        List<ChapterRangeSuggestion>? bestRanges = null;
        var bestScore = 0.0;

        for (var chaptersPerEpisode = minSize; chaptersPerEpisode <= maxSize; chaptersPerEpisode++)
        {
            var baseChapterCount = chaptersPerEpisode * episodeCount;
            var trailingCount = durations.Count - baseChapterCount;
            if (trailingCount < 0 || trailingCount > 2) continue;

            var trailingSeconds = durations.Skip(baseChapterCount).Sum();
            var targetSeconds = sourceMinutes * 60.0 / episodeCount;
            if (trailingSeconds > Math.Max(12.0, targetSeconds * 0.12)) continue;

            var ranges = new List<ChapterRangeSuggestion>(episodeCount);
            var patternError = 0.0;
            var comparisons = 0;
            var runtimeError = 0.0;
            for (var episode = 0; episode < episodeCount; episode++)
            {
                var start = episode * chaptersPerEpisode;
                var end = start + chaptersPerEpisode - 1;
                if (episode == episodeCount - 1) end += trailingCount;
                var segmentSeconds = durations.Skip(start).Take(end - start + 1).Sum();
                runtimeError += Math.Abs(segmentSeconds - targetSeconds) / Math.Max(targetSeconds, 1.0);
                ranges.Add(new ChapterRangeSuggestion(episode + 1, start + 1, end + 1));
            }

            // A candidate boundary is credible when the same chapter positions
            // repeat a similar duration signature across several episode blocks.
            for (var position = 0; position < chaptersPerEpisode; position++)
            {
                var values = Enumerable.Range(0, episodeCount)
                    .Select(i => durations[i * chaptersPerEpisode + position])
                    .ToList();
                var typical = Median(values);
                if (typical <= 0) continue;
                patternError += values.Average(value => Math.Abs(value - typical) / typical);
                comparisons++;
            }

            if (comparisons == 0) continue;
            patternError /= comparisons;
            runtimeError /= episodeCount;
            var patternConfidence = Math.Clamp(100.0 * (1.0 - patternError), 0, 100);
            var runtimeConfidence = Math.Clamp(100.0 * (1.0 - runtimeError), 0, 100);
            var score = patternConfidence * 0.75 + runtimeConfidence * 0.25;
            if (score > bestScore)
            {
                bestScore = score;
                bestRanges = ranges;
            }
        }

        // Avoid proposing ranges when chapter durations do not repeat enough to
        // distinguish episode boundaries from ordinary chapter breaks.
        if (bestRanges == null || bestScore < 72) return Array.Empty<ChapterRangeSuggestion>();
        confidence = (int)Math.Round(bestScore);
        return bestRanges;
    }
    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }

    private static string FormatMinutes(double minutes)
    {
        var whole = (int)Math.Round(minutes);
        return whole >= 60 ? $"{whole / 60}h {whole % 60:00}m" : $"{whole}m";
    }
}

public sealed record ChapterRangeSuggestion(int SegmentNumber, int Start, int End)
{
    public string Display => $"Ch. {Start}-{End}";
}

public sealed record EpisodeStructureAnalysis(
    bool IsMultiEpisode,
    int EstimatedEpisodeCount,
    int Confidence,
    string StructureLabel,
    string Detail,
    IReadOnlyList<ChapterRangeSuggestion> ChapterRanges);
