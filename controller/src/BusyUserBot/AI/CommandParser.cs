using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BusyUserBot.Models;

namespace BusyUserBot.AI;

/// <summary>
/// Parses an AI reply into a validated <see cref="AiDecision"/>. Defensive on
/// purpose — small models love to wrap JSON in Markdown fences or add a
/// trailing apology. We strip those before deserialising.
/// </summary>
internal static class CommandParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
                         | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new LenientNullableIntConverter() },
    };

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "move", "click", "down", "up", "scroll", "type", "key", "wait", "display",
    };

    private static readonly HashSet<string> AllowedButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        "left", "right", "middle",
    };

    public static AiDecision Parse(string raw)
    {
        var json = ExtractJson(raw);
        var decision = JsonSerializer.Deserialize<AiDecision>(json, Options)
                       ?? throw new FormatException("AI returned null");

        Validate(decision);
        return decision;
    }

    /// <summary>
    /// Parse a VALIDATION-MODE reply. Falls back to a permissive scan in case
    /// the model wraps the JSON in prose or fences.
    /// </summary>
    public static AiValidation ParseValidation(string raw)
    {
        var json = ExtractJson(raw);
        var v = JsonSerializer.Deserialize<AiValidation>(json, Options)
                ?? throw new FormatException("AI returned null validation");
        return v;
    }

    /// <summary>Parse a Planner reply into a <see cref="PlanOutput"/>.</summary>
    public static PlanOutput ParsePlan(string raw)
    {
        var json = ExtractJson(raw);
        var p = JsonSerializer.Deserialize<PlanOutput>(json, Options)
                ?? throw new FormatException("AI returned null plan");
        p.Goal ??= "";
        p.Steps ??= new List<PlanStep>();
        foreach (var s in p.Steps)
        {
            s.Description ??= "";
            s.SuccessCriteria ??= "";
        }
        if (p.Steps.Count == 0)
            throw new FormatException("plan has no steps");
        return p;
    }

    /// <summary>Parse a Validator reply into a <see cref="ValidatorVerdict"/>.</summary>
    public static ValidatorVerdict ParseValidatorVerdict(string raw)
    {
        var json = ExtractJson(raw);
        var v = JsonSerializer.Deserialize<ValidatorVerdict>(json, Options)
                ?? throw new FormatException("AI returned null verdict");
        v.Reasons ??= new List<string>();
        return v;
    }

    private static string ExtractJson(string raw)
    {
        // Strip ```json ... ``` fences if present.
        var fence = Regex.Match(raw, "```(?:json)?\\s*(\\{.*?\\})\\s*```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (fence.Success) return fence.Groups[1].Value;

        // Otherwise grab from the first { to the matching last }.
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new FormatException("no JSON object found in AI reply");
        return raw.Substring(start, end - start + 1);
    }

    private static void Validate(AiDecision d)
    {
        if (d.Actions is null) throw new FormatException("missing actions[]");

        foreach (var a in d.Actions)
        {
            if (string.IsNullOrWhiteSpace(a.Type) || !AllowedTypes.Contains(a.Type))
                throw new FormatException($"invalid action type: {a.Type}");

            switch (a.Type.ToLowerInvariant())
            {
                case "move":
                    if (a.X is null || a.Y is null)
                        throw new FormatException("move requires x and y");
                    break;
                case "click":
                case "down":
                case "up":
                    if (a.Button is not null && !AllowedButtons.Contains(a.Button))
                        throw new FormatException($"invalid button: {a.Button}");
                    break;
                case "type":
                    if (a.Text is null) throw new FormatException("type requires text");
                    break;
                case "key":
                    if (a.Keys is null || a.Keys.Length == 0)
                        throw new FormatException("key requires non-empty keys[]");
                    break;
                case "wait":
                    if (a.Ms is null or < 0 or > 10000)
                        throw new FormatException("wait.ms must be 0..10000");
                    break;
            }
        }
    }
}

/// <summary>
/// Accepts integers, floats (rounded), and numeric strings for nullable int
/// fields. Small vision models frequently emit fractional pixel coordinates
/// like "x": 640.5 or stringified numbers like "ms": "500".
/// </summary>
internal sealed class LenientNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var i)) return i;
                if (reader.TryGetInt64(out var l)) return checked((int)l);
                return (int)Math.Round(reader.GetDouble(), MidpointRounding.AwayFromZero);
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var si)) return si;
                if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var sd))
                    return (int)Math.Round(sd, MidpointRounding.AwayFromZero);
                throw new JsonException($"cannot parse '{s}' as int");
            case JsonTokenType.True:  return 1;
            case JsonTokenType.False: return 0;
            case JsonTokenType.StartArray:
                // Some models emit "x": [640, 360] instead of x/y scalars.
                // Take the first numeric/string element and skip the rest.
                int? first = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (first is null && reader.TokenType is JsonTokenType.Number
                                         or JsonTokenType.String
                                         or JsonTokenType.True
                                         or JsonTokenType.False
                                         or JsonTokenType.Null)
                    {
                        first = Read(ref reader, typeToConvert, options);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                return first;
            case JsonTokenType.StartObject:
                reader.Skip();
                return null;
            default:
                throw new JsonException($"unexpected token {reader.TokenType} for int?");
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}
