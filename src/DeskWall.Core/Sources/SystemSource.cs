using System.Diagnostics;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;
using Microsoft.Win32;

namespace DeskWall.Core.Sources;

/// <summary>every default 900. Publishes: uptime (NumberValue seconds), uptimeText (TextValue "3d 4h"), bootedAt (TimeValue),
/// daysSinceCrash (NumberValue; days since the newest System-log event id 41 (Kernel-Power) or 1001 (BugCheck); -1 if none in the log),
/// lastCrashAt (TimeValue, omitted if none), pendingReboot (BoolValue: any of the registry markers below exists),
/// machine (TextValue), user (TextValue).</summary>
public sealed class SystemSource(string name, TimeSpan every, IClock clock, Func<DateTimeOffset?>? crashProbe = null, Func<bool>? rebootProbe = null)
    : PeriodicSource(name, every)
{
    public static SystemSource FromDef(SourceDef def, IClock clock) => new(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 900), clock);

    public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["uptime"] = new NumberValue(Math.Round(uptime.TotalSeconds)),
            ["uptimeText"] = new TextValue(uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}d {uptime.Hours}h" : $"{(int)uptime.TotalHours}h {uptime.Minutes}m"),
            ["bootedAt"] = new TimeValue(now - uptime),
            ["machine"] = new TextValue(Environment.MachineName),
            ["user"] = new TextValue(Environment.UserName),
            ["pendingReboot"] = new BoolValue((rebootProbe ?? RebootPending)()),
        };
        var crash = (crashProbe ?? CachedCrashEvent)();
        d["daysSinceCrash"] = new NumberValue(crash is null ? -1 : Math.Floor((now - crash.Value).TotalDays));
        if (crash is not null) d["lastCrashAt"] = new TimeValue(crash.Value);
        return new(new RecordValue(d));
    }

    /// <summary>The crash time for this boot, scanned once and then remembered.
    /// <para>
    /// SystemSource is a PeriodicSource, so RefreshAsync runs inline on the tick thread - and in the
    /// daemon the tick thread is the message pump, so a multi-second scan blocks the tray, the
    /// display-change wake and the layout watcher with it. The answer cannot change while the machine
    /// is up (the next crash is by definition the next boot), so the 2000-entry walk of the System
    /// .evtx is paid once per process instead of every 15 minutes.
    /// </para>
    /// <para>Single-threaded by construction: only a tick calls it, and ticks do not overlap.</para></summary>
    public static DateTimeOffset? CachedCrashEvent()
    {
        if (s_crashProbed) return s_crash;
        s_crash = NewestCrashEvent();
        s_crashProbed = true;
        return s_crash;
    }

    private static DateTimeOffset? s_crash;
    private static bool s_crashProbed;

    /// <summary>Newest System-log entry with InstanceId 41 (Kernel-Power) or 1001 (BugCheck),
    /// scanning at most the last 2000 entries. Null if none found or the log is unavailable.
    /// Seconds, not milliseconds, on a busy machine: call <see cref="CachedCrashEvent"/> instead.</summary>
    public static DateTimeOffset? NewestCrashEvent()
    {
        try
        {
            using var log = new EventLog("System");
            var entries = log.Entries;
            var count = entries.Count;
            for (var i = count - 1; i >= Math.Max(0, count - 2000); i--)
            {
                var e = entries[i];
                if (e.InstanceId is 41 or 1001 && e.EntryType is EventLogEntryType.Error or EventLogEntryType.Warning)
                    return new DateTimeOffset(e.TimeGenerated);
            }
            return null;
        }
        catch (Exception) { return null; }   // no access, or the log is unavailable: report "no crash known"
    }

    /// <summary>True if any of the standard Windows reboot-pending registry markers exists.</summary>
    public static bool RebootPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null) return true;
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu is not null) return true;
            using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch (Exception) { return false; }
    }
}
