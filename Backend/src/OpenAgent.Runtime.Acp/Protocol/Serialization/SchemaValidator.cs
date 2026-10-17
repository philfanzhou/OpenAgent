using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenAgent.Runtime.Acp.Protocol.Serialization;

/// <summary>Evaluator for the pinned schema's vocabulary, with ACP's opt-in read recovery.</summary>
internal static class SchemaValidator
{
    private static readonly JsonObject Definitions = Load();
    private static readonly ConcurrentDictionary<string, JsonObject> Expanded = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Envelopes = new(StringComparer.Ordinal)
    {
        "ClientRequest", "AgentRequest", "ClientNotification", "AgentNotification", "ClientResponse", "AgentResponse"
    };

    internal static JsonElement Process(string name, JsonElement value, bool compatibility)
    {
        RejectDuplicates(value);
        if (name is "ClientResponse" or "AgentResponse" && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("result", out _) && value.TryGetProperty("error", out _))
            throw new JsonException("A JSON-RPC response cannot carry both a result and an error.");
        JsonObject schema = Expanded.GetOrAdd(name, key => Expand(Definitions[key]!.AsObject()));
        string? method = Envelopes.Contains(name) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("method", out JsonElement tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
        JsonNode? normalized = Apply(JsonNode.Parse(value.GetRawText()), schema, compatibility, name, method);
        using JsonDocument document = JsonDocument.Parse(normalized?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    private static JsonObject Load()
    {
        using Stream stream = typeof(SchemaValidator).Assembly.GetManifestResourceStream("Acp.ProtocolSchema.json")
            ?? throw new InvalidOperationException("The protocol schema resource is missing.");
        return JsonNode.Parse(stream)!["$defs"]!.AsObject();
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static JsonObject Expand(JsonObject schema)
    {
        JsonObject expanded = new();
        if (schema["$ref"] is JsonValue reference)
            Merge(expanded, Expanded.GetOrAdd(reference.GetValue<string>(), key => Expand(Definitions[key.Split('/')[^1]]!.AsObject())));
        if (schema["allOf"] is JsonArray all)
            foreach (JsonObject part in all.OfType<JsonObject>()) Merge(expanded, Expand(part));
        Merge(expanded, schema);
        expanded.Remove("allOf"); expanded.Remove("$ref");
        return expanded;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach ((string name, JsonNode? value) in source)
        {
            if (name == "properties" && target[name] is JsonObject properties)
            {
                foreach ((string field, JsonNode? definition) in value!.AsObject()) properties[field] = definition?.DeepClone();
            }
            else if (name == "required" && target[name] is JsonArray required)
            {
                foreach (JsonNode? field in value!.AsArray())
                    if (!required.Any(item => item!.GetValue<string>() == field!.GetValue<string>())) required.Add(field?.DeepClone());
            }
            else target[name] = value?.DeepClone();
        }
    }

    private static JsonNode? Apply(JsonNode? value, JsonObject original, bool compatibility, string? envelope = null, string? method = null)
    {
        JsonObject schema = Expand(original);
        // Match a known wire tag before any permissive/default alternative. A malformed known
        // payload must not fall through to another variant (auth, MCP, config, elicitation).
        if ((schema["oneOf"] ?? schema["anyOf"]) is JsonArray alternatives)
        {
            JsonObject common = (JsonObject)schema.DeepClone(); common.Remove("oneOf"); common.Remove("anyOf"); common.Remove("discriminator");
            List<JsonObject> candidates = alternatives.OfType<JsonObject>().ToList();
            // Envelope payload unions tag every candidate with the wire method its definition
            // serves, so the in-band envelope method selects exactly one payload schema and no
            // other candidate can mask it (an empty-required variant must never swallow a
            // sibling payload). Response payloads arrive without their request method; they are
            // preserved verbatim and the typed As* projections validate the exact variant.
            List<string?> methods = candidates.Select(candidate => Expand(candidate)["x-method"]?.GetValue<string>()).ToList();
            if (methods.Any(tag => tag is not null))
            {
                if (method is null)
                {
                    if (value is not JsonObject) throw new JsonException("The envelope payload must be a protocol object.");
                    return value.DeepClone();
                }
                JsonObject? selected = null;
                for (int index = 0; index < candidates.Count; index++)
                {
                    if (methods[index] != method) continue;
                    if (selected is not null) throw new JsonException("Two payload schemas claim the same protocol method.");
                    selected = candidates[index];
                }
                if (selected is null)
                {
                    if (HasKnownMethod(method!)) throw new JsonException($"The {envelope} payload does not serve the '{method}' method.");
                    // Anything unknown to the pinned v1 catalog is an extension method, carried
                    // by the variant without a method tag and preserved verbatim.
                    selected = candidates.Single(candidate => Expand(candidate)["x-method"] is null);
                }
                JsonObject payload = Expand(new JsonObject { ["allOf"] = new JsonArray(common.DeepClone(), selected.DeepClone()) });
                return Apply(value?.DeepClone(), payload, compatibility);
            }
            if (value is JsonObject tagged)
            {
                List<JsonObject> known = candidates.Where(candidate => HasMatchingTag(tagged, Expand(candidate))).ToList();
                if (known.Count > 0) candidates = known;
            }
            List<JsonNode?> matches = [];
            foreach (JsonObject candidate in candidates)
            {
                JsonObject merged = Expand(new JsonObject { ["allOf"] = new JsonArray(common.DeepClone(), candidate.DeepClone()) });
                try { matches.Add(Apply(value?.DeepClone(), merged, compatibility, envelope, method)); }
                catch (JsonException) { }
                if (matches.Count > 0 && schema.ContainsKey("anyOf")) return matches[0];
            }
            if (matches.Count != 1) throw new JsonException("The value does not match a unique protocol variant.");
            return matches[0];
        }
        if (schema["not"] is JsonObject forbidden)
        {
            bool matches;
            try { Apply(value?.DeepClone(), forbidden, compatibility: true); matches = true; }
            catch (JsonException) { matches = false; }
            if (matches) throw new JsonException("The value matches an excluded protocol variant.");
        }
        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(value, schema["const"])) throw new JsonException("Invalid protocol constant.");
        if (schema["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(value, choice))) throw new JsonException("Invalid enum value.");
        if (schema["type"] is JsonNode types)
        {
            string[] allowed = types is JsonArray list ? list.Select(t => t!.GetValue<string>()).ToArray() : [types.GetValue<string>()];
            if (!allowed.Any(type => IsType(value, type))) throw new JsonException("Invalid protocol value type.");
        }
        if (value is null) return null;
        if (value is JsonObject obj) return ApplyObject(obj, schema, compatibility, envelope, method);
        if (value is JsonArray array) return ApplyArray(array, schema, compatibility, envelope, method);
        using JsonDocument scalar = JsonDocument.Parse(value.ToJsonString());
        JsonElement element = scalar.RootElement;
        if (element.ValueKind == JsonValueKind.Number) CheckNumber(element, schema);
        if (element.ValueKind == JsonValueKind.String)
        {
            string text = element.GetString()!;
            int length = text.EnumerateRunes().Count();
            if (schema["minLength"] is JsonNode min && length < min.GetValue<int>() || schema["maxLength"] is JsonNode max && length > max.GetValue<int>())
                throw new JsonException("Invalid string length.");
            if (schema["pattern"] is JsonNode pattern && !Regex.IsMatch(text, pattern.GetValue<string>(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new JsonException("Invalid string pattern.");
        }
        return value.DeepClone();
    }

    private static bool HasKnownMethod(string method)
    {
        foreach ((string _, JsonNode? definition) in Definitions)
            if (definition?["x-method"]?.GetValue<string>() == method) return true;
        return false;
    }

    private static bool HasMatchingTag(JsonObject value, JsonObject schema)
    {
        if (schema["properties"] is not JsonObject fields) return false;
        return fields.Any(pair => pair.Value is JsonObject definition && definition["const"] is JsonNode constant
            && value.TryGetPropertyValue(pair.Key, out JsonNode? actual) && JsonNode.DeepEquals(actual, constant));
    }

    private static JsonObject ApplyObject(JsonObject value, JsonObject schema, bool compatibility, string? envelope, string? method)
    {
        JsonObject fields = schema["properties"] as JsonObject ?? new();
        HashSet<string> required = schema["required"]?.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (string name in required)
            if (!value.ContainsKey(name)) throw new JsonException("A required protocol property is missing.");
        JsonObject result = new();
        foreach ((string name, JsonNode? input) in value)
        {
            if (fields[name] is JsonObject field)
            {
                try { result[name] = Apply(input, field, compatibility, envelope, method); }
                catch (JsonException) when (compatibility && !required.Contains(name) && field["x-deserialize-default-on-error"]?.GetValue<bool>() == true)
                {
                    // No implicit empty collection: absence/null keep their original patch meaning.
                    // Explicit schema defaults are applied by generated *OrDefault getters.
                    if (AllowsNull(field)) result[name] = null;
                }
            }
            else if (schema["additionalProperties"] is JsonObject map) result[name] = Apply(input, map, compatibility);
            else if (schema["additionalProperties"]?.ToJsonString() == "true" || schema["unevaluatedProperties"]?.ToJsonString() == "true" || !schema.ContainsKey("properties"))
                result[name] = input?.DeepClone();
            else if (!compatibility) throw new JsonException("Unknown outbound property. Use _meta for extensions.");
        }
        return result;
    }

    private static JsonArray ApplyArray(JsonArray array, JsonObject schema, bool compatibility, string? envelope, string? method)
    {
        if (schema["minItems"] is JsonNode min && array.Count < min.GetValue<int>() || schema["maxItems"] is JsonNode max && array.Count > max.GetValue<int>())
            throw new JsonException("Invalid array length.");
        if (schema["items"] is not JsonObject items) return (JsonArray)array.DeepClone();
        JsonArray result = new();
        foreach (JsonNode? item in array)
        {
            try { result.Add(Apply(item, items, compatibility, envelope, method)); }
            catch (JsonException) when (compatibility && schema["x-deserialize-skip-invalid-items"]?.GetValue<bool>() == true) { }
        }
        return result;
    }

    private static bool AllowsNull(JsonObject schema)
    {
        JsonObject expanded = Expand(schema);
        if (expanded["type"] is JsonArray types && types.Any(t => t!.GetValue<string>() == "null")) return true;
        if (expanded["type"]?.ToJsonString() == "\"null\"") return true;
        if ((expanded["anyOf"] ?? expanded["oneOf"]) is JsonArray alternatives) return alternatives.OfType<JsonObject>().Any(AllowsNull);
        return !expanded.ContainsKey("type") && !expanded.ContainsKey("anyOf") && !expanded.ContainsKey("oneOf");
    }

    private static bool IsType(JsonNode? value, string type)
    {
        if (value is null) return type == "null";
        if (type == "object") return value is JsonObject;
        if (type == "array") return value is JsonArray;
        if (value is not JsonValue) return false;
        using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
        JsonElement element = document.RootElement;
        return type switch
        {
            "string" => element.ValueKind == JsonValueKind.String,
            "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number) && double.IsFinite(number),
            "integer" => element.ValueKind == JsonValueKind.Number && (element.TryGetInt64(out _) || element.TryGetUInt64(out _)),
            _ => false
        };
    }

    private static void CheckNumber(JsonElement value, JsonObject schema)
    {
        string? format = schema["format"]?.GetValue<string>();
        bool valid = format switch
        {
            "int64" => value.TryGetInt64(out _),
            "int32" => value.TryGetInt32(out _),
            "uint16" => value.TryGetUInt16(out _),
            "uint32" => value.TryGetUInt32(out _),
            "uint64" => value.TryGetUInt64(out _),
            _ => value.TryGetDouble(out double number) && double.IsFinite(number)
        };
        if (!valid) throw new JsonException("Protocol number is outside its range.");
        if (schema["minimum"] is JsonNode min && value.GetDecimal() < min.GetValue<decimal>() || schema["maximum"] is JsonNode max && value.GetDecimal() > max.GetValue<decimal>())
            throw new JsonException("Protocol number is outside its bounds.");
    }
}
