using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Settings: command (required: program), args (optional, may contain {secret:x}), workingDir, every (default 600), timeout (default 10),
/// parse = json | text (default: json if stdout starts with { or [, else text), unixTimeFields.
/// Runs hidden (no window), captures stdout (UTF-8) and stderr. Publishes: text | json, exitCode (NumberValue), ranAt (TimeValue), stderr (TextValue when non-empty).
/// Non-zero exit does not throw (users may script that); a timeout kills the process tree and throws.</summary>
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
