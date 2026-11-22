using MediaForge.Models;

namespace MediaForge.Services;

public static class CliLocator
{
    private static readonly string[] MakeMkvNames = { "makemkvcon64.exe", "makemkvcon.exe" };
    private static readonly string[] HandBrakeNames = { "HandBrakeCLI.exe" };
    private static readonly string[] FfmpegNames = { "ffmpeg.exe" };
    private static readonly string[] TesseractNames = { "tesseract.exe" };

    public static AppSettings AutoRepair(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValidExecutable(settings.MakeMkvPath, MakeMkvNames)) settings.MakeMkvPath = FindMakeMkv();
        if (!IsValidExecutable(settings.HandBrakeCliPath, HandBrakeNames)) settings.HandBrakeCliPath = FindHandBrake();
        if (!IsValidExecutable(settings.FfmpegPath, FfmpegNames)) settings.FfmpegPath = FindFfmpeg();
        if (!IsValidExecutable(settings.TesseractPath, TesseractNames)) settings.TesseractPath = FindTesseract();
        return settings;
    }

    public static string FindMakeMkv() => FindExecutable(MakeMkvNames, new[]
    {
        @"C:\MakeMKV\makemkvcon64.exe",
        @"C:\MakeMKV\makemkvcon.exe",
        @"C:\Program Files\MakeMKV\makemkvcon64.exe",
        @"C:\Program Files\MakeMKV\makemkvcon.exe",
        @"C:\Program Files (x86)\MakeMKV\makemkvcon64.exe",
        @"C:\Program Files (x86)\MakeMKV\makemkvcon.exe"
    });

    public static string FindHandBrake() => FindExecutable(HandBrakeNames, new[]
    {
        @"C:\Program Files\HandBrake\HandBrakeCLI.exe",
        @"C:\Program Files (x86)\HandBrake\HandBrakeCLI.exe"
    }, includeUserLocations: true);

    public static string FindFfmpeg() => FindExecutable(FfmpegNames, new[] { @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\FFmpeg\bin\ffmpeg.exe" }, includeUserLocations: true);

    public static string FindTesseract() => FindExecutable(TesseractNames, new[] { @"C:\Program Files\Tesseract-OCR\tesseract.exe", @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe" }, includeUserLocations: true);

    private static string FindExecutable(string[] names, IEnumerable<string> explicitCandidates, bool includeUserLocations = false)
    {
        foreach (var candidate in explicitCandidates)
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        foreach (var name in names)
        {
            var path = FindOnPath(name);
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }

        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in programFiles.Where(Directory.Exists))
        {
            foreach (var appFolder in new[] { "MakeMKV", "HandBrake" })
            foreach (var name in names)
            {
                var candidate = Path.Combine(root, appFolder, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        if (includeUserLocations)
        {
            foreach (var root in GetUserSearchRoots())
            {
                var found = FindNamedExecutable(root, names, maxDepth: 3);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetUserSearchRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(profile, "Downloads"),
            Path.Combine(profile, "Documents")
        };
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string FindNamedExecutable(string root, string[] names, int maxDepth)
    {
        try
        {
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((root, 0));
            while (pending.Count > 0)
            {
                var (current, depth) = pending.Dequeue();
                foreach (var name in names)
                {
                    var candidate = Path.Combine(current, name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                if (depth >= maxDepth) continue;
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    try { pending.Enqueue((directory, depth + 1)); }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        return string.Empty;
    }

    private static string FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var clean = folder.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(clean)) continue;
                var candidate = Path.Combine(clean, executableName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return string.Empty;
    }

    private static bool IsValidExecutable(string? path, string[] expectedNames)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path) && expectedNames.Any(x => string.Equals(x, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }
}
