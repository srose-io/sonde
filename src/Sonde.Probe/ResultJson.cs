using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sonde.Probe;

/// <summary>
/// Serialises an arbitrary script return value to a <see cref="JsonNode"/> the client can always
/// read back. Depth is capped, long strings and large collections are truncated with a marker,
/// reference cycles are broken, and a type that refuses to serialise falls back to
/// <c>ToString()</c>. The client must always get <em>something</em> back rather than a throw.
/// </summary>
public static class ResultJson
{
    /// <summary>How deep an object graph is walked unless the request says otherwise.</summary>
    public const int DefaultMaxDepth = 6;

    /// <summary>Strings longer than this are cut with a marker saying how much was dropped.</summary>
    public const int MaxStringLength = 10_000;

    /// <summary>Collections longer than this are cut with a marker.</summary>
    public const int MaxCollectionItems = 100;

    /// <summary>Objects with more public members than this are cut.</summary>
    public const int MaxObjectMembers = 200;

    private static readonly JsonSerializerOptions ScalarJson = new(JsonSerializerDefaults.Web);

    /// <summary>Serialises <paramref name="value"/>, walking at most <paramref name="maxDepth"/> levels.</summary>
    public static JsonNode? Serialize(object? value, int maxDepth = DefaultMaxDepth)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Walk(value, depth: 0, maxDepth, visited);
    }

    private static JsonNode? Walk(object? value, int depth, int maxDepth, HashSet<object> visited)
    {
        if (value is null)
            return null;

        try
        {
            var type = value.GetType();

            if (value is string s)
                return JsonValue.Create(Truncate(s));

            // Already-JSON values (a tapped payload, a script that built a JsonNode) pass through
            // as data: reflecting over a JsonNode exposes its Parent/Root cycle and loses the value.
            if (value is JsonNode node)
                return node.DeepClone();
            if (value is JsonElement element)
                return JsonSerializer.SerializeToNode(element);
            if (value is JsonDocument document)
                return JsonSerializer.SerializeToNode(document.RootElement);

            if (IsSimple(type))
                return JsonSerializer.SerializeToNode(value, ScalarJson);

            // Beyond scalars we are into graph territory: enforce the depth cap.
            if (depth >= maxDepth)
                return JsonValue.Create($"<max-depth {type.Name}>");

            // Reference cycle along the current path.
            if (!visited.Add(value))
                return JsonValue.Create($"<cycle {type.Name}>");

            try
            {
                return value switch
                {
                    IDictionary dictionary => WalkDictionary(dictionary, depth, maxDepth, visited),
                    IEnumerable enumerable => WalkEnumerable(enumerable, depth, maxDepth, visited),
                    _ => WalkObject(value, type, depth, maxDepth, visited),
                };
            }
            finally
            {
                visited.Remove(value);
            }
        }
        catch (Exception ex)
        {
            // A getter that threw, a type that refuses to serialise: never let the serialiser fail.
            return JsonValue.Create(SafeToString(value, ex));
        }
    }

    private static JsonObject WalkDictionary(IDictionary dictionary, int depth, int maxDepth, HashSet<object> visited)
    {
        var obj = new JsonObject();
        int count = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (count >= MaxCollectionItems)
            {
                obj["…"] = JsonValue.Create($"(+{dictionary.Count - count} more)");
                break;
            }
            string key = entry.Key?.ToString() ?? "null";
            obj[key] = Walk(entry.Value, depth + 1, maxDepth, visited);
            count++;
        }
        return obj;
    }

    private static JsonArray WalkEnumerable(IEnumerable enumerable, int depth, int maxDepth, HashSet<object> visited)
    {
        var array = new JsonArray();
        int count = 0;
        foreach (var item in enumerable)
        {
            if (count >= MaxCollectionItems)
            {
                array.Add(JsonValue.Create("<truncated, +more items>"));
                break;
            }
            array.Add(Walk(item, depth + 1, maxDepth, visited));
            count++;
        }
        return array;
    }

    private static JsonObject WalkObject(object value, Type type, int depth, int maxDepth, HashSet<object> visited)
    {
        var obj = new JsonObject();
        int members = 0;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
                continue;
            if (members++ >= MaxObjectMembers)
                break;
            obj[property.Name] = ReadMember(() => property.GetValue(value), depth, maxDepth, visited);
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (members++ >= MaxObjectMembers)
                break;
            obj[field.Name] = ReadMember(() => field.GetValue(value), depth, maxDepth, visited);
        }

        return obj;
    }

    private static JsonNode? ReadMember(Func<object?> getter, int depth, int maxDepth, HashSet<object> visited)
    {
        object? memberValue;
        try
        {
            memberValue = getter();
        }
        catch (Exception ex)
        {
            return JsonValue.Create($"<error: {(ex.InnerException ?? ex).Message}>");
        }
        return Walk(memberValue, depth + 1, maxDepth, visited);
    }

    private static string Truncate(string s) =>
        s.Length <= MaxStringLength
            ? s
            : string.Concat(s.AsSpan(0, MaxStringLength), $"…(truncated {s.Length - MaxStringLength} chars)");

    private static string SafeToString(object value, Exception ex)
    {
        try
        {
            return Truncate(value.ToString() ?? $"<{value.GetType().Name}: {ex.Message}>");
        }
        catch (Exception)
        {
            return $"<unserialisable {value.GetType().Name}>";
        }
    }

    private static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsPrimitive || t.IsEnum)
            return true;
        return t == typeof(decimal)
            || t == typeof(string)
            || t == typeof(Guid)
            || t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || t == typeof(DateOnly)
            || t == typeof(TimeOnly)
            || t == typeof(TimeSpan)
            || t == typeof(Uri)
            || t == typeof(Version);
    }
}
