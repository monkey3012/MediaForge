using System.Text.RegularExpressions;
using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>
/// Ranks episodes using evidence that identifies an episode. Runtime and chapter
/// count are deliberately not identity signals: many episodes share those values.
/// </summary>
public sealed class EpisodeIdentificationService
{
    private static readonly Regex SeasonEpisodePattern = new(@"\bS(?:eason)?[ ._-]?(?<season>\d{1,2})[ ._-]*E(?:pisode)?[ ._-]?(?<episode>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodePattern = new(@"\b(?:episode|ep)[ ._-]?(?<episode>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "of", "to", "in", "on", "for", "with", "part", "pt", "episode", "ep", "season", "series", "disc", "disk", "dvd", "hd", "uhd", "set", "collection", "complete", "edition", "volume", "pups", "save", "saves", "stop", "stops", "vs"
    };

    public EpisodeMatchResult Match(MediaJob job, int seasonNumber, IReadOnlyList<TmdbEpisode> episodes, string? showTitle = null, bool useDiscContext = false)
    {
        if (episodes.Count == 0)
            return EpisodeMatchResult.NoMatch("No episode catalog is available.");


        var id = job.Season == seasonNumber && job.Episode is > 0 ? job.Episode : FindExplicitEpisode(job, seasonNumber);
        if (id.HasValue)
        {
            var exact = episodes.FirstOrDefault(x => x.SeasonNumber == seasonNumber && x.EpisodeNumber == id.Value);
            if (exact != null)
                return new EpisodeMatchResult(exact, 100, 100, "Exact season/episode identifier found in the source name.",
                    episodes.Select(x => new EpisodeCandidateScore(x, x.EpisodeNumber == exact.EpisodeNumber ? 100 : 0)).ToList());
        }

        var fileTokens = Tokenize(GetSourceFileText(job));
        var sourceTokens = Tokenize(useDiscContext ? GetSourceText(job) : GetSourceFileText(job));
        if (!string.IsNullOrWhiteSpace(showTitle)) sourceTokens.ExceptWith(Tokenize(showTitle));
        if (!string.IsNullOrWhiteSpace(showTitle)) fileTokens.ExceptWith(Tokenize(showTitle));
        if (sourceTokens.Count == 0)
            return EpisodeMatchResult.NoMatch("No episode number or descriptive episode title was found in the source filename.");

        var documentFrequency = BuildDocumentFrequency(episodes);
        var ranked = episodes
            .Select(ep => new EpisodeCandidateScore(ep, ScoreTitle(sourceTokens, Tokenize(ep.Name), documentFrequency)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Episode.EpisodeNumber)
            .ToList();

        var best = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Score : 0;
        var margin = best.Score - second;
        var bestTitleTokens = Tokenize(best.Episode.Name);
        var matchedFileTokens = fileTokens.Intersect(bestTitleTokens, StringComparer.OrdinalIgnoreCase).ToList();
        var fileTitleEvidence = ScoreTitle(fileTokens, bestTitleTokens, documentFrequency);
        var hasDistinctiveTitleEvidence = matchedFileTokens.Count >= 2 || matchedFileTokens.Any(token => documentFrequency.GetValueOrDefault(token) <= 2);
        // Disc labels can narrow candidates, but cannot identify an episode by themselves.
        var confident = !useDiscContext && best.Score >= 78 && margin >= 14 && fileTitleEvidence >= 75 && hasDistinctiveTitleEvidence;
        var detail = best.Score < 45
            ? "No episode title evidence matched the source."
            : $"Episode title similarity scored {best.Score}% with a {margin}% lead; source-file title coverage was {fileTitleEvidence}%.";
        return new EpisodeMatchResult(confident ? best.Episode : null, best.Score, margin, detail, ranked);
    }

    private static int? FindExplicitEpisode(MediaJob job, int seasonNumber)
    {
        var text = $"{job.SourceName} {job.SourcePath}";
        var match = SeasonEpisodePattern.Match(text);
        if (match.Success && int.TryParse(match.Groups["season"].Value, out var parsedSeason) &&
            int.TryParse(match.Groups["episode"].Value, out var parsedEpisode) && parsedSeason == seasonNumber)
            return parsedEpisode;

        // A bare episode marker is only interpreted inside the season the user selected.
        match = EpisodePattern.Match(text);
        if (match.Success && int.TryParse(match.Groups["episode"].Value, out var episode))
            return episode;
        return null;
    }

    private static string GetSourceText(MediaJob job)
    {
        var fileName = GetSourceFileText(job);
        // DVD/BD output filenames are often opaque MakeMKV title codes; the parent folder holds the disc label.
        var discLabel = GetDiscLabel(job);
        return $"{fileName} {discLabel}";
    }

    private static string GetSourceFileText(MediaJob job)
    {
        var pathFileName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var sourceFileName = Path.GetFileNameWithoutExtension(job.SourceName);
        // Do not reuse the stored EpisodeTitle: it may be the result of a previous
        // incorrect identification and would allow bad metadata to reinforce itself.
        return $"{pathFileName} {sourceFileName}";
    }

    public static string GetDiscLabel(MediaJob job)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(job.SourcePath) ?? string.Empty);
        var candidates = new[] { folder, Path.GetFileNameWithoutExtension(job.SourceName), job.Title ?? string.Empty };
        return candidates.FirstOrDefault(value => Regex.IsMatch(value, @"\b(?:disc|disk|dvd|set|volume|vol)[ ._-]?\d+\b", RegexOptions.IgnoreCase))
               ?? candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
               ?? string.Empty;
    }

