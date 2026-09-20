using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Resolve;

namespace DeskWall.Core.Shortcuts;

/// <summary>What one <see cref="ShortcutManager.Reconcile"/> did. Warnings are non-fatal: a single bad
/// slot never stops the others.</summary>
public sealed record ShortcutOutcome(int Written, int Positioned, int Removed, IReadOnlyList<string> Warnings);

/// <summary>Makes the desktop match the rendered layout: one transparent .lnk per shortcut slot, placed
/// so the shell's arrow overlay sits <c>pad</c> px from the cover's bottom-left corner.
/// <para>
/// Slot files are named with non-breaking spaces (<see cref="ShortcutPlan.SlotFileName"/>) so no label
/// draws. The set of slots this manager has written is remembered in <c>shortcuts-owned.json</c> in the
/// runtime dir, and only those are ever deleted: the v0 PowerShell proof of concept owns slots 0..3 on
/// the same desktop until it is retired, and a blanket "delete every slot file" would fight it.
/// </para></summary>
public sealed class ShortcutManager(Calibration calibration, int pad = ShortcutPlan.DefaultPad, Func<string>? desktopDir = null)
{
    /// <summary>Position, read back, re-position: three rounds is enough for the shell to catch up.</summary>
    private const int PositionRounds = 3;
    private const int RetryDelayMs = 150;
    /// <summary>Explorer notices a new desktop file asynchronously; SelectAndPositionItems silently does
    /// nothing for an item the view has not enumerated yet. Only paid when a file was actually written.</summary>
    private const int SettleMs = 900;
    private const int MaxSlot = 63;

    private readonly Func<string> _desktopDir = desktopDir ?? ShortcutFiles.DesktopDir;

    /// <summary>The icon size seen at the last Reconcile. Seeds the fingerprint's arrow lookup so that
    /// <see cref="Fingerprint"/> stays pure (it must not touch the shell: it runs every tick).</summary>
    private int _iconSize = 48;

    private static string OwnedFile => Paths.InRuntime("shortcuts-owned.json");

    /// <summary>Make the desktop match: write/update slot files, delete the slots we own that are no
    /// longer in the list, position all of them and verify with GetPosition. Never throws for a single
    /// bad slot; collects Warnings. Throws only when the desktop view is unavailable.</summary>
    public ShortcutOutcome Reconcile(IReadOnlyList<ResolvedShortcut> shortcuts, int scalePercent)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        var warnings = new List<string>();
        if (!DesktopView.IsAvailable())
            throw new InvalidOperationException("the desktop folder view is unavailable (Explorer not running, or desktop icons are hidden)");

        DesktopFlags.EnsurePlacementAllowed();
        _iconSize = DesktopView.IconSize();
        var arrow = calibration.Get(_iconSize, scalePercent);
        if (arrow is null)
        {
            arrow = FallbackArrow();
            warnings.Add($"no calibration for {Calibration.Key(_iconSize, scalePercent)}; " +
                         $"using {Calibration.Key(48, 100)} = ({arrow.Dx},{arrow.Dy},{arrow.Size}). Run 'deskwall calibrate'.");
        }

        var ico = BlankIcon.Ensure();
        var desktop = _desktopDir();
        Directory.CreateDirectory(desktop);
        var ordered = ShortcutPlan.Ordered(shortcuts);

