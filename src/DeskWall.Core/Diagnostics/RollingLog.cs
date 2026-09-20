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

    /// <summary>Never throws: this is called from inside the tick's own catch handler, so an exception
    /// raised here would replace the one being handled and take the daemon down (finding 2).</summary>
    public void Error(string message, Exception? ex = null)
    {
        string text = message, detail = message;
        try
        {
            text = ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}";
            var first = ex?.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            detail = first is null ? text : $"{text} | {first}";
        }
        catch (Exception) { /* a hostile Message or StackTrace is still not worth a dead daemon */ }
        Write("ERROR", detail, remember: text);
    }

    /// <param name="remember">What to publish as LastError (the tray tooltip reads it), set under the
    /// same lock as the write instead of beside it.</param>
    private void Write(string level, string message, string? remember = null)
    {
        lock (_lock)
        {
            if (remember is not null) LastError = remember;
            try
            {
                var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length + line.Length > maxBytes)
                    File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            // Every exception, not only IOException: a read-only or ACL-denied runtime dir raises
            // UnauthorizedAccessException, and an invalid path raises ArgumentException.
            catch (Exception) { /* logging must never take the daemon down */ }
        }
    }
}