    public static int? GetDiscNumber(MediaJob job)
    {
        foreach (var text in new[] { job.SourceName, job.SourcePath, job.Title ?? string.Empty })
        {
            var match = Regex.Match(text, @"\b(?:disc|disk|dvd|set|volume|vol)[ ._-]?(?<number>\d{1,2})\b", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups["number"].Value, out var number)) return number;
        }
        return null;
    }

    public static int? GetTitleOrder(MediaJob job)
    {
        var source = Path.GetFileNameWithoutExtension(job.SourcePath);
        var match = Regex.Match(source, @"(?:^|[_ .-])t(?<number>\d{1,3})(?:$|[_ .-])", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number) ? number : null;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var normalized = Regex.Replace(value, @"([a-z])([A-Z])", "$1 $2");
        normalized = Regex.Replace(normalized, @"\b(?:S\d{1,2}E\d{1,3}|\d{3,4}p|x26[45]|h26[45])\b", " ", RegexOptions.IgnoreCase);
        return Regex.Matches(normalized.ToLowerInvariant(), @"[a-z0-9]{2,}")
            .Select(x => x.Value)
            .Where(x => !NoiseWords.Contains(x) && !Regex.IsMatch(x, @"^(?:(?:a\d+)?t\d+|a\d+)$", RegexOptions.IgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> BuildDocumentFrequency(IReadOnlyList<TmdbEpisode> episodes)
    {
        var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var episode in episodes)
            foreach (var token in Tokenize(episode.Name))
                frequency[token] = frequency.GetValueOrDefault(token) + 1;
        return frequency;
    }

    private static int ScoreTitle(HashSet<string> source, HashSet<string> title, Dictionary<string, int> documentFrequency)
    {
        if (source.Count == 0 || title.Count == 0) return 0;
        double Weight(string token) => 1.0 + Math.Log((documentFrequency.Count + 1.0) / (documentFrequency.GetValueOrDefault(token) + 1.0));
        var intersection = source.Intersect(title, StringComparer.OrdinalIgnoreCase).Sum(Weight);
        var sourceWeight = source.Sum(Weight);
        if (intersection <= 0) return 0;
        // Ignore unrelated tokens from disc labels, show names, or quality tags when measuring title coverage.
        return (int)Math.Round(100.0 * intersection / sourceWeight);
    }
}

public sealed record EpisodeCandidateScore(TmdbEpisode Episode, int Score);

public sealed record EpisodeMatchResult(
    TmdbEpisode? SuggestedEpisode,
    int Confidence,
    int Margin,
    string Detail,
    IReadOnlyList<EpisodeCandidateScore> Candidates)
{
    public static EpisodeMatchResult NoMatch(string detail) => new(null, 0, 0, detail, Array.Empty<EpisodeCandidateScore>());
}









