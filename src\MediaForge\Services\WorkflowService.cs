using System.IO;
using MediaForge.Models;

namespace MediaForge.Services;

public sealed class WorkflowService
{
    readonly JobDatabase db;
    readonly HandBrakeService hb;
    readonly PlexNamingService plex;

    public WorkflowService(JobDatabase d, HandBrakeService h, PlexNamingService p)
    {
        db = d; hb = h; plex = p;
    }

    public async Task EncodeExistingAsync(
        MediaJob j,
        AppSettings s,
        Action<MediaJob>? changed = null,
        CancellationToken token = default,
        Action<ProcessOutputLine>? liveOutput = null,
        Action<string, string>? processStarted = null)
    {
        if (string.IsNullOrWhiteSpace(s.HandBrakeCliPath) || string.IsNullOrWhiteSpace(s.HandBrakePresetName))
            throw new InvalidOperationException("Configure HandBrakeCLI and an existing HandBrake preset first.");

        // SourceName is an editable display/metadata field. It must never be used
        // as the path HandBrake reads. Always prefer the persisted SourcePath.
        // If an older job has a stale/missing SourcePath, try to recover the MKV
        // from the same folder using the current display name before failing.
        var sourcePath = ResolveSourcePath(j);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                $"Source MKV was not found. Renaming the displayed File Name does not rename the source file. Expected: {j.SourcePath}",
                j.SourcePath);

        // Keep the actual source path authoritative. Do not overwrite it with
        // SourceName, even when SourceName was manually edited.
        if (!string.Equals(j.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            j.SourcePath = sourcePath;
            db.Upsert(j);
            changed?.Invoke(j);
        }

        try { j.RippedSizeBytes ??= new FileInfo(sourcePath).Length; } catch { }
        Directory.CreateDirectory(s.TempPath);
        var hasFinalDestination = TryGetFinalDestination(j, s, out var finalPath);
        if (!hasFinalDestination && HasPartialIdentification(j))
            throw new InvalidOperationException("Media identification is incomplete. For TV, enter Show, Year, Season, Episode, and Episode Title before processing.");
        var outp = hasFinalDestination
            ? finalPath
            : Path.Combine(s.TempPath, $"job-{j.Id}.mp4");
        j.Status = JobStatus.Encoding;
        j.Stage = "Encoding";
        j.Progress = 0;
        j.ErrorMessage = null;
        db.Upsert(j); changed?.Invoke(j);

        try
        {
            var commandArguments = hb.BuildArguments(sourcePath, outp, s.HandBrakePresetName, HandBrakeService.FindPresetFile(), j.ChapterStart, j.ChapterEnd);
            processStarted?.Invoke(s.HandBrakeCliPath, commandArguments);

            var result = await hb.EncodeAsync(s.HandBrakeCliPath, sourcePath, outp, s.HandBrakePresetName, HandBrakeService.FindPresetFile(), e =>
            {
                j.Progress = e.Percent;
                j.Stage = e.Percent > 0 ? $"Encoding {e.Percent}%" : "Encoding";
                db.Upsert(j);
                changed?.Invoke(j);
            }, token, liveOutput, j.ChapterStart, j.ChapterEnd);

            if (!result.Success)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StdErr)
                    ? $"HandBrakeCLI failed with exit code {result.ExitCode}."
                    : result.StdErr.Trim());

            if (!File.Exists(outp)) throw new FileNotFoundException("HandBrake completed but no MP4 output was created.", outp);

