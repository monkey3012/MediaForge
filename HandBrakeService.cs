using System.IO;
using System.Text.RegularExpressions;

namespace MediaForge.Services;

public sealed class HandBrakeProgressEventArgs : EventArgs
{
    public int Percent { get; init; }
    public string Message { get; init; } = "";
}

public sealed class HandBrakeService
{
    readonly ProcessRunner runner;
    static readonly Regex PercentRegex = new(@"(?<!\d)(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    public HandBrakeService(ProcessRunner r) => runner = r;

    public static string? FindPresetFile()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HandBrake",
            "presets.json");
        return File.Exists(path) ? path : null;
    }

    public async Task<ProcessResult> EncodeAsync(
        string cli,
        string input,
        string output,
        string preset,
        string? presetFile = null,
        Action<HandBrakeProgressEventArgs>? progress = null,
        CancellationToken token = default,
        Action<ProcessOutputLine>? liveOutput = null, int? chapterStart = null, int? chapterEnd = null)
    {
        if (!File.Exists(cli))
            throw new FileNotFoundException("HandBrakeCLI was not found.", cli);
        if (!File.Exists(input))
            throw new FileNotFoundException("Source file was not found.", input);
        if (string.IsNullOrWhiteSpace(preset))
            throw new InvalidOperationException("No HandBrake preset has been selected.");

        var outputDirectory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("The output folder is invalid.");
        Directory.CreateDirectory(outputDirectory);

        var lastPercent = -1;
        void Handle(string line)
        {
            var match = PercentRegex.Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, out var value))
            {
                var percent = Math.Clamp((int)Math.Round(value), 0, 100);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Invoke(new HandBrakeProgressEventArgs { Percent = percent, Message = line });
                }
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                progress?.Invoke(new HandBrakeProgressEventArgs { Percent = Math.Max(0, lastPercent), Message = line });
            }
        }

        // HandBrakeCLI is a separate process and does not inherit the GUI's
        // loaded custom presets. Import the user's GUI preset file for every
        // encode so custom presets work reliably.
        var args = BuildArguments(input, output, preset, presetFile, chapterStart, chapterEnd);
        return await runner.RunAsync(cli, args, Handle, Handle, token, liveOutput);
    }

    public string BuildArguments(string input, string output, string preset, string? presetFile = null, int? chapterStart = null, int? chapterEnd = null)
    {
        var import = string.IsNullOrWhiteSpace(presetFile) ? FindPresetFile() : presetFile;
        var importArg = !string.IsNullOrWhiteSpace(import) && File.Exists(import)
            ? $"--preset-import-file {Quote(import)} "
            : "";
        var chapters = chapterStart is > 0 && chapterEnd is > 0 ? $" --chapters {chapterStart}-{Math.Max(chapterStart.Value, chapterEnd.Value)}" : "";
        return $"{importArg}--preset {Quote(preset)} -i {Quote(input)} -o {Quote(output)} --format av_mp4{chapters}";
    }

    public async Task<IReadOnlyList<string>> ListPresetsAsync(string cli, CancellationToken token = default)
    {
        if (!File.Exists(cli))
            throw new FileNotFoundException("HandBrakeCLI was not found.", cli);

        var presetFile = FindPresetFile();
        var importArg = !string.IsNullOrWhiteSpace(presetFile)
            ? $"--preset-import-file {Quote(presetFile)} "
            : "";

        var q = await runner.RunAsync(cli, $"{importArg}--preset-list", cancellationToken: token);
        if (!q.Success)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(q.StdErr) ? "HandBrakeCLI could not list presets." : q.StdErr.Trim());

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Get the CLI's built-in presets.
        foreach (var raw in q.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Presets", StringComparison.OrdinalIgnoreCase))
                continue;

            // HandBrake has used a few different display formats over time.
            // Keep lines that look like actual preset entries and ignore
            // obvious headings/separators.
            if (line.StartsWith("<") && line.EndsWith(">"))
                continue;
            if (line.StartsWith("- ") || line.StartsWith("+ "))
                line = line[2..].Trim();
            if (line.StartsWith("-"))
                continue;
            if (line.Contains("-----"))
                continue;
            if (!string.IsNullOrWhiteSpace(line))
                names.Add(line);
        }

        // The GUI preset file is the authoritative source for custom presets.
        // Reading it directly also means the dropdown works even when the CLI
        // output format changes between HandBrake releases.
        if (!string.IsNullOrWhiteSpace(presetFile) && File.Exists(presetFile))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(presetFile));
                foreach (var element in document.RootElement.EnumerateObject().SelectMany(x => EnumerateObjects(x.Value)))
                {
                    if (element.TryGetProperty("PresetName", out var presetName) &&
                        presetName.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var name = presetName.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            names.Add(name);
                    }
                }
            }
            catch
            {
                // A malformed GUI preset file should not prevent built-in
                // presets from appearing in the dropdown.
            }
        }

        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<System.Text.Json.JsonElement> EnumerateObjects(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
                foreach (var child in EnumerateObjects(property.Value))
                    yield return child;
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                foreach (var item in EnumerateObjects(child))
                    yield return item;
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
