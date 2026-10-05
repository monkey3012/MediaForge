using System.IO;
using System.Text.RegularExpressions;
using MediaForge.Models;

namespace MediaForge.Services;

public sealed class MakeMkvService
{
    private readonly ProcessRunner _runner;

    public MakeMkvService(ProcessRunner runner) => _runner = runner;

    public async Task<IReadOnlyList<MakeMkvDrive>> GetOpticalDrivesAsync(
        string makemkvconPath,
        CancellationToken token = default,
        Action<ProcessOutputLine>? liveOutput = null)
    {
        ValidateCliPath(makemkvconPath);
        MediaForgeLogger.Info($"Enumerating MakeMKV drives with {makemkvconPath}");

        // MakeMKV's robot-mode drive enumeration is authoritative. 9999 returns
        // the DRV records without attempting to open a disc.
        var result = await _runner.RunAsync(
            makemkvconPath,
            "-r info disc:9999",
            cancellationToken: token,
            liveOutput: liveOutput);

        var drives = ParseDrives(result.StdOut);

        if (drives.Count == 0)
        {
            var fallback = await _runner.RunAsync(
                makemkvconPath,
                "-r info",
                cancellationToken: token,
                liveOutput: liveOutput);
            drives = ParseDrives(fallback.StdOut);
        }

        if (drives.Count == 0)
        {
            var detail = ExtractMakeMkvMessages(result.StdOut);
            if (string.IsNullOrWhiteSpace(detail))
                detail = !string.IsNullOrWhiteSpace(result.StdErr)
                    ? CleanMakeMkvError(result.StdErr)
                    : "MakeMKV did not report any optical drives.";
            throw new InvalidOperationException(detail);
        }

        return drives.OrderBy(x => x.Index).ToList();
    }

    public async Task<IReadOnlyList<MakeMkvTitle>> ScanDiscAsync(
        string makemkvconPath,
        MakeMkvDrive drive,
        CancellationToken token = default,
        Action<ProcessOutputLine>? liveOutput = null)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ValidateCliPath(makemkvconPath);

        var source = GetDeviceSource(drive);
        MediaForgeLogger.Info($"Scanning disc with source {source}; drive={drive.DisplayName}");
        var result = await _runner.RunAsync(
            makemkvconPath,
            $"-r info {source}",
            cancellationToken: token,
            liveOutput: liveOutput);

        var titles = ParseTitles(result.StdOut);

        if (titles.Count == 0)
        {
            var detail = ExtractMakeMkvMessages(result.StdOut);
            if (string.IsNullOrWhiteSpace(detail))
                detail = !string.IsNullOrWhiteSpace(result.StdErr)
                    ? CleanMakeMkvError(result.StdErr)
                    : $"MakeMKV did not return any titles for {source}.";
            throw new InvalidOperationException(detail);
        }

