using System.IO;
using MediaForge.Models;

namespace MediaForge.Services;

public sealed class JobService
{
    private readonly List<MediaJob> _jobs = new();

    public IReadOnlyList<MediaJob> GetJobs() => _jobs;

    public MediaJob Add(string sourcePath)
    {
        var job = new MediaJob
        {
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourcePath = sourcePath,
            SourceName = Path.GetFileName(sourcePath),
            Stage = "Queued"
        };
        _jobs.Add(job);
        return job;
    }
}
