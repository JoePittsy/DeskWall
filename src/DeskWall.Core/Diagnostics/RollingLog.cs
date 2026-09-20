using System.Globalization;
using System.Text;

namespace DeskWall.Core.Diagnostics;

/// <summary>Append-only text log in the runtime dir: deskwall.log, rolled to deskwall.1.log when it
/// passes MaxBytes. One line per entry: "yyyy-MM-dd HH:mm:ss.fff [LEVEL] message". Thread-safe.</summary>
public sealed class RollingLog(string path, long maxBytes = 1_000_000)
{
    private readonly object _lock = new();
    public string? LastError { get; private set; }

    public static RollingLog Default() => new(Paths.InRuntime("deskwall.log"));

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null)
    {
        var text = ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}";
        LastError = text;
        var first = ex?.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
        Write("ERROR", first is null ? text : $"{text} | {first}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length + line.Length > maxBytes)
                    File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException) { /* logging must never take the daemon down */ }
        }
    }
}