        return titles;
    }

    // Kept for compatibility with older callers. Prefer the MakeMkvDrive overload
    // because MakeMKV 1.18.x reliably opens physical optical media using dev:E:.
    public async Task<IReadOnlyList<MakeMkvTitle>> ScanDiscAsync(
        string makemkvconPath,
        int driveIndex,
        CancellationToken token = default,
        Action<ProcessOutputLine>? liveOutput = null)
    {
        ValidateCliPath(makemkvconPath);
        var drive = new MakeMkvDrive { Index = driveIndex };
        var drives = await GetOpticalDrivesAsync(makemkvconPath, token, liveOutput);
        var selected = drives.FirstOrDefault(x => x.Index == driveIndex) ?? drive;
        return await ScanDiscAsync(makemkvconPath, selected, token, liveOutput);
    }

    public async Task<int> RipAsync(
        string makemkvconPath,
        MakeMkvDrive drive,
        int title,
        string outputFolder,
        Action<string>? log = null,
        CancellationToken token = default,
        Action<ProcessOutputLine>? liveOutput = null)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ValidateCliPath(makemkvconPath);

        if (title < 0)
            throw new ArgumentOutOfRangeException(nameof(title));
        if (string.IsNullOrWhiteSpace(outputFolder))
            throw new ArgumentException("An output folder is required.", nameof(outputFolder));

        Directory.CreateDirectory(outputFolder);
        var source = GetDeviceSource(drive);
        MediaForgeLogger.Info($"Ripping title {title} from {source} to {outputFolder}");
        var result = await _runner.RunAsync(
            makemkvconPath,
            $"-r mkv {source} {title} \"{outputFolder}\"",
            log,
            log,
            token,
            liveOutput);

        if (!result.Success)
        {
            var detail = ExtractMakeMkvMessages(result.StdOut);
            if (string.IsNullOrWhiteSpace(detail)) detail = result.StdErr;
            log?.Invoke(string.IsNullOrWhiteSpace(detail)
                ? $"MakeMKV exited with code {result.ExitCode}."
                : detail);
        }

        return result.ExitCode;
    }

    // Compatibility overload for existing code.
    public async Task<int> RipAsync(
        string makemkvconPath,
        int driveIndex,
        int title,
        string outputFolder,
        Action<string>? log = null,
        CancellationToken token = default,
        Action<ProcessOutputLine>? liveOutput = null)
    {
        var drives = await GetOpticalDrivesAsync(makemkvconPath, token, liveOutput);
        var drive = drives.FirstOrDefault(x => x.Index == driveIndex)
            ?? throw new InvalidOperationException($"MakeMKV drive index {driveIndex} is no longer available.");
        return await RipAsync(makemkvconPath, drive, title, outputFolder, log, token, liveOutput);
    }

    public static IReadOnlyList<MakeMkvDrive> ParseDrives(string? stdout)
    {
        var drives = new List<MakeMkvDrive>();
        if (string.IsNullOrWhiteSpace(stdout)) return drives;

        foreach (var raw in Lines(stdout))
        {
            if (!raw.StartsWith("DRV:", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var parts = SplitCsvLike(raw[4..]);
                if (parts.Count < 7 || !int.TryParse(parts[0], out var index)) continue;

                // In MakeMKV robot output 256 means an unused/hidden slot.
                if (string.Equals(parts[1], "256", StringComparison.OrdinalIgnoreCase)) continue;

                drives.Add(new MakeMkvDrive
                {
                    Index = index,
                    IsVisible = true,
                    IsEnabled = !string.Equals(parts[2], "0", StringComparison.OrdinalIgnoreCase),
                    DriveName = Unquote(parts[4]),
                    DiscName = Unquote(parts[5]),
                    DriveLetter = ExtractWindowsDriveLetter(Unquote(parts[6]))
                });
            }
            catch { /* Ignore malformed DRV records. */ }
        }

        return drives.GroupBy(x => x.Index).Select(x => x.First()).OrderBy(x => x.Index).ToList();
    }

    public static IReadOnlyList<MakeMkvTitle> ParseTitles(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return Array.Empty<MakeMkvTitle>();

        var data = new Dictionary<int, Dictionary<int, string>>();
        var streams = new Dictionary<int, Dictionary<int, int>>();

        foreach (var raw in Lines(stdout))
        {
            try
            {
                if (raw.StartsWith("TINFO:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = SplitCsvLike(raw[6..]);
                    if (parts.Count < 4 || !int.TryParse(parts[0], out var titleIndex) || !int.TryParse(parts[1], out var attr)) continue;
                    if (!data.TryGetValue(titleIndex, out var attrs)) data[titleIndex] = attrs = new();
                    attrs[attr] = Unquote(parts[3]);
                }
                else if (raw.StartsWith("SINFO:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = SplitCsvLike(raw[6..]);
                    if (parts.Count < 5 || !int.TryParse(parts[0], out var titleIndex) || !int.TryParse(parts[1], out var streamIndex)) continue;
                    if (int.TryParse(parts[2], out var attribute) && attribute == 1)
                    {
                        var type = Unquote(parts[4]);
                        if (!streams.TryGetValue(titleIndex, out var titleStreams)) streams[titleIndex] = titleStreams = new();
                        if (type.Equals("Video", StringComparison.OrdinalIgnoreCase)) titleStreams[streamIndex] = 1;
                        else if (type.Equals("Audio", StringComparison.OrdinalIgnoreCase)) titleStreams[streamIndex] = 2;
                    }
                }
            }
            catch { /* Ignore malformed robot records. */ }
        }

        var titles = new List<MakeMkvTitle>();
        foreach (var pair in data.OrderBy(x => x.Key))
        {
            var a = pair.Value;
            var title = new MakeMkvTitle { Index = pair.Key };
            if (a.TryGetValue(2, out var name)) title.TitleName = name;
            if (a.TryGetValue(9, out var duration) && TimeSpan.TryParse(duration, out var parsedDuration)) title.Duration = parsedDuration;
            // MakeMKV 1.18.4: attribute 10 is display size, 11 is bytes.
            if (a.TryGetValue(11, out var bytes) && long.TryParse(bytes, out var sizeBytes)) title.SizeBytes = sizeBytes;
            // MakeMKV 1.18.4: attribute 8 is chapter count.
            if (a.TryGetValue(8, out var chapters) && int.TryParse(chapters, out var chapterCount)) title.Chapters = chapterCount;
            if (a.TryGetValue(16, out var sourceFile)) title.SourceFile = sourceFile;

            if (streams.TryGetValue(pair.Key, out var streamTypes))
            {
                title.VideoTracks = streamTypes.Values.Count(x => x == 1);
                title.AudioTracks = streamTypes.Values.Count(x => x == 2);
            }

            title.MainScore = Score(title);
            titles.Add(title);
        }

        return titles.OrderByDescending(x => x.MainScore).ThenByDescending(x => x.Duration).ThenByDescending(x => x.SizeBytes).ToList();
    }

    private static int Score(MakeMkvTitle title)
    {
        var score = 0;
        if (title.Duration >= TimeSpan.FromMinutes(45)) score += 30;
        if (title.Duration >= TimeSpan.FromMinutes(80)) score += 20;
        if (title.Duration >= TimeSpan.FromMinutes(100)) score += 10;
        if (title.SizeBytes >= 5_000_000_000) score += 20;
        if (title.SizeBytes >= 15_000_000_000) score += 10;
        if (title.Chapters >= 8) score += 10;
        if (title.Chapters >= 20) score += 5;
        if (title.VideoTracks > 0) score += 5;
        return Math.Min(score, 100);
    }

    private static string GetDeviceSource(MakeMkvDrive drive)
    {
        if (!string.IsNullOrWhiteSpace(drive.DriveLetter))
            return $"dev:{drive.DriveLetter.Trim().TrimEnd('\\')}";
        return $"disc:{drive.Index}";
    }

    private static IEnumerable<string> Lines(string text) => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());

    private static List<string> SplitCsvLike(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in input)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }

    private static string Unquote(string value) => value.Trim().Trim('"');

    private static string ExtractWindowsDriveLetter(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"(?i)([A-Z]):(?:\\)?$");
        return match.Success ? match.Groups[1].Value + ":" : (value ?? string.Empty).Trim();
    }

    private static void ValidateCliPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("MakeMKV CLI is not configured. Open Settings and select makemkvcon64.exe.");
        if (!File.Exists(path)) throw new FileNotFoundException("The configured MakeMKV CLI executable could not be found.", path);
    }

    private static string ExtractMakeMkvMessages(string output)
    {
        var messages = Lines(output)
            .Where(x => x.StartsWith("MSG:", StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Contains("Failed", StringComparison.OrdinalIgnoreCase) || x.Contains("error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return messages.Count == 0 ? string.Empty : string.Join(Environment.NewLine, messages);
    }

    private static string CleanMakeMkvError(string value)
    {
        var cleaned = value.Replace("\0", string.Empty).Trim();
        return cleaned.Length > 3000 ? cleaned[^3000..] : cleaned;
    }
}
