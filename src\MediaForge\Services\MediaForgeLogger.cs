using System.Text;

namespace MediaForge.Services;

public static class MediaForgeLogger
{
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaForge", "Logs");

    public static string LogDirectory => DirectoryPath;
    public static string MainLogPath => Path.Combine(DirectoryPath, "MediaForge.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    public static string WriteProcessOutput(string executable, string arguments, ProcessResult result)
    {
        var safeName = $"process-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log";
        var path = Path.Combine(DirectoryPath, safeName);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var sb = new StringBuilder();
            sb.AppendLine($"Executable: {executable}");
            sb.AppendLine($"Arguments: {arguments}");
            sb.AppendLine($"Exit code: {result.ExitCode}");
            sb.AppendLine("--- STDOUT ---");
            sb.AppendLine(result.StdOut);
            sb.AppendLine("--- STDERR ---");
            sb.AppendLine(result.StdErr);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch (Exception ex)
        {
            Error("Could not write process log.", ex);
            return string.Empty;
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(MainLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { }
    }
}
