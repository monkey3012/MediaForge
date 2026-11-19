using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MediaForge.Services;

public sealed class ProcessOutputLine
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Stream { get; init; } = "OUT";
    public string Text { get; init; } = "";
}

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool Success => ExitCode == 0;
}

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        Action<string>? output = null,
        Action<string>? error = null,
        CancellationToken cancellationToken = default,
        Action<ProcessOutputLine>? liveOutput = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("An executable path is required.", nameof(executable));
        if (!File.Exists(executable))
            throw new FileNotFoundException("The command-line executable was not found.", executable);

        arguments ??= string.Empty;
        MediaForgeLogger.Info($"Starting process: {executable} {arguments}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Windows could not start '{executable}'.");
        }
        catch (Win32Exception ex)
        {
            MediaForgeLogger.Error($"Could not start process: {executable}", ex);
            throw new InvalidOperationException(
                $"Could not start '{executable}'. Windows error {ex.NativeErrorCode}: {ex.Message}", ex);
        }

        // Do not use BeginOutputReadLine/BeginErrorReadLine here. Both MakeMKV and
        // HandBrakeCLI frequently update progress with carriage returns rather than
        // newline-delimited records. Reading the raw streams lets the Live Console
        // display those progress updates just like a real CMD window.
        var stdoutTask = ReadStreamAsync(process.StandardOutput, "OUT", stdout, output, liveOutput, cancellationToken);
        var stderrTask = ReadStreamAsync(process.StandardError, "ERR", stderr, error, liveOutput, cancellationToken);

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    MediaForgeLogger.Warn($"Cancellation requested; terminating process PID {process.Id}.");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                MediaForgeLogger.Error("Unable to terminate cancelled process.", ex);
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MediaForgeLogger.Warn($"Process cancelled: {executable}");
            throw;
        }
        catch (Exception ex)
        {
            MediaForgeLogger.Error($"Process runner failure: {executable}", ex);
            throw;
        }

        var result = new ProcessResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString()
        };

        MediaForgeLogger.Info($"Process exited with code {result.ExitCode}: {executable}");
        var processLog = MediaForgeLogger.WriteProcessOutput(executable, arguments, result);
        if (!string.IsNullOrWhiteSpace(processLog))
            MediaForgeLogger.Info($"Process output saved to {processLog}");

        return result;
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        string streamName,
        StringBuilder fullOutput,
        Action<string>? simpleOutput,
        Action<ProcessOutputLine>? liveOutput,
        CancellationToken token)
    {
        var buffer = new char[2048];
        var line = new StringBuilder();

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (count == 0) break;

            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c == '\0') continue;

                if (c == '\r' || c == '\n')
                {
                    if (line.Length == 0) continue;
                    Emit(line.ToString());
                    line.Clear();
                    continue;
                }

                line.Append(c);
            }
        }

        if (line.Length > 0)
            Emit(line.ToString());

        void Emit(string value)
        {
            fullOutput.AppendLine(value);
            simpleOutput?.Invoke(value);
            liveOutput?.Invoke(new ProcessOutputLine
            {
                Timestamp = DateTime.Now,
                Stream = streamName,
                Text = value
            });
        }
    }
}
