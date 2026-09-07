using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Consultologist.CopilotAgent.Core.Engine;

/// <summary>
/// The JSON kind a supplied value travels as — the agent-side mirror of the
/// engine's <c>ConsultInputKind</c> (Consultologist.PackageFormat). The engine
/// dispatches on exactly these; this carrier only has to emit each one's wire
/// shape.
/// </summary>
public enum ConsultInputKind
{
    Text,
    Boolean,
    Number,
    Object,
    Array,

    /// <summary>A JSON null — legal only inside structure (an array element or
    /// an object field). The engine reads a top-level null as blank text.</summary>
    Null,
}

/// <summary>One field of an object value, in the order the caller sent it.</summary>
public sealed record ConsultInputEntry(string Id, ConsultInputValue Value);

/// <summary>
/// One supplied input value, typed on the wire exactly as the engine's
/// <c>ConsultInputValue</c> expects (package-format v8/v9 § 4):
/// <list type="bullet">
///   <item>text, date and enum → a JSON <b>string</b>;</item>
///   <item>boolean → a JSON <b>boolean</b>;</item>
///   <item>number → a JSON <b>number</b> carried as the caller's exact spelling
///     ("1.50" ≠ "1.5"), no exponent, within <see cref="decimal"/> range;</item>
///   <item>object → a JSON <b>object</b>, fields in supplied order, ids unique;</item>
///   <item>array → a JSON <b>array</b>, elements in order.</item>
/// </list>
/// This is the agent's own carrier, deliberately independent of the engine
/// assemblies: the agent is a satellite that produces the right bytes, and the
/// engine re-validates every value against the package declaration at job start
/// (a mismatch is the engine's 422, never ours to pre-judge).
/// </summary>
[JsonConverter(typeof(ConsultInputValueJsonConverter))]
public sealed class ConsultInputValue
{
    private static readonly ConsultInputValue NullInstance = new(ConsultInputKind.Null);

    private ConsultInputValue(
        ConsultInputKind kind,
        string? text = null,
        bool? flag = null,
        string? number = null,
        IReadOnlyList<ConsultInputEntry>? fields = null,
        IReadOnlyList<ConsultInputValue>? elements = null)
    {
        Kind = kind;
        Text = text;
        Flag = flag;
        Number = number;
        Fields = fields;
        Elements = elements;
    }

    public ConsultInputKind Kind { get; }

    /// <summary>The text, when <see cref="Kind"/> is Text.</summary>
    public string? Text { get; }

    /// <summary>The flag, when <see cref="Kind"/> is Boolean.</summary>
    public bool? Flag { get; }

    /// <summary>The number's spelling exactly as sent — "1.50", not "1.5".</summary>
    public string? Number { get; }

    /// <summary>The object's fields in supplied order, when <see cref="Kind"/> is Object.</summary>
    public IReadOnlyList<ConsultInputEntry>? Fields { get; }

    /// <summary>The array's elements in supplied order, when <see cref="Kind"/> is Array.</summary>
    public IReadOnlyList<ConsultInputValue>? Elements { get; }

    /// <summary>text, date and enum all travel as a JSON string.</summary>
    public static ConsultInputValue OfText(string text) =>
        new(ConsultInputKind.Text, text: text ?? throw new ArgumentNullException(nameof(text)));

    public static ConsultInputValue OfBoolean(bool value) => new(ConsultInputKind.Boolean, flag: value);

    /// <summary>
    /// Accepts a spelling only if it parses as a decimal with no exponent AND
    /// that decimal prints back as exactly the spelling — the same rule the
    /// engine's <c>TryParseNumber</c> enforces, so "the digits as sent" holds
    /// by construction. Exponent form, out-of-range and excess precision fail.
    /// </summary>
    public static bool TryOfNumber(string spelling, out ConsultInputValue value)
    {
        value = NullInstance;

        if (string.IsNullOrEmpty(spelling))
        {
            return false;
        }

        if (!decimal.TryParse(
                spelling,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.ToString(CultureInfo.InvariantCulture), spelling, StringComparison.Ordinal))
        {
            return false;
        }

        value = new ConsultInputValue(ConsultInputKind.Number, number: spelling);
        return true;
    }

    public static ConsultInputValue OfNumber(string spelling) =>
        TryOfNumber(spelling, out var value)
            ? value
            : throw new ArgumentException($"'{spelling}' is not a plain decimal the format carries.", nameof(spelling));

    /// <summary>An object of fields, ids unique and order preserved.</summary>
    public static ConsultInputValue OfObject(IEnumerable<ConsultInputEntry> fields)
    {
        var list = fields.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in list)
        {
            if (!seen.Add(field.Id))
            {
                throw new ArgumentException($"The object repeats the field '{field.Id}'.", nameof(fields));
            }
        }

        return new ConsultInputValue(ConsultInputKind.Object, fields: list);
    }

    /// <summary>An array of values, order preserved.</summary>
    public static ConsultInputValue OfArray(IEnumerable<ConsultInputValue> elements) =>
        new(ConsultInputKind.Array, elements: elements.ToList());

    /// <summary>A JSON null — legal only inside structure.</summary>
    public static ConsultInputValue NullElement => NullInstance;

    /// <summary>This value as its wire <see cref="JsonNode"/> — the shape the
    /// engine reads. A number rides as a raw JSON number literal so its exact
    /// spelling survives (System.Text.Json preserves a parsed value's raw text).</summary>
    public JsonNode? ToJsonNode() => Kind switch
    {
        ConsultInputKind.Text => JsonValue.Create(Text),
        ConsultInputKind.Boolean => JsonValue.Create(Flag!.Value),
        ConsultInputKind.Number => JsonNode.Parse(Number!),
        ConsultInputKind.Null => null,
        ConsultInputKind.Object => BuildObject(),
        ConsultInputKind.Array => BuildArray(),
        _ => throw new InvalidOperationException($"Unhandled kind {Kind}."),
    };

    private JsonObject BuildObject()
    {
        var obj = new JsonObject();
        foreach (var field in Fields!)
        {
            obj[field.Id] = field.Value.ToJsonNode();
        }

        return obj;
    }

    private JsonArray BuildArray()
    {
        var arr = new JsonArray();
        foreach (var element in Elements!)
        {
            arr.Add(element.ToJsonNode());
        }

        return arr;
    }
}

/// <summary>
/// Writes a <see cref="ConsultInputValue"/> as its wire JSON. Read is not
/// implemented: the agent only ever sends these — it never deserialises an
/// inbound input map.
/// </summary>
public sealed class ConsultInputValueJsonConverter : JsonConverter<ConsultInputValue>
{
    public override ConsultInputValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("ConsultInputValue is send-only on the agent.");

    public override void Write(Utf8JsonWriter writer, ConsultInputValue value, JsonSerializerOptions options)
    {
        var node = value.ToJsonNode();
        if (node is null)
        {
            writer.WriteNullValue();
            return;
        }

        node.WriteTo(writer, options);
    }
}
