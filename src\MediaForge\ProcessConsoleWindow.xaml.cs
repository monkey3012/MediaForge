using System.Text;
using System.Windows;
using MediaForge.Services;

namespace MediaForge;

public partial class ProcessConsoleWindow : Window
{
    private readonly StringBuilder _buffer = new();

    public ProcessConsoleWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public void StartProcess(string executable, string arguments)
    {
        ProcessTitle.Text = $"Running: {System.IO.Path.GetFileName(executable)}";
        CommandText.Text = $"{executable} {arguments}";
        StatusText.Text = "Running...";
        Append(new ProcessOutputLine { Timestamp = DateTime.Now, Stream = "SYS", Text = $"$ {executable} {arguments}" });
    }

    public void Append(ProcessOutputLine line)
    {
        Dispatcher.Invoke(() =>
        {
            var prefix = line.Stream == "ERR" ? "ERR" : line.Stream == "SYS" ? "SYS" : "OUT";
            _buffer.Append('[').Append(line.Timestamp.ToString("HH:mm:ss")).Append("] ")
                .Append(prefix).Append(" | ").AppendLine(line.Text);
            ConsoleText.Text = _buffer.ToString();
            ConsoleText.ScrollToEnd();
        });
    }

    public void SetFinished(int exitCode, bool cancelled)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = cancelled ? "Cancelled" : $"Exited with code {exitCode}";
            ProcessTitle.Text = cancelled ? "Process cancelled" : "Process finished";
        });
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _buffer.Clear();
        ConsoleText.Clear();
    }
}
