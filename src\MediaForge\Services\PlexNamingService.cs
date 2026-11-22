using System.IO;
namespace MediaForge.Services;

public sealed class PlexNamingService
{
    public string MovieFolder(string title, int year) =>
        $"{Sanitize(title)} ({year})";

    public string MovieFile(string title, int year, string extension = ".mp4") =>
        $"{Sanitize(title)} ({year}){extension}";

    public string TvFolder(string title, int year) =>
        $"{Sanitize(title)} ({year})";

    public string SeasonFolder(int season) =>
        $"Season {season:00}";

    public string EpisodeFile(string title, int year, int season, int episode,
        string episodeTitle, string extension = ".mp4") =>
        $"{Sanitize(title)} ({year}) - s{season:00}e{episode:00} - {Sanitize(episodeTitle)}{extension}";

    public string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }
}
