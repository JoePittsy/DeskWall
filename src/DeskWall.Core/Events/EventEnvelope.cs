using System.Globalization;
using System.Text.Json;
using DeskWall.Core.Bindings;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;

namespace DeskWall.Core.Events;

/// <summary>One accepted event: a patch to a provider's record plus what to do about it.
/// A CloudEvents subset (spec section 4) without the conformance - unknown attributes are
/// ignored rather than rejected, and a missing "specversion" is never an error, because the
/// producer is a user's three-line script and every rejection is a thing they have to debug
/// through a pipe that tells them nothing.</summary>
/// <param name="Source">Which provider this patches. The only routing key in v1.</param>
/// <param name="Data">The payload, merged into (or replacing) that provider's data record.</param>
/// <param name="SentAt">The producer's own "time"; published as sentAt.</param>
/// <param name="Replace">true drops the fields this event does not carry. Default is a merge.</param>
/// <param name="Wake">false updates state without waking the daemon. Default is to wake.</param>
public sealed record EventEnvelope(
    string Source,
    RecordValue Data,
    string? Type,
    string? Subject,
    string? Id,
    DateTimeOffset? SentAt,
    bool Replace,
    bool Wake);

public static class EventEnvelopeParser
{
    /// <summary>Null result means rejected; reason is never null then.</summary>
    public static (EventEnvelope? Event, string? Reason) Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (null, "empty line");
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "the event must be a JSON object");

            if (!root.TryGetProperty("source", out var sourceEl) || sourceEl.ValueKind != JsonValueKind.String)
                return (null, "missing \"source\"");
            var source = sourceEl.GetString();
            if (string.IsNullOrWhiteSpace(source)) return (null, "\"source\" is empty; it names the provider to patch");
            // A source name becomes the first segment of every binding into this provider, so a
            // name no binding could spell would be unreachable by definition.
            if (!BindingParser.IsName(source))
                return (null, $"\"source\" \"{source}\" is not a valid binding name: a letter or _ then letters, digits, _ or -");

            if (!root.TryGetProperty("data", out var dataEl)) return (null, "missing \"data\"");
            if (dataEl.ValueKind != JsonValueKind.Object) return (null, "\"data\" must be a JSON object");
            // From, not JsonValues.Parse: Parse would re-serialize the element we already hold and
            // read it back. It is the same mapping every other source's payload goes through.
            var data = JsonValues.From(dataEl);

            return (new EventEnvelope(
                source,
                data,
                Text(root, "type"),
                Text(root, "subject"),
                Text(root, "id"),
                Time(root, "time"),
                Flag(root, "replace", false),
                Flag(root, "wake", true)), null);
        }
        catch (JsonException ex)
        {
            return (null, "invalid json: " + ex.Message);
        }
        catch (FormatException ex)
        {
            // JsonValues rejects a shape it cannot map; that is the payload's fault, not a crash.
            return (null, "\"data\" could not be read: " + ex.Message);
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static bool Flag(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.ValueKind == JsonValueKind.True
            : fallback;

    /// <summary>A timestamp we cannot read is dropped, never fatal: the producer's clock is a
    /// nicety and receivedAt is the one the layout actually ages from.</summary>
    private static DateTimeOffset? Time(JsonElement root, string name)
        => Text(root, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
}
