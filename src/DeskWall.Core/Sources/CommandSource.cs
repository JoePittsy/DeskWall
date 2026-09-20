using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Settings: command (required: program), args (optional, may contain {secret:x}), workingDir, every (default 600), timeout (default 10),
/// parse = json | text (default: json if stdout starts with { or [, else text), unixTimeFields.
/// Runs hidden (no window), captures stdout (UTF-8) and stderr. Publishes: text | json, exitCode (NumberValue), ranAt (TimeValue), stderr (TextValue when non-empty).
/// Non-zero exit does not throw while stdout has something in it (users script that); a non-zero exit with
/// empty stdout throws, so the last good values stay published; a timeout kills the process tree and throws.
/// Caveat, documented in layouts/README.md: stderr is published verbatim, so a command that fails and echoes
/// its own argument list can put a substituted {secret:} into a value a component could draw.</summary>
public sealed class CommandSource(string name, TimeSpan every, TimeSpan timeout, string command, string? args, string? workingDir, string? parse,
    IReadOnlySet<string> unixTimeFields, Secrets secrets, IClock clock) : AsyncSource(name, every, timeout)
{
    public static CommandSource FromDef(SourceDef def, IClock clock, Secrets secrets)
    {
        var s = def.Settings;
        if (!s.TryGetValue("command", out var cmd) || string.IsNullOrWhiteSpace(cmd)) throw new ArgumentException($"command source '{def.Name}' needs settings.command");
        var timeout = TimeSpan.FromSeconds(s.TryGetValue("timeout", out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) ? ts : 10);
        var unix = new HashSet<string>((s.TryGetValue("unixTimeFields", out var u) ? u : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        return new CommandSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 600), timeout, cmd, s.GetValueOrDefault("args"), s.GetValueOrDefault("workingDir"), s.GetValueOrDefault("parse"), unix, secrets, clock);
    }

    protected override async Task<RecordValue> FetchAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(command),
            Arguments = args is null ? "" : secrets.Substitute(args),
            WorkingDirectory = workingDir is null ? "" : Environment.ExpandEnvironmentVariables(workingDir),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {command}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var kill = new CancellationTokenSource(Timeout + TimeSpan.FromSeconds(1));   // hard stop slightly after the tick gives up
        try { await p.WaitForExitAsync(kill.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"command '{command}' exceeded {Timeout.TotalSeconds:0} s and was killed");
        }
        var outText = await stdout; var errText = await stderr;
        // Spec 3.2 again: a non-zero exit that printed nothing has no values to publish, and
        // publishing text = "" over the last good text is the same partial-record-over-last-good the
        // spec decides against. A non-zero exit that DID print is still a success: scripts use the
        // exit code as a flag. The message carries the command, never the substituted args.
        if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(outText))
            throw new InvalidOperationException($"command '{command}' exited {p.ExitCode} with no output");
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["exitCode"] = new NumberValue(p.ExitCode),
            ["ranAt"] = new TimeValue(clock.Now),
        };
        var trimmed = outText.TrimStart();
        var mode = parse ?? (trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "json" : "text");
        if (mode == "json") d["json"] = JsonValues.Parse(outText, unixTimeFields);
        else d["text"] = new TextValue(outText);
        if (!string.IsNullOrWhiteSpace(errText)) d["stderr"] = new TextValue(errText);
        return new RecordValue(d);
    }
}
