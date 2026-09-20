using System.Text.Json;
using System.Text.Json.Serialization;
using DeskWall.Core.Bindings;

namespace DeskWall.Core.Layout;

/// <summary>A component property: a literal string or a binding. Numbers are literals in
/// invariant text; the resolver parses them.</summary>
[JsonConverter(typeof(PropertyValueConverter))]
public sealed class PropertyValue
{
    public string? LiteralText { get; }
    public Binding? Binding { get; }

    private PropertyValue(string? literal, Binding? binding) { LiteralText = literal; Binding = binding; }

    public static PropertyValue Literal(string s) => new(s, null);
    public static PropertyValue Literal(double d) => new(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture), null);
    public static PropertyValue Bound(Binding b) => new(null, b);
    public bool IsBound => Binding is not null;
    public override string ToString() => IsBound ? $"{{bind {Binding}}}" : LiteralText ?? "";
}

public sealed class PropertyValueConverter : JsonConverter<PropertyValue>
{
    public override PropertyValue Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.String: return PropertyValue.Literal(r.GetString()!);
            case JsonTokenType.Number: return PropertyValue.Literal(r.GetDouble());
            case JsonTokenType.True: return PropertyValue.Literal("true");
            case JsonTokenType.False: return PropertyValue.Literal("false");
            case JsonTokenType.StartObject:
                string? bind = null;
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    var name = r.GetString(); r.Read();
                    if (name == "bind") bind = r.GetString();
                    else r.Skip();
                }
                if (bind is null) throw new JsonException("property object needs \"bind\"");
                return PropertyValue.Bound(Binding.Parse(bind));
            default: throw new JsonException($"bad property token {r.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter w, PropertyValue v, JsonSerializerOptions o)
    {
        if (v.IsBound) { w.WriteStartObject(); w.WriteString("bind", v.Binding!.ToString()); w.WriteEndObject(); }
        else w.WriteStringValue(v.LiteralText);
    }
}

/// <summary>Rect as [x, y, w, h].</summary>
public sealed class RectConverter : JsonConverter<Rect>
{
    public override Rect Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("rect must be [x,y,w,h]");
        var v = new int[4]; var i = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) { if (i > 3) throw new JsonException("rect has 4 numbers"); v[i++] = r.GetInt32(); }
        if (i != 4) throw new JsonException("rect has 4 numbers");
        return new Rect(v[0], v[1], v[2], v[3]);
    }
    public override void Write(Utf8JsonWriter w, Rect v, JsonSerializerOptions o)
    { w.WriteStartArray(); w.WriteNumberValue(v.X); w.WriteNumberValue(v.Y); w.WriteNumberValue(v.W); w.WriteNumberValue(v.H); w.WriteEndArray(); }
}
