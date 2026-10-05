using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaForge.Models;

namespace MediaForge.Services;

public sealed class TmdbService
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public TmdbService(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("TMDB API Read Access Token is required.", nameof(accessToken));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, CancellationToken token = default)
    {
        var dto = await GetAsync<TmdbSearchResponse>($"search/movie?query={Uri.EscapeDataString(query)}&include_adult=false&language=en-US", token);
        return dto.Results.Select(x => new TmdbSearchResult { Id=x.Id, Title=x.Title ?? "", OriginalTitle=x.OriginalTitle, Overview=x.Overview, Date=x.ReleaseDate, PosterPath=x.PosterPath, MediaType="movie" }).ToList();
    }

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchTvAsync(string query, CancellationToken token = default)
    {
        var dto = await GetAsync<TmdbSearchResponse>($"search/tv?query={Uri.EscapeDataString(query)}&include_adult=false&language=en-US", token);
        return dto.Results.Select(x => new TmdbSearchResult { Id=x.Id, Title=x.Name ?? "", OriginalTitle=x.OriginalName, Overview=x.Overview, Date=x.FirstAirDate, PosterPath=x.PosterPath, MediaType="tv" }).ToList();
    }

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchBothAsync(string query, CancellationToken token = default)
    {
        var movies = await SearchMoviesAsync(query, token);
        var tv = await SearchTvAsync(query, token);
        return movies.Concat(tv).ToList();
    }

    public async Task<IReadOnlyList<TmdbSeasonSummary>> GetSeasonsAsync(int showId, CancellationToken token = default)
    {
        var dto = await GetAsync<TmdbTvDetailsDto>($"tv/{showId}?language=en-US", token);
        return (dto.Seasons ?? new()).OrderBy(x => x.SeasonNumber)
            .Select(x => new TmdbSeasonSummary { SeasonNumber=x.SeasonNumber, Name=x.Name ?? $"Season {x.SeasonNumber:00}", AirDate=x.AirDate, EpisodeCount=x.EpisodeCount }).ToList();
    }

    public async Task<TmdbSeason> GetSeasonAsync(int showId, int seasonNumber, CancellationToken token = default)
    {
        var dto = await GetAsync<TmdbSeasonDto>($"tv/{showId}/season/{seasonNumber}?language=en-US", token);
        return new TmdbSeason
        {
            SeasonNumber=dto.SeasonNumber,
            Name=dto.Name ?? $"Season {dto.SeasonNumber:00}",
            Episodes=(dto.Episodes ?? new()).OrderBy(x=>x.EpisodeNumber).Select(x=>new TmdbEpisode
            {
                Id=x.Id,
                EpisodeNumber=x.EpisodeNumber,
                SeasonNumber=x.SeasonNumber,
                Name=x.Name ?? "",
                AirDate=x.AirDate, StillPath=x.StillPath
            }).ToList()
        };
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await _http.GetAsync(path, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"TMDB request failed ({(int)response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<T>(body, _json) ?? throw new InvalidOperationException("TMDB returned an empty response.");
    }
}
