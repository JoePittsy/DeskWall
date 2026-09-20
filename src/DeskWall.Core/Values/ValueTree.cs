namespace DeskWall.Core.Values;

/// <summary>Root of all published values: one field per source name.</summary>
public static class ValueTree
{
    public static RecordValue Empty { get; } = new(new Dictionary<string, Value>());

    public static RecordValue Of(params (string Name, Value Value)[] fields)
        => new(fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.OrdinalIgnoreCase));
}
