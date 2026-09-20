using System.Globalization;
using System.Text;
using System.Text.Json;
using DeskWall.Core.Display;

namespace DeskWall.Core.Verify;

/// <summary>One shortcut slot measured against the composed frame. <paramref name="LeftPad"/> and
/// <paramref name="BottomPad"/> are -1 when no arrow was found, so a missing icon can never read as
/// a pad of 0.</summary>
/// <param name="Cover">The cover rect the icon is supposed to sit on, in canvas pixels.</param>
/// <param name="Wanted">Where <see cref="Shortcuts.ShortcutPlan.IconPosition"/> says the item goes.</param>
/// <param name="Got">Where the desktop folder view says it is, or null when the shell does not know it.</param>
/// <param name="ArrowBox">The bounding box of everything inside the cover that differs from the
/// composed frame: the shortcut-arrow overlay, when the icon itself is transparent.</param>
public sealed record SlotCheck(
    int Slot,
    string Id,
    Rect Cover,
    (int X, int Y) Wanted,
    (int X, int Y)? Got,
    Rect? ArrowBox,
    int LeftPad,
    int BottomPad,
    bool Ok,
    string? Note)
{
    public string ToLine() =>
        $"slot {Slot,2}  {Id,-28}  cover ({Cover.X},{Cover.Y},{Cover.W},{Cover.H})  " +
        $"wanted ({Wanted.X},{Wanted.Y})  got {(Got is null ? "-" : $"({Got.Value.X},{Got.Value.Y})")}  " +
        $"left pad {Pad(LeftPad)}, bottom pad {Pad(BottomPad)}  {(Ok ? "OK" : Note ?? "FAIL")}";

    private static string Pad(int n) => n < 0 ? "-" : n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What one <see cref="Verifier.Run"/> measured. <see cref="Ok"/> is true only when every slot
/// was found at the wanted padding: the exit code of `deskwall verify` is this and nothing else.</summary>
public sealed record VerifyReport(
    DisplaySignature Signature,
    IReadOnlyList<SlotCheck> Slots,
    string ScreenshotPath,
    string ClockCropPath,
    bool Ok)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append("display ").AppendLine(Signature.Key);
        if (Slots.Count == 0) sb.AppendLine("(no shortcut components in the layout)");
        foreach (var s in Slots) sb.AppendLine(s.ToLine());
        sb.Append("screenshot: ").AppendLine(ScreenshotPath);
        if (ClockCropPath.Length > 0) sb.Append("clock crop: ").AppendLine(ClockCropPath);
        sb.Append("RESULT: ").Append(Ok ? "OK" : "FAIL");
        return sb.ToString();
    }

    /// <summary>Hand-written with Utf8JsonWriter rather than a serializer: the record holds tuples and
    /// nullable structs, which the source generator does not shape the way a report reader wants, and
    /// reflection-based serialization is not available under native AOT.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("signature", Signature.Key);
            w.WriteBoolean("ok", Ok);
            w.WriteString("screenshot", ScreenshotPath);
            w.WriteString("clockCrop", ClockCropPath);
            w.WriteStartArray("slots");
            foreach (var s in Slots)
            {
                w.WriteStartObject();
                w.WriteNumber("slot", s.Slot);
                w.WriteString("id", s.Id);
                WriteRect(w, "cover", s.Cover);
                WritePoint(w, "wanted", s.Wanted);
                if (s.Got is { } got) WritePoint(w, "got", got); else w.WriteNull("got");
                if (s.ArrowBox is { } box) WriteRect(w, "arrowBox", box); else w.WriteNull("arrowBox");
                w.WriteNumber("leftPad", s.LeftPad);
                w.WriteNumber("bottomPad", s.BottomPad);
                w.WriteBoolean("ok", s.Ok);
                if (s.Note is null) w.WriteNull("note"); else w.WriteString("note", s.Note);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRect(Utf8JsonWriter w, string name, Rect r)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(r.X); w.WriteNumberValue(r.Y); w.WriteNumberValue(r.W); w.WriteNumberValue(r.H);
        w.WriteEndArray();
    }

    private static void WritePoint(Utf8JsonWriter w, string name, (int X, int Y) p)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(p.X); w.WriteNumberValue(p.Y);
        w.WriteEndArray();
    }
}