        var owned = LoadOwned();
        var nowOwned = new Dictionary<string, string>();
        var written = 0;
        var wanted = new List<(string Path, int X, int Y)>();
        foreach (var s in ordered)
        {
            try
            {
                var path = Path.Combine(desktop, ShortcutPlan.SlotFileName(s.Slot));
                var spec = ShortcutFiles.SpecFor(s, ico);
                var key = SpecKey(spec);
                if (!Matches(path, SlotKey(s.Slot), key, spec, owned))
                {
                    ShortcutFiles.Write(path, spec);
                    written++;
                }
                nowOwned[SlotKey(s.Slot)] = key;
                var (x, y) = ShortcutPlan.IconPosition(s.Rect, arrow, pad);
                wanted.Add((path, x, y));
            }
            catch (Exception ex)
            {
                warnings.Add($"slot {s.Slot} ({s.Id}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        var removed = RemoveStale(desktop, owned.Keys, nowOwned, warnings);
        SaveOwned(nowOwned);
        var positioned = PlaceAndVerify(wanted, settle: written > 0, warnings);
        return new ShortcutOutcome(written, positioned, removed, warnings);
    }

    /// <summary>Delete every slot file on the desktop (uninstall). Unlike Reconcile this is not scoped to
    /// the slots we own: uninstall means the desktop goes back to having no slot files at all.</summary>
    public int RemoveAll()
    {
        var desktop = _desktopDir();
        var removed = 0;
        if (Directory.Exists(desktop))
        {
            foreach (var file in Directory.EnumerateFiles(desktop, "*.lnk"))
            {
                if (ShortcutPlan.SlotFromFileName(Path.GetFileName(file)) is null) continue;
                try { ShortcutFiles.Delete(file); removed++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (File.Exists(OwnedFile)) File.Delete(OwnedFile);
        return removed;
    }

    /// <summary>The state we last wrote, so the tick can skip Reconcile when nothing changed: slots,
    /// rects, targets, tooltips, the pad and the arrow rect in force for this scale.</summary>
    public string Fingerprint(IReadOnlyList<ResolvedShortcut> shortcuts, int scalePercent)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        var arrow = calibration.Get(_iconSize, scalePercent) ?? FallbackArrow();
        var sb = new StringBuilder();
        sb.Append(pad).Append('|').Append(scalePercent).Append('|').Append(_iconSize).Append('|')
          .Append(arrow.Dx).Append(',').Append(arrow.Dy).Append(',').Append(arrow.Size);
        foreach (var s in ShortcutPlan.Ordered(shortcuts))
            sb.Append('\u001F').Append(s.Slot)
              .Append('|').Append(s.Rect.X).Append(',').Append(s.Rect.Y).Append(',').Append(s.Rect.W).Append(',').Append(s.Rect.H)
              .Append('|').Append(s.Target)
              .Append('|').Append(s.Tooltip);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private ArrowRect FallbackArrow() => calibration.Get(48, 100) ?? Calibration.Seed().Get(48, 100)!;

    /// <summary>True when the .lnk already on disk is the one we want, so it is left alone.
    /// <para>
    /// A protocol target does not round-trip: the shell stores <c>steam://rungameid/620</c> as an id
    /// list and <c>GetPath</c> with <c>SLGP_RAWPATH</c> reads it back as the empty string (measured).
    /// Comparing targets alone therefore never matches, so every reconcile would rewrite every slot,
    /// which makes Explorer re-enumerate the desktop and throw the positions away. The hash of the
    /// spec last written to this slot is kept in <c>shortcuts-owned.json</c> and compared instead; the
    /// fields that do round-trip are still read back, so a .lnk edited behind our back is rewritten.
    /// </para>
    /// <para>WorkingDirectory is deliberately not compared: the shell normalises it for some
    /// targets.</para></summary>
    private static bool Matches(string path, string slotKey, string key, ShortcutSpec wanted, Dictionary<string, string> owned)
    {
        if (!owned.TryGetValue(slotKey, out var previous) || previous != key) return false;
        var existing = ShortcutFiles.Read(path);   // null when the file is missing or is not a link
        return existing is not null
               && string.Equals(existing.Arguments, wanted.Arguments, StringComparison.Ordinal)
               && string.Equals(existing.Description, wanted.Description, StringComparison.Ordinal)
               && string.Equals(existing.IconPath, wanted.IconPath, StringComparison.OrdinalIgnoreCase)
               && (existing.Target.Length == 0 || string.Equals(existing.Target, wanted.Target, StringComparison.OrdinalIgnoreCase));
    }

    private static string SlotKey(int slot) => slot.ToString(CultureInfo.InvariantCulture);

    private static string SpecKey(ShortcutSpec spec)
        => Hash(string.Join('\u001F', spec.Target, spec.Arguments, spec.WorkingDirectory, spec.Description, spec.IconPath));

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Delete the slot files this manager wrote that are no longer wanted. Scoped to the
    /// owned set on purpose: the v0 PowerShell tool owns slots 0..3 on the same desktop.</summary>
    private int RemoveStale(string desktop, IEnumerable<string> owned, Dictionary<string, string> keep, List<string> warnings)
    {
        var removed = 0;
        foreach (var slotKey in owned)
        {
            if (keep.ContainsKey(slotKey)) continue;
            if (!int.TryParse(slotKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)
                || slot is < 0 or > MaxSlot) continue;
            var path = Path.Combine(desktop, ShortcutPlan.SlotFileName(slot));
            if (!File.Exists(path)) continue;
            try { ShortcutFiles.Delete(path); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"slot {slot}: could not delete: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return removed;
    }

    private int PlaceAndVerify(List<(string Path, int X, int Y)> wanted, bool settle, List<string> warnings)
    {
        if (wanted.Count == 0) return 0;
        if (settle) Thread.Sleep(SettleMs);

        var pending = wanted;
        var placed = 0;
        for (var round = 0; round < PositionRounds && pending.Count > 0; round++)
        {
            if (round > 0) Thread.Sleep(RetryDelayMs);
            try { DesktopView.Position(pending); }
            catch (Exception ex)
            {
                warnings.Add($"position round {round + 1}: {ex.GetType().Name}: {ex.Message}");
            }

            var missed = new List<(string Path, int X, int Y)>();
            foreach (var it in pending)
            {
                var got = DesktopView.GetPosition(it.Path);
                if (got is not null && got.Value.X == it.X && got.Value.Y == it.Y) placed++;
                else missed.Add(it);
            }
            pending = missed;
        }

        foreach (var it in pending)
        {
            var got = DesktopView.GetPosition(it.Path);
            warnings.Add($"slot {ShortcutPlan.SlotFromFileName(Path.GetFileName(it.Path))}: wanted ({it.X},{it.Y}), " +
                         $"got {(got is null ? "nothing" : $"({got.Value.X},{got.Value.Y})")} after {PositionRounds} attempts");
        }
        return placed;
    }

    private static Dictionary<string, string> LoadOwned()
    {
        var path = OwnedFile;
        if (!File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ShortcutOwnershipJsonContext.Default.OwnedSlots)?.Slots ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new Dictionary<string, string>(); }
    }

    private static void SaveOwned(Dictionary<string, string> slots)
    {
        var path = OwnedFile;
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new OwnedSlots { Slots = slots }, ShortcutOwnershipJsonContext.Default.OwnedSlots));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>Slot number -> hash of the spec this manager last wrote there. Only these slots are
    /// ever deleted by <see cref="Reconcile"/>, so a slot file somebody else owns is left alone, and
    /// the hash is how an unchanged slot is recognised without reading the target back.</summary>
    public sealed class OwnedSlots
    {
        public Dictionary<string, string> Slots { get; set; } = new();
    }
}

[JsonSerializable(typeof(ShortcutManager.OwnedSlots))]
internal sealed partial class ShortcutOwnershipJsonContext : JsonSerializerContext;
