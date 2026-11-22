using System.Text.Json.Serialization;
namespace MediaForge.Models;

public sealed class TmdbSearchResult
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? Date { get; set; }
    public string? PosterPath { get; set; }
    public string? StillPath { get; set; }
    public string StillUrl => string.IsNullOrWhiteSpace(StillPath) ? "" : $"https://image.tmdb.org/t/p/w300{StillPath}";
    public string? MediaType { get; set; }
    public string Year => !string.IsNullOrWhiteSpace(Date) && Date!.Length >= 4 ? Date[..4] : "";
    public string DisplayName => string.IsNullOrWhiteSpace(Year) ? Title : $"{Title} ({Year})";
    public string PosterUrl => string.IsNullOrWhiteSpace(PosterPath) ? "" : $"https://image.tmdb.org/t/p/w185{PosterPath}";
    public string DisplayType => MediaType == "tv" ? "TV" : MediaType == "movie" ? "Movie" : "";
}

public sealed class TmdbEpisode
{
    public int Id { get; set; }
    public int EpisodeNumber { get; set; }
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public string? AirDate { get; set; }
    public string? StillPath { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string StillUrl => string.IsNullOrWhiteSpace(StillPath) ? "" : $"https://image.tmdb.org/t/p/w300{StillPath}";
    public string Display => $"S{SeasonNumber:00}E{EpisodeNumber:00} - {Name}";
}

public sealed class TmdbSeason
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public List<TmdbEpisode> Episodes { get; set; } = new();
}

public sealed class TmdbSeasonSummary
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public string? AirDate { get; set; }
    public int EpisodeCount { get; set; }
    public string Display => SeasonNumber == 0 ? "Specials" : $"Season {SeasonNumber:00} - {Name}";
}

public sealed class TmdbTvDetailsDto
{
    [JsonPropertyName("seasons")] public List<TmdbSeasonSummaryDto>? Seasons { get; set; }
}

public sealed class TmdbSeasonSummaryDto
{
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("air_date")] public string? AirDate { get; set; }
    [JsonPropertyName("episode_count")] public int EpisodeCount { get; set; }
}

public sealed class TmdbSearchResponse
{
    [JsonPropertyName("results")] public List<TmdbSearchDto> Results { get; set; } = new();
}

public sealed class TmdbSearchDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("original_name")] public string? OriginalName { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
}

public sealed class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
    [JsonPropertyName("tvdb_id")] public int? TvdbId { get; set; }
}
public sealed class TmdbSeasonDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("episodes")] public List<TmdbEpisodeDto>? Episodes { get; set; }
}

public sealed class TmdbEpisodeDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("air_date")] public string? AirDate { get; set; }
    [JsonPropertyName("still_path")] public string? StillPath { get; set; }
    [JsonPropertyName("runtime")] public int? RuntimeMinutes { get; set; }
}
