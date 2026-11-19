using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MediaForge.Models;
using MediaForge.Services;

namespace MediaForge;

public partial class IdentifyWindow : Window
{
    private readonly IReadOnlyList<MediaJob> _jobs;
    private readonly AppSettings _settings;
    private readonly JobDatabase _db;
    private TmdbService? _tmdb;
    private TvMazeCatalog? _tvMazeCatalog;
    private IReadOnlyList<TmdbEpisodeGroupDetailsDto> _tmdbEpisodeGroupCatalogs = Array.Empty<TmdbEpisodeGroupDetailsDto>();
    private int _tmdbEpisodeGroupsForShowId;
    private readonly DvdEpisodeGroupMatcher _dvdEpisodeGroupMatcher = new();
    private int _tvMazeForTmdbShowId;
    private readonly ObservableCollection<TmdbSearchResult> _results = new();
    private readonly ObservableCollection<TvMappingRow> _mappingRows = new();
    private TmdbSearchResult? _selected;
    private TmdbSeason? _season;
    private IReadOnlyList<TmdbSeasonSummary> _availableSeasons = Array.Empty<TmdbSeasonSummary>();
    private bool _isTv;
    private bool _loadingSeasons;
    private bool _loadingEpisodes;
    private bool _settingAutoSelection;
    private readonly EpisodeStructureService _structureService = new();
    private readonly EpisodeIdentificationService _episodeIdentificationService = new();
    private readonly EpisodeTitleCardMatcher _titleCardMatcher = new();
    private readonly Dictionary<long, IReadOnlyList<EpisodeTitleCardMatch?>> _titleCardAssignments = new();

    public IdentifyWindow(IReadOnlyList<MediaJob> jobs, AppSettings settings, JobDatabase db, bool autoIdentify = false)
    {
        InitializeComponent();
        ThemeManager.Apply(this, settings.IsDarkMode);
        _jobs = jobs;
        _settings = settings;
        _db = db;
        ResultsGrid.ItemsSource = _results;
        MappingGrid.ItemsSource = _mappingRows;
        SetMediaType(jobs.FirstOrDefault()?.MediaType == "TV Show", false);
        QueryBox.Text = BuildIdentificationQuery(jobs);
        SourceText.Text = $"{jobs.Count} queued item(s) selected — {string.Join(", ", jobs.Take(3).Select(x => x.SourceName))}";
        if (jobs.Count == 1) { ManualShowBox.Text = jobs[0].Title ?? CleanSourceName(Path.GetFileNameWithoutExtension(jobs[0].FileName)); ManualYearBox.Text = jobs[0].Year?.ToString() ?? ""; ManualSeasonBox.Text = jobs[0].Season?.ToString() ?? "1"; ManualEpisodeBox.Text = jobs[0].Episode?.ToString() ?? "1"; ManualEpisodeTitleBox.Text = jobs[0].EpisodeTitle ?? ""; }
        MappingHint.Text = "Auto Identify analyzes filenames, folders, batch structure and TMDB artwork/metadata; manual override remains available.";
        if (autoIdentify) Loaded += async (_, _) => await AutoIdentifyAsync();
    }

