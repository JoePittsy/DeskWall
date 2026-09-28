using DeskWall.Core.Values;

namespace DeskWall.Core.Events;

/// <summary>One provider's accumulated state: everything it has ever sent, merged, plus the
/// metadata of its last event. Immutable, like <see cref="Sources.SourceSnapshot"/>; the bus
/// swaps whole records so a reader never sees a half-applied event.
/// <para>The record accumulates rather than replacing because a producer sends what changed - a
/// volume event knows the level and not the device name, and must not blank it. That is also
/// what makes the persisted record a usable description of the provider's shape (spec 3).</para></summary>
public sealed record ProviderRecord(
    string Name,
    RecordValue Data,
    string? Type, string? Subject, string? Id,
    DateTimeOffset? SentAt,
    DateTimeOffset ReceivedAt)
{
    public static ProviderRecord Empty(string name, DateTimeOffset at)
        => new(name, new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)), null, null, null, null, at);

    /// <summary>Merge (or replace) the payload and take the new metadata.
    /// <para>The merge is top level only. A deep merge has no obvious rule for a list (append?
    /// replace? match on what key?) and nobody has asked for one, so a nested object is one value
    /// like any other: sending {a:{y:2}} after {a:{x:1}} leaves a = {y:2}.</para>
    /// <para>Metadata is taken wholesale, not merged: spec section 5 says type/subject/id are
    /// "from the last event", and keeping a previous event's type would have it read as this
    /// one's.</para></summary>
    public ProviderRecord Apply(EventEnvelope e, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(e);
        RecordValue data;
        if (e.Replace) data = e.Data;
        else
        {
            var d = new Dictionary<string, Value>(Data.Fields, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in e.Data.Fields) d[k] = v;
            data = new RecordValue(d);
        }
        return this with
        {
            Data = data,
            Type = e.Type,
            Subject = e.Subject,
            Id = e.Id,
            SentAt = e.SentAt,
            ReceivedAt = at,
        };
    }

    /// <summary>What the value tree sees. ageSeconds is computed from `now`, not stored.
    /// <para>An absent attribute is an absent key, not an empty string: a binding to a missing
    /// value falls back to the component's own default, and "" would paint nothing instead.</para></summary>
    public RecordValue ToValues(DateTimeOffset now)
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["data"] = Data,
            ["receivedAt"] = new TimeValue(ReceivedAt),
            // Whole seconds: this is recomputed every refresh, and an unrounded age would change
            // the content key of every bound component on every tick forever.
            // Clamped at zero because a clock correction between receipt and refresh would
            // otherwise publish a negative age, which formats as "-3 s ago".
            ["ageSeconds"] = new NumberValue(Math.Max(0, Math.Round((now - ReceivedAt).TotalSeconds))),
        };
        if (Type is not null) d["type"] = new TextValue(Type);
        if (Subject is not null) d["subject"] = new TextValue(Subject);
        if (Id is not null) d["id"] = new TextValue(Id);
        if (SentAt is { } sent) d["sentAt"] = new TimeValue(sent);
        return new RecordValue(d);
    }
}
