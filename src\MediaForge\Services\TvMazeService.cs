using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaForge.Models;

namespace MediaForge.Services;

/// <summary>Public TVmaze cross-reference, including aired and DVD alternate episode order.</summary>
public sealed class TvMazeService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.tvmaze.com/"), Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaForge/1.0 (local media identification)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<TvMazeCatalog?> FindCatalogAsync(string showTitle, string? year, string? imdbId, CancellationToken token = default)
    {
        var matches = await GetAsync<List<TvMazeSearchDto>>($"search/shows?q={Uri.EscapeDataString(showTitle)}", token);
        var show = !string.IsNullOrWhiteSpace(imdbId)
            ? matches.Select(x => x.Show).FirstOrDefault(x => string.Equals(x?.Externals?.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (show == null)
        {
            show = matches
                .Select(x => x.Show)
                .Where(x => x != null)
                .OrderByDescending(x => ScoreShow(x!, showTitle, year))
                .FirstOrDefault();
            if (show == null || ScoreShow(show, showTitle, year) < 45) return null;
        }

        var episodes = await GetAsync<List<TvMazeEpisodeDto>>($"shows/{show.Id}/episodes?specials=1", token);
        var listSummaries = await GetAsync<List<TvMazeAlternateListDto>>($"shows/{show.Id}/alternatelists", token);
        var dvdLists = new List<TvMazeAlternateListDto>();
        foreach (var list in listSummaries.Where(x => x.DvdRelease).Take(5))
        {
            var expanded = await TryGetAsync<TvMazeAlternateListDto>($"alternatelists/{list.Id}?embed=alternateepisodes", token);
            if (expanded?._Embedded?.AlternateEpisodes is { Count: > 0 }) dvdLists.Add(expanded);
        }

        return new TvMazeCatalog
        {
            Show = show,
            Episodes = episodes,
            DvdLists = dvdLists
        };
    }

    private static int ScoreShow(TvMazeShowDto show, string title, string? year)
    {
        var wanted = Normalize(title);
        var actual = Normalize(show.Name);
        var score = wanted == actual ? 100 : TokenScore(wanted, actual);
        if (int.TryParse(year, out var wantedYear) && DateTime.TryParse(show.Premiered, out var premiered))
        {
            var difference = Math.Abs(wantedYear - premiered.Year);
            score += difference == 0 ? 18 : difference == 1 ? 8 : difference <= 3 ? 0 : -25;
        }
        return score;
    }

    private static int TokenScore(string left, string right)
    {
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (a.Count == 0 || b.Count == 0) return 0;
        return (int)Math.Round(80d * a.Intersect(b).Count() / Math.Max(a.Count, b.Count));
    }

    private static string Normalize(string value) => string.Join(' ', System.Text.RegularExpressions.Regex
        .Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static async Task<T?> TryGetAsync<T>(string path, CancellationToken token)
    {
        try { return await GetAsync<T>(path, token); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return default; }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { return default; }
    }

    private static async Task<T> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await Http.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            using var retry = await Http.GetAsync(path, token);
            retry.EnsureSuccessStatusCode();
            return await JsonSerializer.DeserializeAsync<T>(await retry.Content.ReadAsStreamAsync(token), Json, token)
                ?? throw new InvalidOperationException("TVmaze returned an empty response.");
        }
        response.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<T>(await response.Content.ReadAsStreamAsync(token), Json, token)
            ?? throw new InvalidOperationException("TVmaze returned an empty response.");
    }
}

public sealed class TvMazeCatalog
{
    public TvMazeShowDto Show { get; init; } = new();
    public List<TvMazeEpisodeDto> Episodes { get; init; } = new();
    public List<TvMazeAlternateListDto> DvdLists { get; init; } = new();
}

public sealed class TvMazeSearchDto
{
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("show")] public TvMazeShowDto? Show { get; set; }
}

public sealed class TvMazeShowDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("premiered")] public string? Premiered { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("externals")] public TvMazeExternalIds? Externals { get; set; }
}

public sealed class TvMazeExternalIds
{
    [JsonPropertyName("imdb")] public string? ImdbId { get; set; }
    [JsonPropertyName("thetvdb")] public int? TvdbId { get; set; }
}

public sealed class TvMazeEpisodeDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("season")] public int? Season { get; set; }
    [JsonPropertyName("number")] public int? Number { get; set; }
    [JsonPropertyName("airdate")] public string? AirDate { get; set; }
    [JsonPropertyName("runtime")] public int? Runtime { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
}

public sealed class TvMazeAlternateListDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("dvd_release")] public bool DvdRelease { get; set; }
    [JsonPropertyName("verbatim_order")] public bool VerbatimOrder { get; set; }
    [JsonPropertyName("_embedded")] public TvMazeAlternateEmbeddedDto? _Embedded { get; set; }
}

public sealed class TvMazeAlternateEmbeddedDto
{
    [JsonPropertyName("alternateepisodes")] public List<TvMazeAlternateEpisodeDto>? AlternateEpisodes { get; set; }
}

public sealed class TvMazeAlternateEpisodeDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("season")] public int? Season { get; set; }
    [JsonPropertyName("number")] public int? Number { get; set; }
    [JsonPropertyName("runtime")] public int? Runtime { get; set; }
    [JsonPropertyName("_links")] public TvMazeAlternateEpisodeLinks? _Links { get; set; }
}

public sealed class TvMazeAlternateEpisodeLinks
{
    [JsonPropertyName("episodes")] public List<TvMazeHrefDto>? Episodes { get; set; }
}

public sealed class TvMazeHrefDto
{
    [JsonPropertyName("href")] public string Href { get; set; } = "";
}