    private string BuildIdentificationQuery(IEnumerable<MediaJob> jobs)
    {
        var tokens = new List<string>();
        foreach (var j in jobs)
        {
            var candidates = new List<string>
            {
                Path.GetFileNameWithoutExtension(j.SourceName),
                Path.GetFileNameWithoutExtension(j.SourcePath)
            };
            // Imported TV files are often nested under Show\\Season N\\Disc N,
            // while the filename itself is only a generic MakeMKV title code.
            // Walk ancestors so the actual show folder remains available as evidence.
            var directory = Path.GetDirectoryName(j.SourcePath);
            for (var depth = 0; !string.IsNullOrWhiteSpace(directory) && depth < 8; depth++)
            {
                var folder = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(folder) && !IsGenericSourceFolder(folder))
                    candidates.Add(folder);
                var parent = Path.GetDirectoryName(directory);
                if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)) break;
                directory = parent;
            }

            foreach (var raw in candidates)
            {
                var cleaned = CleanSourceName(raw);
                if (!string.IsNullOrWhiteSpace(cleaned)) tokens.Add(cleaned);
            }
        }
        if (tokens.Count == 0) return "";

        // Prefer the common/most informative title phrase across a batch.
        return tokens
            .GroupBy(x => Normalize(x), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => Tokens(g.Key).Count)
            .Select(g => g.First())
            .FirstOrDefault() ?? "";
    }

    private static bool IsGenericSourceFolder(string folder) => Regex.IsMatch(folder.Trim(),
        @"^(?:media|video(?:s)?|tv(?: shows?)?|shows?|movies?|rips?|imports?|output|encoded|processed|season\s*\d+|series\s*\d+|disc\s*\d+|disk\s*\d+|dvd\s*\d+)$",
        RegexOptions.IgnoreCase);

    private static string CleanSourceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        // Normalize separators first so DVD/Blu-ray naming variants are treated consistently.
        name = name.Replace('\\', ' ').Replace('/', ' ').Replace('.', ' ').Replace('_', ' ');

        // Remove common media/container/authoring markers. These are evidence about the
        // source file, but should never become part of the title sent to TMDB.
        name = Regex.Replace(name, @"\b(?:title|track|stream|playlist)[ ._-]?\d+\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\b(?:disc|disk|dvd|bd|blu[ .-]?ray|volume|vol)[ ._-]?(?:\d+|[A-Z])\b", " ", RegexOptions.IgnoreCase);

        // TV/DVD season and episode markers. Handle both compact and human-readable forms,
        // including the common "Gilmore Girls Season 7" folder/file naming.
        name = Regex.Replace(name, @"\b(?:season|series)[ ._-]?(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\bS\d{1,2}(?:E\d{1,3}(?:[-.]?E?\d{1,3})*)?\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\b(?:D|Disk|Disc)\d{1,2}\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\b(?:episode|ep)[ ._-]?\d{1,3}\b", " ", RegexOptions.IgnoreCase);

        // MakeMKV/DVD authoring filenames frequently look like A_t01, B_t02,
        // C1_t00, BX_t03, IX_t04, etc. Strip the entire marker wherever it occurs,
        // not just when it appears at the end of the filename.
        name = Regex.Replace(name, @"\b[A-Z]{1,3}\d{0,3}X?[ _.-]+t\d{1,3}\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\b[A-Z]{1,3}X[ _.-]+t\d{1,3}\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"(?:^|\s)[A-Z]{1,3}\d{0,3}[_ .-]?t\d{1,3}(?=\s|$)", " ", RegexOptions.IgnoreCase);

        // Common release/source descriptors.
        name = Regex.Replace(name, @"\b(1080p|2160p|720p|576p|480p|x264|x265|h264|h265|avc|hevc|bluray|blu-ray|web-dl|webrip|remux|proper|repack|extended|special edition|uncut|criterion|hd|uhd|set|collection|complete|edition|boxset)\b", " ", RegexOptions.IgnoreCase);

        // Remove standalone authoring suffixes left after stripping separators.
        name = Regex.Replace(name, @"\b(?:t|title)\d{1,3}\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\b(?:c|a|b|d|e|f|g|h|i|j)\d{0,2}\b", " ", RegexOptions.IgnoreCase);

        name = Regex.Replace(name, @"[-_ .]+", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        name = Regex.Replace(name, @"^[\s\-_:]+|[\s\-_:]+$", "");

        if (Regex.IsMatch(name, @"^(?:disc|disk)?\s*\d{1,3}$", RegexOptions.IgnoreCase)) return "";
        if (Regex.IsMatch(name, @"^(?:title|track|stream)\s*\d{1,3}$", RegexOptions.IgnoreCase)) return "";
        return name;
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(!_isTv);

    private void MovieButton_Click(object sender, RoutedEventArgs e) => SetMediaType(false, true);
    private void TvButton_Click(object sender, RoutedEventArgs e) => SetMediaType(true, true);

    private void SetMediaType(bool tv, bool clearSelection)
    {
        _isTv = tv;
        MovieButton.Background = tv ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 41, 55)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
        MovieButton.Foreground = System.Windows.Media.Brushes.White;
        TvButton.Background = tv ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 41, 55));
        TvButton.Foreground = System.Windows.Media.Brushes.White;
        if (clearSelection)
        {
            _selected = null;
            ResultsGrid.SelectedItem = null;
            SeasonBox.ItemsSource = null;
            _season = null;
            _mappingRows.Clear();
            MappingHint.Text = tv ? "TV Show selected — choose a season after selecting a show." : "Movie selected — TV season/episode mapping is disabled.";
        }
        else if (!tv)
        {
            SeasonBox.ItemsSource = null;
            _season = null;
            _mappingRows.Clear();
        }
    }

    private async Task SearchAsync(bool moviesOnly, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.TmdbAccessToken)) { StatusText.Text = "Add your TMDB token in Settings."; return; }
        if (string.IsNullOrWhiteSpace(QueryBox.Text)) { StatusText.Text = "Enter a title or use Auto Identify."; return; }
        try
        {
            _tmdb ??= new TmdbService(_settings.TmdbAccessToken);
            StatusText.Text = "Searching TMDB...";
            _results.Clear();
            var list = moviesOnly ? await _tmdb.SearchMoviesAsync(QueryBox.Text, token) : await _tmdb.SearchTvAsync(QueryBox.Text, token);
            foreach (var x in list) _results.Add(x);
            StatusText.Text = $"{_results.Count} result(s).";
        }
        catch (Exception ex) { MediaForgeLogger.Error("TMDB search failed.", ex); StatusText.Text = ex.Message; }
    }

    private async void AutoIdentify_Click(object sender, RoutedEventArgs e) => await AutoIdentifyAsync();

    public async Task AutoIdentifyAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.TmdbAccessToken)) { StatusText.Text = "Add your TMDB token in Settings first."; return; }
        try
        {
            _tmdb ??= new TmdbService(_settings.TmdbAccessToken);
            var query = BuildIdentificationQuery(_jobs);
            if (string.IsNullOrWhiteSpace(query))
            {
                StatusText.Text = "MediaForge could not derive a usable title from the source metadata. Rename the File Name to something descriptive and retry.";
                return;
            }

            QueryBox.Text = query;
            StatusText.Text = $"Analyzing '{query}' against TMDB...";

            var candidates = (await _tmdb.SearchBothAsync(query)).ToList();
            if (candidates.Count == 0)
            {
                StatusText.Text = "TMDB returned no movie or TV matches. Try a more descriptive source name.";
                return;
            }

            var ranked = candidates
                .Select(c => new { Candidate = c, Score = ScoreCandidate(c, query) + ScoreBatchShape(c) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => Tokens(x.Candidate.Title).Count)
                .ThenByDescending(x => x.Candidate.Year)
                .ToList();

            _results.Clear();
            foreach (var x in ranked.Take(15)) _results.Add(x.Candidate);
            _selected = ranked[0].Candidate;
            _settingAutoSelection = true;
            ResultsGrid.SelectedItem = _selected;
            _settingAutoSelection = false;
            ResultsGrid.ScrollIntoView(_selected);
            SetMediaType(_selected.MediaType == "tv", false);

            StatusText.Text = $"Auto Identify selected {_selected.DisplayName} ({(_selected.MediaType == "tv" ? "TV" : "Movie")}). Poster artwork and overview are shown for confirmation.";
            if (_selected.MediaType == "tv") await LoadSeasonsAndAutoMapAsync();
        }
        catch (Exception ex)
        {
            MediaForgeLogger.Error("Auto identification failed.", ex);
            StatusText.Text = "Auto identification failed: " + ex.Message;
        }
    }

    private int ScoreBatchShape(TmdbSearchResult c)
    {
        if (_jobs.Count <= 1) return 0;
        // A batch of several similarly named source titles is much more likely to be a TV season.
        return c.MediaType == "tv" ? 18 : -8;
    }

    private static int ScoreCandidate(TmdbSearchResult c, string query)
    {
        var q = Tokens(query);
        var title = Tokens(c.Title);
        var original = Tokens(c.OriginalTitle ?? "");
        var overlap = q.Count(t => title.Contains(t) || original.Contains(t));
        var score = overlap * 30;
        if (string.Equals(Normalize(c.Title), Normalize(query), StringComparison.OrdinalIgnoreCase)) score += 100;
        if (q.Count > 0 && overlap == q.Count) score += 40;
        return score;
    }

    private static HashSet<string> Tokens(string s) => Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 1).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingAutoSelection) return;
        _selected = ResultsGrid.SelectedItem as TmdbSearchResult;
        _mappingRows.Clear();
        SeasonBox.ItemsSource = null;
        _season = null;
        if (_selected == null || _selected.MediaType != "tv") return;
        await LoadSeasonsAndAutoMapAsync();
    }

    private async Task LoadSeasonsAndAutoMapAsync()
    {
        if (_selected == null || _selected.MediaType != "tv") return;
        try
        {
            _tmdb ??= new TmdbService(_settings.TmdbAccessToken);
            StatusText.Text = "Loading seasons...";
            await LoadTvMazeCatalogAsync();
            await LoadTmdbEpisodeGroupsAsync();
            var seasons = (await _tmdb.GetSeasonsAsync(_selected.Id)).Where(x => x.SeasonNumber >= 0).ToList();
            _availableSeasons = seasons;
            TmdbSeasonSummary? target = null;
            _loadingSeasons = true;
            try
            {
                SeasonBox.ItemsSource = seasons;
                var dvdGroupSeason = _dvdEpisodeGroupMatcher.GetUniqueSeason(FindDvdGroupForDisc());
                var inferredSeason = InferSeasonNumber(_jobs);
                // A matched release-specific DVD catalog is stronger evidence than a
                // previously saved season assignment, which may itself be the error.
                target = dvdGroupSeason.HasValue
                    ? seasons.FirstOrDefault(x => x.SeasonNumber == dvdGroupSeason.Value)
                    : inferredSeason.HasValue
                        ? seasons.FirstOrDefault(x => x.SeasonNumber == inferredSeason.Value)
                        : await InferSeasonFromDiscContextAsync(seasons);
                if (dvdGroupSeason.HasValue && inferredSeason.HasValue && dvdGroupSeason.Value != inferredSeason.Value)
                    StatusText.Text = $"Disc release metadata corrected the saved season {inferredSeason.Value:00} to Season {dvdGroupSeason.Value:00}.";
                if (target != null) SeasonBox.SelectedItem = target;
                else StatusText.Text = seasons.Count == 0 ? "The show was found, but TMDB returned no seasons." : "Could not reliably infer the season from source metadata. Choose a season from the list.";
            }
            finally
            {
                _loadingSeasons = false;
            }
            // Initial selection is suppressed while binding; load episodes after releasing the guard.
            if (target != null) await LoadEpisodesAsync(target.SeasonNumber);
        }
        catch (Exception ex) { StatusText.Text = "Season lookup failed: " + ex.Message; }
    }

    private async Task LoadTvMazeCatalogAsync()
    {
        if (_selected == null || _selected.MediaType != "tv" || _tvMazeForTmdbShowId == _selected.Id) return;
        _tvMazeForTmdbShowId = _selected.Id;
        _tvMazeCatalog = null;
        try
        {
            _tmdb ??= new TmdbService(_settings.TmdbAccessToken);
            string? imdbId = null;
            try { imdbId = (await _tmdb.GetExternalIdsAsync(_selected.Id)).ImdbId; }
            catch (Exception ex) { MediaForgeLogger.Warn($"TMDB external IDs unavailable for '{_selected.Title}': {ex.Message}"); }
            var service = new TvMazeService();
            _tvMazeCatalog = await service.FindCatalogAsync(_selected.Title, _selected.Year, imdbId);
        }
        catch (Exception ex)
        {
            MediaForgeLogger.Warn($"TVmaze cross-reference unavailable for '{_selected.Title}': {ex.Message}");
        }
    }

    private async Task LoadTmdbEpisodeGroupsAsync()
    {
        if (_selected == null || _selected.MediaType != "tv" || _tmdbEpisodeGroupsForShowId == _selected.Id) return;
        _tmdbEpisodeGroupsForShowId = _selected.Id;
        _tmdbEpisodeGroupCatalogs = Array.Empty<TmdbEpisodeGroupDetailsDto>();
        try
        {
            _tmdb ??= new TmdbService(_settings.TmdbAccessToken);
            var summaries = await _tmdb.GetEpisodeGroupsAsync(_selected.Id);
            var dvdSummaries = summaries.Where(x => x.Type == 3).Take(12).ToList();
            var details = await Task.WhenAll(dvdSummaries.Select(x => _tmdb.GetEpisodeGroupDetailsAsync(x.Id)));
            _tmdbEpisodeGroupCatalogs = details;
        }
        catch (Exception ex)
        {
            MediaForgeLogger.Warn($"TMDB DVD episode groups unavailable for '{_selected.Title}': {ex.Message}");
        }
    }

    private TmdbEpisodeGroupSetDto? FindDvdGroupForDisc()
    {
        if (_selected == null || _tmdbEpisodeGroupCatalogs.Count == 0) return null;
        var labels = _jobs.Select(EpisodeIdentificationService.GetDiscLabel)
            .Where(label => !string.IsNullOrWhiteSpace(label)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (labels.Count == 0) return null;
        var matches = labels.Select(label => _dvdEpisodeGroupMatcher.FindDiscGroup(label, _selected.Title, _tmdbEpisodeGroupCatalogs))
            .Where(group => group != null).Cast<TmdbEpisodeGroupSetDto>().DistinctBy(group => group.Id).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private IReadOnlyDictionary<long, IReadOnlyList<TmdbEpisode>> GetDvdTitleAssignments()
    {
        var group = FindDvdGroupForDisc();
        return group == null
            ? new Dictionary<long, IReadOnlyList<TmdbEpisode>>()
            : _dvdEpisodeGroupMatcher.MatchTitles(_jobs, group);
    }
    private sealed record IndependentEpisodeCheck(bool HasMatch, bool NumberingAgrees, string Detail);

    private IndependentEpisodeCheck GetIndependentEpisodeEvidence(TmdbEpisode? candidate)
    {
        if (_tvMazeCatalog == null || candidate == null) return new(false, false, string.Empty);
        var candidateTitle = Normalize(candidate.Name);
        var ranked = _tvMazeCatalog.Episodes.Select(episode =>
        {
            var title = Normalize(episode.Name);
            var exactTitle = candidateTitle.Length > 0 && candidateTitle == title;
            var sameDate = DateTime.TryParse(candidate.AirDate, out var tmdbDate) &&
                           DateTime.TryParse(episode.AirDate, out var mazeDate) && tmdbDate.Date == mazeDate.Date;
            var sameRuntime = candidate.RuntimeMinutes.HasValue && episode.Runtime.HasValue &&
                              Math.Abs(candidate.RuntimeMinutes.Value - episode.Runtime.Value) <= 2;
            var score = exactTitle ? 85 : 0;
            if (sameDate) score += 55;
            if (sameRuntime) score += 10;
            return (Episode: episode, Score: score);
        }).OrderByDescending(x => x.Score).ToList();
        if (ranked.Count == 0 || ranked[0].Score < 80 ||
            (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < 10)) return new(false, false, string.Empty);

        var match = ranked[0].Episode;
        var evidence = new List<string>();
        evidence.Add($"TVmaze aired order S{match.Season ?? 0:00}E{match.Number ?? 0:00}");
        var linkedLists = _tvMazeCatalog.DvdLists.SelectMany(list =>
                (list._Embedded?.AlternateEpisodes ?? new()).Where(alt =>
                    alt._Links?.Episodes?.Any(link => link.Href.EndsWith("/" + match.Id, StringComparison.Ordinal)) == true)
                    .Select(alt => (List: list, Episode: alt)))
            .ToList();
        foreach (var item in linkedLists.Take(3))
            evidence.Add($"TVmaze DVD order S{item.Episode.Season ?? 0:00}E{item.Episode.Number ?? 0:00}");
        var agrees = candidate.SeasonNumber == match.Season && candidate.EpisodeNumber == match.Number;
        return new(true, agrees, string.Join("; ", evidence));
    }
    private async Task<TmdbSeasonSummary?> InferSeasonFromDiscContextAsync(IReadOnlyList<TmdbSeasonSummary> seasons)
    {
        var candidates = seasons.Where(x => x.SeasonNumber > 0).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault() ?? seasons.FirstOrDefault();
        var dvdGroup = FindDvdGroupForDisc();
        var dvdSeasons = dvdGroup?.Episodes.Select(x => x.SeasonNumber).Where(x => x > 0).Distinct().ToList() ?? new List<int>();
        if (dvdSeasons.Count == 1)
        {
            var dvdSeason = candidates.FirstOrDefault(x => x.SeasonNumber == dvdSeasons[0]);
            if (dvdSeason != null)
            {
                StatusText.Text = "TMDB DVD order group matched Season " + dvdSeason.SeasonNumber.ToString("00") + ".";
                return dvdSeason;
            }
        }

        StatusText.Text = $"Comparing disc/file clues across {candidates.Count} seasons...";
        var ranked = new List<(TmdbSeasonSummary Season, int Score, int Support)>();
        foreach (var summary in candidates)
        {
            var catalog = await _tmdb!.GetSeasonAsync(_selected!.Id, summary.SeasonNumber);
            var bestByEpisode = new Dictionary<int, int>();
            foreach (var job in _jobs)
            {
                var match = _episodeIdentificationService.Match(job, summary.SeasonNumber, catalog.Episodes, _selected.Title, useDiscContext: true);
                if (match.Candidates.Count == 0) continue;
                var best = match.Candidates[0];
                bestByEpisode[best.Episode.EpisodeNumber] = Math.Max(bestByEpisode.GetValueOrDefault(best.Episode.EpisodeNumber), best.Score);
            }
            var evidence = bestByEpisode.Values.OrderByDescending(x => x).Take(3).ToList();
            ranked.Add((summary, evidence.Sum(), evidence.Count(x => x >= 55)));
        }

        var ordered = ranked.OrderByDescending(x => x.Support).ThenByDescending(x => x.Score).ToList();
        if (ordered.Count > 1 && ordered[0].Support > 0 &&
            (ordered[0].Support > ordered[1].Support || ordered[0].Score - ordered[1].Score >= 18))
        {
            StatusText.Text = $"Disc and file title clues suggest Season {ordered[0].Season.SeasonNumber:00}; verify episode matches below.";
            return ordered[0].Season;
        }
        return null;
    }

    private async void AutoSeason_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected.MediaType != "tv")
        {
            StatusText.Text = "Select a TV show first, or use Auto Identify to search for one.";
            return;
        }
        await LoadSeasonsAndAutoMapAsync();
    }
    private static int? InferSeasonNumber(IEnumerable<MediaJob> jobs)
    {
        foreach (var j in jobs)
        {
            if (j.Season is > 0) return j.Season.Value;
            var text = j.SourceName + " " + j.SourcePath + " " + j.Title;
            var m = Regex.Match(text, @"(?:^|[^a-z])s(?:eason)?[ ._-]?(\d{1,2})(?:[^0-9]|$)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
        }
        return null;
    }

    private void EpisodeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.SelectedItem is TmdbEpisode ep && !string.IsNullOrWhiteSpace(ep.StillUrl))
            EpisodePreviewImage.Source = new BitmapImage(new Uri(ep.StillUrl));
    }

    private async void SeasonBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSeasons || _loadingEpisodes) return;
        if (_selected == null || _selected.MediaType != "tv") return;
        if (SeasonBox?.SelectedItem is not TmdbSeasonSummary season) return;

        try
        {
            await LoadEpisodesAsync(season.SeasonNumber);
        }
        catch (Exception ex)
        {
            MediaForgeLogger.Error("Season selection failed.", ex);
            StatusText.Text = "Season selection failed: " + ex.Message;
        }
    }

    private async Task LoadEpisodesAsync(int seasonNumber)
    {
        if (_selected == null || _selected.Id <= 0 || _loadingEpisodes) return;
        _loadingEpisodes = true;
        try
        {
            _tmdb ??= new TmdbService(_settings.TmdbAccessToken);
            StatusText.Text = "Loading episodes and reading source chapter timings...";
            _season = await _tmdb.GetSeasonAsync(_selected.Id, seasonNumber);
            await LoadChapterTimingsAsync();

            IReadOnlyList<TmdbEpisode> catalogEpisodes = _season.Episodes;
            var dvdAssignments = GetDvdTitleAssignments();
            var hasCompilation = _jobs.Any(job => _structureService.Analyze(job, _season.Episodes).EstimatedEpisodeCount > 1) ||
                                 dvdAssignments.Values.Any(assigned => assigned.Count > 1);
            if (hasCompilation && _availableSeasons.Count > 1)
            {
                // Compilation segments can cross a season boundary, so expose the
                // complete show episode catalog as candidate choices.
                var otherSeasonNumbers = _availableSeasons.Select(x => x.SeasonNumber).Where(x => x != seasonNumber);
                var otherSeasons = await Task.WhenAll(otherSeasonNumbers.Select(x => _tmdb.GetSeasonAsync(_selected.Id, x)));
                catalogEpisodes = _season.Episodes.Concat(otherSeasons.SelectMany(x => x.Episodes))
                    .OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber).ToList();
            }

            var dvdGroup = FindDvdGroupForDisc();
            if (dvdGroup != null)
            {
                // The release set narrows candidates to its published episode list;
                // it may contain several physical discs, so this alone is not a match.
                var releaseEpisodes = dvdGroup.Episodes.Select(x => new TmdbEpisode
                {
                    Id = x.Id, Name = x.Name, SeasonNumber = x.SeasonNumber,
                    EpisodeNumber = x.EpisodeNumber, RuntimeMinutes = x.RuntimeMinutes, AirDate = x.AirDate
                }).ToList();
                var releaseKeys = releaseEpisodes.Select(x => (x.SeasonNumber, x.EpisodeNumber)).ToHashSet();
                var availableEpisodes = catalogEpisodes;
                catalogEpisodes = availableEpisodes.Where(x => releaseKeys.Contains((x.SeasonNumber, x.EpisodeNumber)))
                    .Concat(releaseEpisodes.Where(x => !availableEpisodes.Any(existing => existing.SeasonNumber == x.SeasonNumber && existing.EpisodeNumber == x.EpisodeNumber)))
                    .OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber).ToList();
            }

            _titleCardAssignments.Clear();
            await AnalyzeTitleCardsAsync(catalogEpisodes);
            BuildMappingRows(catalogEpisodes, seasonNumber);
            MappingGrid.Items.Refresh();
            var splitCount = _mappingRows.Count(x => x.IsSplitSegment);
            var splitSources = _mappingRows.Where(x => x.IsSplitSegment).Select(x => x.Job.Id).Distinct().Count();
            var sourceSummary = dvdGroup != null
                ? $"TMDB DVD group {dvdGroup.Name} + TMDB/TVmaze"
                : _tvMazeCatalog == null ? "TMDB" : $"TMDB + TVmaze ({_tvMazeCatalog.DvdLists.Count} DVD alternate list(s))";
            MappingHint.Text = splitSources > 0
                ? $"{sourceSummary}: {catalogEpisodes.Count} episode candidates; runtime/chapter patterns suggest {splitSources} compilation(s) and {splitCount} provisional ranges. Select the episode for each segment."
                : $"{sourceSummary}: loaded {_season.Episodes.Count} episodes for Season {seasonNumber:00}. Cross-source title, date, runtime, and DVD-order evidence appears in each match tooltip.";
            StatusText.Text = $"Season {seasonNumber:00}: {_season.Episodes.Count} episodes loaded.";
        }
        catch (Exception ex) { MediaForgeLogger.Error("Episode lookup failed.", ex); StatusText.Text = "Episode lookup failed: " + ex.Message; }
        finally { _loadingEpisodes = false; }
    }
    private async Task AnalyzeTitleCardsAsync(IReadOnlyList<TmdbEpisode> episodes)
    {
        if (string.IsNullOrWhiteSpace(_settings.FfmpegPath) || !File.Exists(_settings.FfmpegPath) ||
            string.IsNullOrWhiteSpace(_settings.TesseractPath) || !File.Exists(_settings.TesseractPath))
            return;

        var reader = new TitleCardOcrService(new ProcessRunner());
        foreach (var job in _jobs.Where(x => !string.IsNullOrWhiteSpace(x.SourcePath) && File.Exists(x.SourcePath)))
        {
            var structure = _structureService.Analyze(job, episodes);
            IReadOnlyList<double> times;
            if (structure.IsMultiEpisode && structure.ChapterRanges.Count == structure.EstimatedEpisodeCount &&
                job.ChapterDurationsSeconds.Count > 0)
            {
                times = TitleCardOcrService.PlanSampleTimes(job.ChapterDurationsSeconds, structure.ChapterRanges);
            }
            else
            {
                times = new[] { 45d };
            }

            try
            {
                StatusText.Text = $"Reading title card(s): {job.SourceName}";
                var samples = await reader.ReadFramesAsync(job, times, _settings.FfmpegPath, _settings.TesseractPath);
                var matches = samples.Select(sample => _titleCardMatcher.Match(sample.Text, episodes)).ToList();
                if (matches.Any(match => match != null)) _titleCardAssignments[job.Id] = matches;
            }
            catch (Exception ex)
            {
                MediaForgeLogger.Warn($"Title-card OCR was skipped for '{job.SourceName}': {ex.Message}");
            }
        }
    }
    private async Task LoadChapterTimingsAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.HandBrakeCliPath) || !File.Exists(_settings.HandBrakeCliPath)) return;
        var scanner = new HandBrakeService(new ProcessRunner());
        foreach (var job in _jobs.Where(x => x.ChapterDurationsSeconds.Count == 0 &&
                     !string.IsNullOrWhiteSpace(x.SourcePath) && File.Exists(x.SourcePath) &&
                     x.Status != JobStatus.Encoding && x.Status != JobStatus.Ripping))
        {
            try
            {
                StatusText.Text = $"Reading chapter timings: {job.SourceName}";
                job.ChapterDurationsSeconds = (await scanner.ScanChapterDurationsAsync(_settings.HandBrakeCliPath, job.SourcePath)).ToList();
                job.SourceChapterCount = job.ChapterDurationsSeconds.Count;
                if (job.SourceDurationSeconds is null)
                    job.SourceDurationSeconds = (long)Math.Round(job.ChapterDurationsSeconds.Sum());
                _db.Upsert(job);
            }
            catch (Exception ex)
            {
                MediaForgeLogger.Warn($"Could not scan chapters for '{job.SourceName}': {ex.Message}");
            }
        }
    }
    private void BuildMappingRows(IReadOnlyList<TmdbEpisode> episodes, int seasonNumber)
    {
        _mappingRows.Clear();
        var dvdAssignments = GetDvdTitleAssignments();
        var matches = _jobs.ToDictionary(x => x.Id, x => _episodeIdentificationService.Match(x, seasonNumber, episodes, _selected?.Title));
        var conflictingTitleCardEpisodeNumbers = _titleCardAssignments.Values
            .SelectMany(x => x)
            .Where(x => x != null)
            .GroupBy(x => x!.Episode.EpisodeNumber)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var collisions = matches.Values
            .Where(x => x.SuggestedEpisode != null)
            .GroupBy(x => x.SuggestedEpisode!.EpisodeNumber)
            .Where(x => x.Count() > 1)
            .SelectMany(x => x)
            .ToHashSet();

        foreach (var job in _jobs)
        {
            if (dvdAssignments.TryGetValue(job.Id, out var dvdEpisodes) &&
                TryBuildDvdOrderRows(job, episodes, dvdEpisodes))
                continue;

            var analysis = _structureService.Analyze(job, episodes);
            _titleCardAssignments.TryGetValue(job.Id, out var cardMatches);
            var match = matches[job.Id];
            var proposedSingleCardMatch = cardMatches?.Count == 1 ? cardMatches[0] : null;
            var singleCardMatch = !analysis.IsMultiEpisode && proposedSingleCardMatch != null &&
                !conflictingTitleCardEpisodeNumbers.Contains(proposedSingleCardMatch.Episode.EpisodeNumber)
                ? proposedSingleCardMatch : null;
            var selectedEpisode = singleCardMatch?.Episode ?? (collisions.Contains(match) ? null : match.SuggestedEpisode);
            var matchDetail = collisions.Contains(match)
                ? "More than one source title matched this episode. Resolve the duplicate assignments manually."
                                : match.Detail;
            var crossSourceEvidence = GetIndependentEpisodeEvidence(selectedEpisode ?? match.Candidates.FirstOrDefault()?.Episode);
            var numberingConflict = singleCardMatch == null && selectedEpisode != null && crossSourceEvidence.HasMatch && !crossSourceEvidence.NumberingAgrees;
            if (crossSourceEvidence.HasMatch)
                matchDetail += $" Independent metadata cross-check: {crossSourceEvidence.Detail}. " +
                    (crossSourceEvidence.NumberingAgrees
                        ? "TMDB and TVmaze aired-order season/episode numbers agree."
                        : "TMDB and TVmaze aired-order season/episode numbers differ; confirm the numbering scheme before applying.");
            var matchDisplay = singleCardMatch != null
                ? $"Auto • title card {singleCardMatch.Confidence}%"
                : collisions.Contains(match)
                ? "Review • duplicate"
                : numberingConflict
                    ? "Review • numbering differs"
                    : match.SuggestedEpisode != null
                    ? (crossSourceEvidence.HasMatch && crossSourceEvidence.NumberingAgrees ? $"Auto • {match.Confidence}% • corroborated" : $"Auto • {match.Confidence}%")
                    : match.Candidates.Count > 0
                        ? $"Review • {match.Candidates[0].Score}%"
                        : "Unmatched";

            // Runtime can indicate a compilation, but it cannot identify the component
            // episodes or their order. Offer reviewable chapter ranges and require the
            // user to choose each episode rather than assuming aired order.
            if (analysis.IsMultiEpisode && analysis.Confidence >= 85 &&
                analysis.ChapterRanges.Count == analysis.EstimatedEpisodeCount && !IsAlreadyEncoded(job))
            {
                for (var segmentIndex = 0; segmentIndex < analysis.ChapterRanges.Count; segmentIndex++)
                {
                    var range = analysis.ChapterRanges[segmentIndex];
                    var proposedCardMatch = cardMatches != null && cardMatches.Count == analysis.ChapterRanges.Count
                        ? cardMatches[segmentIndex] : null;
                    var cardMatch = proposedCardMatch != null &&
                        !conflictingTitleCardEpisodeNumbers.Contains(proposedCardMatch.Episode.EpisodeNumber)
                        ? proposedCardMatch : null;
                    _mappingRows.Add(new TvMappingRow(job, episodes)
                    {
                        SelectedEpisode = cardMatch?.Episode,
                        ChapterStart = range.Start,
                        ChapterEnd = range.End,
                        IsSplitSegment = true,
                        StructureLabel = analysis.StructureLabel,
                        StructureConfidence = analysis.Confidence,
                        StructureDetail = analysis.Detail + (cardMatch != null
                            ? " The title card was read locally and matched against show metadata."
                            : " Chapter boundaries are provisional; select the episode for this segment."),
                        MatchDisplay = cardMatch != null ? $"Title card OCR • {cardMatch.Confidence}%" : "Review segment",
                        MatchDetail = cardMatch != null
                            ? $"Recognized title card text: {cardMatch.RecognizedText}"
                            : proposedCardMatch != null
                                ? "The title card matched an episode that was assigned to multiple segments. Resolve the duplicate assignments manually."
                                : "A runtime/chapter pattern suggests a segment, but it does not identify the episode."
                    });
                }
                continue;
            }

            _mappingRows.Add(new TvMappingRow(job, episodes)
            {
                SelectedEpisode = singleCardMatch != null ? singleCardMatch.Episode : numberingConflict ? null : selectedEpisode,
                ChapterStart = job.ChapterStart,
                ChapterEnd = job.ChapterEnd,
                StructureLabel = analysis.StructureLabel,
                StructureConfidence = analysis.Confidence,
                StructureDetail = analysis.Detail,
                MatchDisplay = matchDisplay,
                MatchDetail = singleCardMatch != null
                    ? $"Recognized title card text: {singleCardMatch.RecognizedText}"
                    : matchDetail
            });
        }
    }
    private bool TryBuildDvdOrderRows(MediaJob job, IReadOnlyList<TmdbEpisode> visibleEpisodes, IReadOnlyList<TmdbEpisode> groupEpisodes)
    {
        var dvdGroup = FindDvdGroupForDisc();
        if (dvdGroup == null || groupEpisodes.Count == 0) return false;
        var resolved = groupEpisodes.Select(mapped => visibleEpisodes.FirstOrDefault(ep => ep.Id == mapped.Id) ?? mapped).ToList();
        var analysis = _structureService.Analyze(job, resolved);
        var ranges = analysis.ChapterRanges.Count == resolved.Count ? analysis.ChapterRanges : Array.Empty<ChapterRangeSuggestion>();
        var titleOrder = EpisodeIdentificationService.GetTitleOrder(job);
        var titleLabel = titleOrder.HasValue ? $"MakeMKV title t{titleOrder.Value:00}" : job.SourceName;
        for (var i = 0; i < resolved.Count; i++)
        {
            var episode = resolved[i];
            var hasRange = ranges.Count == resolved.Count;
            var range = hasRange ? ranges[i] : null;
            _mappingRows.Add(new TvMappingRow(job, visibleEpisodes)
            {
                SelectedEpisode = episode,
                ChapterStart = range?.Start,
                ChapterEnd = range?.End,
                IsSplitSegment = resolved.Count > 1,
                StructureLabel = resolved.Count > 1 ? "DVD group split" : analysis.StructureLabel,
                StructureConfidence = hasRange ? analysis.Confidence : 0,
                StructureDetail = resolved.Count > 1
                    ? analysis.Detail + (hasRange ? " TMDB DVD ordering identifies the component episodes; review chapter boundaries." : " TMDB DVD ordering identifies the component episodes, but chapter boundaries still need manual review.")
                    : analysis.Detail,
                MatchDisplay = resolved.Count > 1 && !hasRange ? "DVD order • chapter review" : "TMDB DVD order",
                MatchDetail = $"Matched {titleLabel} against TMDB DVD group '{dvdGroup.Name}': {episode.Display}."
            });
        }
        return true;
    }
    private static bool IsAlreadyEncoded(MediaJob job) =>
        (job.Status == JobStatus.Encoded || job.Status == JobStatus.Completed) &&
        !string.IsNullOrWhiteSpace(job.OutputPath) && File.Exists(job.OutputPath);

    private void AutoMap_Click(object sender, RoutedEventArgs e)
    {
        if (_season == null) { StatusText.Text = "Select a season first."; return; }
        BuildMappingRows(_season.Episodes, _season.SeasonNumber);
        MappingGrid.Items.Refresh();
        var reviewCount = _mappingRows.Count(x => x.SelectedEpisode == null);
        StatusText.Text = reviewCount > 0
            ? $"Episode evidence matched where strong; {reviewCount} segment(s) need review."
            : "Episode assignments use explicit numbering or a unique title match.";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { StatusText.Text = "Select or Auto Identify a TMDB result first."; return; }
        var isTv = _selected.MediaType == "tv";
        if (!isTv && _jobs.Count > 1) { StatusText.Text = "A movie identification can only be applied to one source title."; return; }
        if (isTv)
        {
            if (_season == null || _mappingRows.Count == 0 || _mappingRows.Any(x => x.SelectedEpisode == null))
            {
                StatusText.Text = "Select a season and map every title to an episode.";
                return;
            }
            if (_mappingRows.Any(row => row.IsSplitSegment && (row.ChapterStart is not > 0 || row.ChapterEnd is not > 0 || row.ChapterEnd.Value < row.ChapterStart.Value)))
            {
                StatusText.Text = "Episode order is known, but set valid chapter ranges before applying a multi-episode split.";
                return;
            }
            if (_mappingRows.GroupBy(x => new { x.SelectedEpisode!.SeasonNumber, x.SelectedEpisode.EpisodeNumber }).Any(g => g.Count() > 1))
            {
                StatusText.Text = "Each TMDB episode can only be assigned once in this import.";
                return;
            }

            var rowsByJob = _mappingRows.GroupBy(x => x.Job.Id).ToList();
            foreach (var group in rowsByJob)
            {
                var original = group.First().Job;
                var rows = group.ToList();
                if (rows.Count > 1 && IsAlreadyEncoded(original))
                {
                    StatusText.Text = $"Cannot split '{original.SourceName}' because it is already encoded. Split the MKV before encoding.";
                    return;
                }
            }

            foreach (var group in rowsByJob)
            {
                var original = group.First().Job;
                var rows = group.OrderBy(x => x.SegmentNumber).ToList();
                var targets = rows.Select((_, i) => i == 0 ? original : CloneJob(original)).ToList();
                var existingIds = _db.LoadAll().Select(x => x.Id).ToHashSet();
                foreach (var target in targets.Skip(1))
                {
                    while (existingIds.Contains(target.Id)) target.Id++;
                    existingIds.Add(target.Id);
                }
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var target = targets[i];
                    ApplyTvMetadata(target, row.SelectedEpisode!, row);
                    if (rows.Count > 1)
                    {
                        target.SourceName = BuildSegmentSourceName(original.SourceName, row.ChapterStart, row.ChapterEnd, row.SelectedEpisode!.EpisodeNumber);
                        target.OutputPath = null;
                        target.ErrorMessage = null;
                        target.Progress = 0;
                        target.EncodedSizeBytes = null;
                        target.Status = JobStatus.Identify;
                        target.Stage = "Identified - split from multi-episode source";
                    }
                    _db.Upsert(target);
                }
            }
        }
        else
        {
            var j = _jobs[0];
            j.MediaType = "Movie"; j.Title = _selected.Title; j.Year = int.TryParse(_selected.Year, out var y) ? y : null; j.TmdbId = _selected.Id; j.TmdbType = "movie";
            var wasEncoded = IsAlreadyEncoded(j);
            j.Status = wasEncoded ? JobStatus.Encoded : JobStatus.Identify;
            j.Stage = wasEncoded ? "Identified - ready to organize" : "Identified";
            _db.Upsert(j);
        }
        DialogResult = true;
    }

    private void ApplyTvMetadata(MediaJob job, TmdbEpisode episode, TvMappingRow row)
    {
        job.MediaType = "TV Show";
        job.Title = _selected!.Title;
        job.Year = int.TryParse(_selected.Year, out var year) ? year : null;
        job.TmdbId = _selected.Id;
        job.TmdbType = "tv";
        job.Season = episode.SeasonNumber;
        job.Episode = episode.EpisodeNumber;
        job.EpisodeTitle = episode.Name;
        job.ChapterStart = row.ChapterStart;
        job.ChapterEnd = row.ChapterEnd;

        var wasEncoded = IsAlreadyEncoded(job);
        job.Status = wasEncoded ? JobStatus.Encoded : JobStatus.Identify;
        job.Stage = wasEncoded ? "Identified - ready to organize" : "Identified";
    }

    private static MediaJob CloneJob(MediaJob source)
    {
        return new MediaJob
        {
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourceName = source.SourceName,
            SourcePath = source.SourcePath,
            Status = JobStatus.Identify,
            Stage = "Identified - split from multi-episode source",
            Progress = 0,
            SourceDurationSeconds = source.SourceDurationSeconds,
            SourceChapterCount = source.SourceChapterCount,
            SourceKind = source.SourceKind,
            MediaType = source.MediaType,
            Title = source.Title,
            Year = source.Year,
            TmdbId = source.TmdbId,
            TmdbType = source.TmdbType,
            RippedSizeBytes = source.RippedSizeBytes
        };
    }

    private static string BuildSegmentSourceName(string original, int? start, int? end, int episode)
    {
        var suffix = start is > 0 && end is > 0 ? $" [Ch {start}-{end}]" : $" [Episode {episode:00}]";
        return original.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? original : original + suffix;
    }

    private void ManualTvApply_Click(object sender, RoutedEventArgs e)
    {
        if (_jobs.Count != 1) { StatusText.Text = "Select exactly one source for Manual TV Entry."; return; }
        if (string.IsNullOrWhiteSpace(ManualShowBox.Text) || !int.TryParse(ManualYearBox.Text, out var year) || !int.TryParse(ManualSeasonBox.Text, out var season) || !int.TryParse(ManualEpisodeBox.Text, out var episode) || episode < 1 || string.IsNullOrWhiteSpace(ManualEpisodeTitleBox.Text))
        { StatusText.Text = "Enter show, year, season, episode, and episode title."; return; }
        var j = _jobs[0];
        j.MediaType = "TV Show"; j.Title = ManualShowBox.Text.Trim(); j.Year = year; j.Season = season; j.Episode = episode; j.EpisodeTitle = ManualEpisodeTitleBox.Text.Trim(); j.TmdbId = null; j.TmdbType = "tv-manual";
        var encoded = j.Status == JobStatus.Encoded && !string.IsNullOrWhiteSpace(j.OutputPath) && File.Exists(j.OutputPath);
        j.Status = encoded ? JobStatus.Encoded : JobStatus.Identify; j.Stage = encoded ? "Manual TV metadata entered - ready to organize" : "Manual TV metadata entered";
        _db.Upsert(j); DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public sealed class TvMappingRow : INotifyPropertyChanged
    {
        public MediaJob Job { get; }
        public IReadOnlyList<TmdbEpisode> Episodes { get; }
        private TmdbEpisode? _selectedEpisode;
        private int? _chapterStart;
        private int? _chapterEnd;
        public int? ChapterStart { get => _chapterStart; set { if (_chapterStart == value) return; _chapterStart = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterStart))); } }
        public int? ChapterEnd { get => _chapterEnd; set { if (_chapterEnd == value) return; _chapterEnd = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterEnd))); } }
        public TmdbEpisode? SelectedEpisode { get => _selectedEpisode; set { if (ReferenceEquals(_selectedEpisode, value)) return; _selectedEpisode = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedEpisode))); } }
        public bool IsSplitSegment { get; set; }
        public int StructureConfidence { get; set; }
        public string StructureLabel { get; set; } = "";
        public string StructureDetail { get; set; } = "";
        public string MatchDisplay { get; set; } = "";
        public string MatchDetail { get; set; } = "";
        public string StructureDisplay => IsSplitSegment
            ? $"{StructureLabel} • {StructureConfidence}% • Ch. {ChapterStart}-{ChapterEnd}"
            : $"{StructureLabel} • {StructureConfidence}%";
        public int SegmentNumber => IsSplitSegment && ChapterStart.HasValue ? ChapterStart.Value : 0;
        public TvMappingRow(MediaJob job, IReadOnlyList<TmdbEpisode> episodes) { Job = job; Episodes = episodes; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

}







