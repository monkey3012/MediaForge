using System.Text.RegularExpressions;
using MediaForge.Models;
namespace MediaForge.Services;

/// <summary>Matches a disc label and MakeMKV title order to a TMDB DVD episode group.</summary>
public sealed class DvdEpisodeGroupMatcher
{
    public TmdbEpisodeGroupSetDto? FindDiscGroup(string discLabel, string showTitle, IEnumerable<TmdbEpisodeGroupDetailsDto> catalogs)
    {
        var disc = Normalize(RemoveShowTitle(discLabel, showTitle));
        if (disc.Length == 0) return null;
        var ranked = catalogs.Where(catalog => catalog.Type == 3)
            .SelectMany(catalog => catalog.Groups.Select(group => (Catalog: catalog, Group: group)))
            .Select(x => (x.Catalog, x.Group, Score: LabelScore(disc, Normalize(RemoveShowTitle(x.Group.Name, showTitle)))))
            .Where(x => x.Score >= 85).OrderByDescending(x => x.Score).ToList();
        if (ranked.Count == 0 || (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < 10)) return null;
        return ranked[0].Group;
    }

    public int? GetUniqueSeason(TmdbEpisodeGroupSetDto? group)
    {
        if (group == null) return null;
        var seasons = group.Episodes.Select(x => x.SeasonNumber).Where(x => x > 0).Distinct().ToList();
        return seasons.Count == 1 ? seasons[0] : null;
    }
    public IReadOnlyDictionary<long, IReadOnlyList<TmdbEpisode>> MatchTitles(IReadOnlyList<MediaJob> jobs, TmdbEpisodeGroupSetDto group)
    {
        var orderedJobs = jobs.Select((job, index) => (Job: job, Index: index, TitleOrder: EpisodeIdentificationService.GetTitleOrder(job)))
            .OrderBy(x => x.TitleOrder ?? int.MaxValue).ThenBy(x => x.Index).ToList();
        var orderedEpisodes = group.Episodes.OrderBy(x => x.Order).ToList();
        if (orderedJobs.Count == 0 || orderedEpisodes.Count == 0 || orderedJobs.Any(x => x.Job.SourceDurationSeconds is not > 0))
            return new Dictionary<long, IReadOnlyList<TmdbEpisode>>();

        // Solve the whole disc as a sequence. Greedy per-title choices can consume an
        // episode that makes every later title wrong; dynamic programming considers all
        // contiguous partitions before choosing a mapping.
        var memo = new Dictionary<(int Job, int Episode), List<Path>>();
        List<Path> Solve(int jobIndex, int episodeIndex)
        {
            if (jobIndex == orderedJobs.Count)
                return episodeIndex == orderedEpisodes.Count ? new List<Path> { new(0, Array.Empty<int[]>()) } : new();
            if (memo.TryGetValue((jobIndex, episodeIndex), out var cached)) return cached;
            var job = orderedJobs[jobIndex].Job;
            var sourceMinutes = job.SourceDurationSeconds!.Value / 60.0;
            var paths = new List<Path>();
            for (var count = 1; count <= 8 && episodeIndex + count <= orderedEpisodes.Count; count++)
            {
                var group = orderedEpisodes.Skip(episodeIndex).Take(count).ToList();
                if (group.Any(ep => ep.RuntimeMinutes is not > 0)) break;
                var expected = group.Sum(ep => ep.RuntimeMinutes!.Value);
                var error = Math.Abs(expected - sourceMinutes) / Math.Max(expected, 1.0);
                // DVDs often include padding, credits, or edits. Keep plausible options,
                // but reject runtime fits too far from the official combined runtime.
                if (error > 0.28) continue;
                foreach (var tail in Solve(jobIndex + 1, episodeIndex + count))
                    paths.Add(new Path(tail.Error + error, new[] { new[] { count } }.Concat(tail.Counts).ToArray()));
            }
            var best = paths.OrderBy(path => path.Error).Take(2).ToList();
            memo[(jobIndex, episodeIndex)] = best;
            return best;
        }

        var solutions = Solve(0, 0);
        if (solutions.Count == 0 || (solutions.Count > 1 && solutions[1].Error - solutions[0].Error < 0.015))
            return new Dictionary<long, IReadOnlyList<TmdbEpisode>>();

        var assignments = new Dictionary<long, IReadOnlyList<TmdbEpisode>>();
        var position = 0;
        for (var i = 0; i < orderedJobs.Count; i++)
        {
            var count = solutions[0].Counts[i][0];
            assignments[orderedJobs[i].Job.Id] = orderedEpisodes.Skip(position).Take(count).Select(ToEpisode).ToList();
            position += count;
        }
        return assignments;
    }

    private static TmdbEpisode ToEpisode(TmdbEpisodeGroupEpisodeDto episode) => new()
    {
        Id = episode.Id, Name = episode.Name, SeasonNumber = episode.SeasonNumber,
        EpisodeNumber = episode.EpisodeNumber, RuntimeMinutes = episode.RuntimeMinutes, AirDate = episode.AirDate
    };

    private sealed record Path(double Error, int[][] Counts);

    private static int LabelScore(string disc, string group)
    {
        if (group.Length == 0) return 0;
        if (disc.Equals(group, StringComparison.OrdinalIgnoreCase)) return 100;
        if (disc.Contains(group, StringComparison.OrdinalIgnoreCase) || group.Contains(disc, StringComparison.OrdinalIgnoreCase)) return 92;
        var wanted = disc.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = group.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.Count == 0 || actual.Count == 0 ? 0 : (int)Math.Round(100.0 * wanted.Intersect(actual).Count() / Math.Max(wanted.Count, actual.Count));
    }

    private static string RemoveShowTitle(string label, string showTitle)
    {
        var normalized = Normalize(label);
        var show = Normalize(showTitle);
        return show.Length == 0 ? normalized : Regex.Replace(normalized, $@"\b{Regex.Escape(show)}\b", " ", RegexOptions.IgnoreCase).Trim();
    }
    private static string Normalize(string value) => string.Join(' ', Regex.Replace(Regex.Replace(value.ToLowerInvariant(), @"\b(?:hd|uhd|disc|disk|dvd|blu ray|bluray)\b", " "), @"[^a-z0-9]+", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

