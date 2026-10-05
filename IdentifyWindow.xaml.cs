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
    private readonly ObservableCollection<TmdbSearchResult> _results = new();
    private readonly ObservableCollection<TvMappingRow> _mappingRows = new();
    private TmdbSearchResult? _selected;
    private TmdbSeason? _season;

    public IdentifyWindow(IReadOnlyList<MediaJob> jobs, AppSettings settings, JobDatabase db, bool autoIdentify = false)
    {
        InitializeComponent();
        ThemeManager.Apply(this, settings.IsDarkMode);
        _jobs = jobs;
        _settings = settings;
        _db = db;
        ResultsGrid.ItemsSource = _results;
        MappingGrid.ItemsSource = _mappingRows;
        TypeBox.SelectedIndex = jobs.FirstOrDefault()?.MediaType == "TV Show" ? 1 : 0;
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
            foreach (var raw in new[]
            {
                Path.GetFileNameWithoutExtension(j.SourceName),
                Path.GetFileNameWithoutExtension(j.FileName),
                Path.GetFileNameWithoutExtension(j.SourcePath),
                Path.GetFileName(Path.GetDirectoryName(j.SourcePath) ?? "")
            })
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
        name = Regex.Replace(name, @"\b(1080p|2160p|720p|576p|480p|x264|x265|h264|h265|avc|hevc|bluray|blu-ray|web-dl|webrip|remux|proper|repack|extended|special edition|uncut|criterion)\b", " ", RegexOptions.IgnoreCase);

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

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(TypeBox.SelectedIndex == 0);

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
                StatusText.Text = "MediaForge could not derive a usable title from the source metadata. Rename the Source Name to something descriptive and retry.";
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
                .ThenByDescending(x => x.Candidate.Year)
                .ToList();

            _results.Clear();
            foreach (var x in ranked.Take(15)) _results.Add(x.Candidate);
            _selected = ranked[0].Candidate;
            ResultsGrid.SelectedItem = _selected;
            ResultsGrid.ScrollIntoView(_selected);
            TypeBox.SelectedIndex = _selected.MediaType == "tv" ? 1 : 0;

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
            var seasons = (await _tmdb.GetSeasonsAsync(_selected.Id)).Where(x => x.SeasonNumber >= 0).ToList();
            SeasonBox.ItemsSource = seasons;
            var inferredSeason = InferSeasonNumber(_jobs);
            var target = seasons.FirstOrDefault(x => x.SeasonNumber == inferredSeason) ?? seasons.FirstOrDefault(x => x.SeasonNumber > 0) ?? seasons.FirstOrDefault();
            if (target != null) SeasonBox.SelectedItem = target;
            else StatusText.Text = "The show was found, but TMDB returned no seasons.";
        }
        catch (Exception ex) { StatusText.Text = "Season lookup failed: " + ex.Message; }
    }

    private static int? InferSeasonNumber(IEnumerable<MediaJob> jobs)
    {
        foreach (var j in jobs)
        {
            var text = j.SourceName + " " + j.SourcePath;
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
        if (_selected == null || _selected.MediaType != "tv" || SeasonBox.SelectedItem is not TmdbSeasonSummary season) return;
        await LoadEpisodesAsync(season.SeasonNumber);
    }

    private async Task LoadEpisodesAsync(int seasonNumber)
    {
        if (_selected == null) return;
        try
        {
            _tmdb ??= new TmdbService(_settings.TmdbAccessToken);
            StatusText.Text = "Loading episodes...";
            _season = await _tmdb.GetSeasonAsync(_selected.Id, seasonNumber);
            _mappingRows.Clear();
            var episodes = _season.Episodes.ToList();
            var used = new HashSet<int>();

            for (var i = 0; i < _jobs.Count; i++)
            {
                var row = new TvMappingRow(_jobs[i], episodes);
                if (_jobs[i].ChapterStart == null && _jobs[i].ChapterEnd == null && _jobs[i].RippedSizeBytes != null) { _jobs[i].ChapterStart = 1; _jobs[i].ChapterEnd = null; }
                // Prefer explicit SxxExx metadata in the source name/path.
                // If none is present, fall back to source order, which matches
                // the usual MakeMKV episode ordering for a TV disc.
                var inferred = InferEpisodeNumber(_jobs[i], seasonNumber);
                var selected = inferred.HasValue
                    ? episodes.FirstOrDefault(x => x.EpisodeNumber == inferred.Value && !used.Contains(x.EpisodeNumber))
                    : null;
                selected ??= episodes.FirstOrDefault(x => !used.Contains(x.EpisodeNumber));
                if (selected != null)
                {
                    row.SelectedEpisode = selected;
                    used.Add(selected.EpisodeNumber);
                }
                _mappingRows.Add(row);
            }
            MappingGrid.Items.Refresh();
            MappingHint.Text = $"{episodes.Count} episode(s) loaded. Actual TMDB season/episode numbers are shown below; filename episode numbers are used when available, otherwise source order is used.";
            StatusText.Text = $"Season {seasonNumber:00}: {episodes.Count} episodes loaded.";
        }
        catch (Exception ex) { MediaForgeLogger.Error("Episode lookup failed.", ex); StatusText.Text = "Episode lookup failed: " + ex.Message; }
    }

    private static int? InferEpisodeNumber(MediaJob job, int selectedSeason)
    {
        var text = job.SourceName + " " + job.SourcePath;
        var match = Regex.Match(text, @"\bs(?:eason)?[ ._-]?(\d{1,2})[ ._-]*e[ ._-]?(\d{1,3})\b", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var season) && int.TryParse(match.Groups[2].Value, out var episode) && season == selectedSeason)
            return episode;

        match = Regex.Match(text, @"\b(?:episode|ep)[ ._-]?(\d{1,3})\b", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var ep))
            return ep;
        return null;
    }

    private void AutoMap_Click(object sender, RoutedEventArgs e)
    {
        if (_season == null) { StatusText.Text = "Select a season first."; return; }
        for (var i = 0; i < _mappingRows.Count; i++)
            _mappingRows[i].SelectedEpisode = i < _season.Episodes.Count ? _season.Episodes[i] : null;
        MappingGrid.Items.Refresh();
        StatusText.Text = "Episodes automatically mapped in source order.";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { StatusText.Text = "Select or Auto Identify a TMDB result first."; return; }
        var isTv = _selected.MediaType == "tv";
        if (!isTv && _jobs.Count > 1) { StatusText.Text = "A movie identification can only be applied to one source title."; return; }
        if (isTv)
        {
            if (_season == null || _mappingRows.Count != _jobs.Count || _mappingRows.Any(x => x.SelectedEpisode == null)) { StatusText.Text = "Select a season and map every title to an episode."; return; }
            if (_mappingRows.GroupBy(x => new { x.SelectedEpisode!.SeasonNumber, x.SelectedEpisode.EpisodeNumber }).Any(g => g.Count() > 1)) { StatusText.Text = "Each ripped title must map to a different episode."; return; }
            foreach (var row in _mappingRows)
            {
                var ep = row.SelectedEpisode!; var j = row.Job;
                j.MediaType = "TV Show"; j.Title = _selected.Title; j.Year = int.TryParse(_selected.Year, out var y) ? y : null; j.TmdbId = _selected.Id; j.TmdbType = "tv";
                j.Season = ep.SeasonNumber; j.Episode = ep.EpisodeNumber; j.EpisodeTitle = ep.Name;
                var wasEncoded = j.Status == JobStatus.Encoded && !string.IsNullOrWhiteSpace(j.OutputPath) && File.Exists(j.OutputPath);
                j.Status = wasEncoded ? JobStatus.Encoded : JobStatus.Identify;
                j.Stage = wasEncoded ? "Identified - ready to organize" : "Identified";
                _db.Upsert(j);
            }
        }
        else
        {
            var j = _jobs[0];
            j.MediaType = "Movie"; j.Title = _selected.Title; j.Year = int.TryParse(_selected.Year, out var y) ? y : null; j.TmdbId = _selected.Id; j.TmdbType = "movie";
            var wasEncoded = j.Status == JobStatus.Encoded && !string.IsNullOrWhiteSpace(j.OutputPath) && File.Exists(j.OutputPath);
            j.Status = wasEncoded ? JobStatus.Encoded : JobStatus.Identify;
            j.Stage = wasEncoded ? "Identified - ready to organize" : "Identified";
            _db.Upsert(j);
        }
        DialogResult = true;
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
        public TmdbEpisode? SelectedEpisode { get => _selectedEpisode; set { if (ReferenceEquals(_selectedEpisode, value)) return; _selectedEpisode = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedEpisode))); } }
        public TvMappingRow(MediaJob job, IReadOnlyList<TmdbEpisode> episodes) { Job = job; Episodes = episodes; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