            j.Progress = 100;
            j.OutputPath = outp;
            try { j.EncodedSizeBytes = new FileInfo(outp).Length; } catch { j.EncodedSizeBytes = null; }
            if (hasFinalDestination)
            {
                j.Status = JobStatus.Completed;
                j.Stage = "Completed - saved to final library";
                if (s.DeleteTempAfterSuccess && !string.IsNullOrWhiteSpace(j.SourcePath) && File.Exists(j.SourcePath))
                {
                    try { File.Delete(j.SourcePath); } catch { }
                }
            }
            else
            {
                j.Status = JobStatus.Encoded;
                j.Stage = "Encoded - identification required for final organization";
            }
            db.Upsert(j); changed?.Invoke(j);
        }
        catch (OperationCanceledException)
        {
            j.Status = JobStatus.Cancelled;
            j.Stage = "Cancelled by user";
            j.ErrorMessage = "Encoding was cancelled by the user.";
            db.Upsert(j); changed?.Invoke(j);
            throw;
        }
        catch (Exception ex)
        {
            j.Status = JobStatus.Failed;
            j.Stage = "Encoding failed";
            j.ErrorMessage = ex.Message;
            db.Upsert(j); changed?.Invoke(j);
            throw;
        }
    }

    private static string ResolveSourcePath(MediaJob j)
    {
        if (!string.IsNullOrWhiteSpace(j.SourcePath) && File.Exists(j.SourcePath))
            return j.SourcePath;

        if (string.IsNullOrWhiteSpace(j.SourcePath))
            return j.SourcePath;

        var directory = Path.GetDirectoryName(j.SourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return j.SourcePath;

        // Recovery is deliberately limited to the same directory so a display
        // rename cannot make MediaForge encode an unrelated file elsewhere.
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(j.SourceName))
        {
            var name = j.SourceName.Trim();
            candidates.Add(Path.Combine(directory, name));
            if (!Path.HasExtension(name))
                candidates.Add(Path.Combine(directory, name + ".mkv"));
        }

        // If SourceName is stale, look for the only MKV in the original folder.
        // This is safe for imported jobs when the folder contains one source.
        if (candidates.Count == 0 || candidates.All(x => !File.Exists(x)))
        {
            var mkvs = Directory.GetFiles(directory, "*.mkv", SearchOption.TopDirectoryOnly);
            if (mkvs.Length == 1)
                candidates.Add(mkvs[0]);
        }

        return candidates.FirstOrDefault(File.Exists) ?? j.SourcePath;
    }

    private static bool HasPartialIdentification(MediaJob j)
    {
        var any = !string.IsNullOrWhiteSpace(j.Title) || j.Year != null || j.Season != null || j.Episode != null || !string.IsNullOrWhiteSpace(j.EpisodeTitle) || !string.IsNullOrWhiteSpace(j.MediaType);
        return any && (!string.IsNullOrWhiteSpace(j.MediaType) || !string.IsNullOrWhiteSpace(j.Title));
    }

    private bool TryGetFinalDestination(MediaJob j, AppSettings s, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(j.Title) || j.Year == null) return false;
        if (string.Equals(j.MediaType, "TV Show", StringComparison.OrdinalIgnoreCase))
        {
            if (j.Season == null || j.Episode == null || string.IsNullOrWhiteSpace(j.EpisodeTitle)) return false;
        }
        try
        {
            path = Destination(j, s);
            return true;
        }
        catch
        {
            path = string.Empty;
            return false;
        }
    }

    public string Destination(MediaJob j, AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(j.Title) || j.Year == null) throw new InvalidOperationException("Title and year required.");
        if (j.MediaType == "TV Show")
        {
            if (j.Season == null || j.Episode == null || string.IsNullOrWhiteSpace(j.EpisodeTitle))
                throw new InvalidOperationException("Season, episode and episode title required.");
            var d = Path.Combine(s.TvShowsPath, plex.TvFolder(j.Title, j.Year.Value), plex.SeasonFolder(j.Season.Value));
            Directory.CreateDirectory(d);
            return plex.UniquePath(Path.Combine(d, plex.EpisodeFile(j.Title, j.Year.Value, j.Season.Value, j.Episode.Value, j.EpisodeTitle)));
        }
        var md = Path.Combine(s.MoviesPath, plex.MovieFolder(j.Title, j.Year.Value));
        Directory.CreateDirectory(md);
        return plex.UniquePath(Path.Combine(md, plex.MovieFile(j.Title, j.Year.Value)));
    }

    public void Organize(MediaJob j, AppSettings s, Action<MediaJob>? changed = null)
    {
        if (string.IsNullOrWhiteSpace(j.OutputPath) || !File.Exists(j.OutputPath)) throw new FileNotFoundException("Encoded output not found.");
        j.Status = JobStatus.Organizing; j.Stage = "Organizing"; j.Progress = 0; db.Upsert(j); changed?.Invoke(j);
        var dest = Destination(j, s);
        File.Move(j.OutputPath, dest);
        j.OutputPath = dest;
        j.Status = JobStatus.Completed; j.Stage = "Completed"; j.Progress = 100;

        if (s.DeleteTempAfterSuccess && !string.IsNullOrWhiteSpace(j.SourcePath) && File.Exists(j.SourcePath))
        {
            try { File.Delete(j.SourcePath); } catch { /* cleanup failure should not invalidate a completed library move */ }
        }
        db.Upsert(j); changed?.Invoke(j);
    }
}
