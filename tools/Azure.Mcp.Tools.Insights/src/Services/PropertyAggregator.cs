// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Mcp.Tools.Insights.Services.Models;

namespace Azure.Mcp.Tools.Insights.Services;

/// <summary>
/// Aggregates property value frequencies from Azure Resource Graph rows.
/// Port of the Python prototype's <c>walk_properties</c> / <c>_insert_aggregation</c> /
/// <c>_emit_aggregation</c> / <c>aggregate_resources</c> helpers.
///
/// Behavior:
/// - For each row, walk <c>location</c>, <c>sku</c>, and <c>properties</c> recursively
///   (max depth = 5). Values deeper than the cap are dropped.
/// - Lists become a single scalar leaf <c>&lt;list[N]&gt;</c> capturing only the count.
/// - <c>null</c> values are skipped.
/// - On type collisions (scalar vs object at the same path) the first-seen shape wins;
///   the conflicting later value is dropped silently.
/// - Each scalar leaf emits the top-3 most-common observed values mapped to their counts.
/// - Resource type keys are lowercased.
/// </summary>
internal static class PropertyAggregator
{
    internal const int MaxPropertyDepth = 5;
    internal const int TopValuesPerLeaf = 3;

    // Drop ARM child types (>=2 slashes, e.g. ".../virtualmachines/extensions") as
    // auto-created plumbing. Mirrors DROP_ARM_CHILD_TYPES in the Python prototype.
    internal const bool DropArmChildTypes = true;

    // Denylist of top-level ARM types that Azure auto-provisions as a side effect of
    // creating something else (no design signal). Mirrors AUTO_CREATED_TYPES in Python.
    internal static readonly FrozenSet<string> AutoCreatedTypes = new[]
    {
        "microsoft.alertsmanagement/smartdetectoralertrules",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns true when the given lowercased ARM type should be dropped from
    /// aggregation as an auto-created side-effect resource.
    /// </summary>
    internal static bool IsAutoCreated(string armType)
    {
        if (string.IsNullOrEmpty(armType))
        {
            return false;
        }

        if (DropArmChildTypes)
        {
            int slashes = 0;
            for (int i = 0; i < armType.Length; i++)
            {
                if (armType[i] == '/' && ++slashes >= 2)
                {
                    return true;
                }
            }
        }

        return AutoCreatedTypes.Contains(armType);
    }

    /// <summary>
    /// Aggregate the given ARG rows into a <see cref="SubscriptionAggregation"/>.
    ///
    /// Rows whose ARM type matches <see cref="IsAutoCreated"/> are dropped before
    /// aggregation; the dropped types are surfaced via
    /// <see cref="SubscriptionAggregation.FilteredAutoCreatedTypes"/> for transparency.
    /// </summary>
    public static SubscriptionAggregation Aggregate(IEnumerable<JsonElement> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var byType = new Dictionary<string, ResourceState>(StringComparer.Ordinal);
        var filtered = new HashSet<string>(StringComparer.Ordinal);
        var resourceGroups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var typeStr = TryGetString(row, "type");
            if (string.IsNullOrEmpty(typeStr))
            {
                continue;
            }

            var typeKey = typeStr.ToLowerInvariant();

            if (IsAutoCreated(typeKey))
            {
                filtered.Add(typeKey);
                continue;
            }

            var rg = TryGetString(row, "resourceGroup");
            if (!string.IsNullOrEmpty(rg))
            {
                resourceGroups.Add(rg.ToLowerInvariant());
            }

            if (!byType.TryGetValue(typeKey, out var state))
            {
                state = new ResourceState();
                byType[typeKey] = state;
            }

            state.Count++;

            // Source object: location + sku + properties (matches Python prototype)
            foreach (var (path, value) in WalkRow(row))
            {
                Insert(state.Tree, path, value);
            }
        }

        var result = new Dictionary<string, ResourceTypeAggregation>(StringComparer.Ordinal);
        foreach (var (typeKey, state) in byType)
        {
            result[typeKey] = new ResourceTypeAggregation(
                typeKey,
                state.Count,
                Emit(state.Tree));
        }

        var filteredSorted = filtered
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        return new SubscriptionAggregation(result, resourceGroups.Count, filteredSorted);
    }

    private static IEnumerable<(IReadOnlyList<string> Path, string Value)> WalkRow(JsonElement row)
    {
        // location is a top-level scalar in ARG output
        var location = TryGetString(row, "location");
        if (!string.IsNullOrEmpty(location))
        {
            yield return (new[] { "location" }, location);
        }

        if (row.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in WalkObject(sku, new List<string> { "sku" }, depth: 2))
            {
                yield return pair;
            }
        }

        if (row.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in WalkObject(props, new List<string> { "properties" }, depth: 2))
            {
                yield return pair;
            }
        }
    }

    private static IEnumerable<(IReadOnlyList<string> Path, string Value)> WalkObject(
        JsonElement node,
        List<string> path,
        int depth)
    {
        foreach (var prop in node.EnumerateObject())
        {
            var newPath = new List<string>(path) { prop.Name };
            var value = prop.Value;

            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (depth >= MaxPropertyDepth)
                    {
                        continue;
                    }
                    foreach (var pair in WalkObject(value, newPath, depth + 1))
                    {
                        yield return pair;
                    }
                    break;
                case JsonValueKind.Array:
                    yield return (newPath, $"<list[{value.GetArrayLength()}]>");
                    break;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    continue;
                default:
                    yield return (newPath, ScalarToString(value));
                    break;
            }
        }
    }

    private static void Insert(JsonObject tree, IReadOnlyList<string> path, string value)
    {
        var cursor = tree;
        for (int i = 0; i < path.Count - 1; i++)
        {
            var key = path[i];
            if (!cursor.TryGetPropertyValue(key, out var existing) || existing is null)
            {
                var next = new JsonObject();
                cursor[key] = next;
                cursor = next;
            }
            else if (existing is JsonObject obj)
            {
                // If this interior node has already been finalized as a counter leaf,
                // first-seen shape wins — don't descend into it as if it were a branch.
                if (IsCounter(obj))
                {
                    return;
                }
                cursor = obj;
            }
            else
            {
                // Type collision: a scalar/leaf already lives here. First-seen shape wins.
                return;
            }
        }

        var leafKey = path[^1];
        if (!cursor.TryGetPropertyValue(leafKey, out var leaf) || leaf is null)
        {
            cursor[leafKey] = new JsonObject { [value] = JsonValue.Create(1) };
            return;
        }

        if (leaf is JsonObject leafCounter)
        {
            // Detect that leafCounter is a counter (all values are numeric primitives).
            // If a nested object already lives at this path, first-seen shape wins.
            if (!IsCounter(leafCounter))
            {
                return;
            }

            if (leafCounter.TryGetPropertyValue(value, out var cur) && cur is JsonValue jv && jv.TryGetValue<int>(out var n))
            {
                leafCounter[value] = JsonValue.Create(n + 1);
            }
            else
            {
                leafCounter[value] = JsonValue.Create(1);
            }
        }
        // else: scalar leaf collision, drop silently
    }

    private static bool IsCounter(JsonObject obj)
    {
        if (obj.Count == 0)
        {
            return false;
        }
        foreach (var kvp in obj)
        {
            if (kvp.Value is not JsonValue v || !v.TryGetValue<int>(out _))
            {
                return false;
            }
        }
        return true;
    }

    private static JsonObject Emit(JsonObject tree)
    {
        var result = new JsonObject();
        foreach (var kvp in tree)
        {
            if (kvp.Value is JsonObject child)
            {
                if (IsCounter(child))
                {
                    result[kvp.Key] = TopN(child, TopValuesPerLeaf);
                }
                else
                {
                    result[kvp.Key] = Emit(child);
                }
            }
        }
        return result;
    }

    private static JsonObject TopN(JsonObject counter, int n)
    {
        var ordered = counter
            .Where(kvp => kvp.Value is JsonValue jv && jv.TryGetValue<int>(out _))
            .Select(kvp => (Key: kvp.Key, Count: kvp.Value!.GetValue<int>()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .Take(n);

        var result = new JsonObject();
        foreach (var (key, count) in ordered)
        {
            result[key] = JsonValue.Create(count);
        }
        return result;
    }

    private static string ScalarToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText(),
    };

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private sealed class ResourceState
    {
        public int Count;
        public JsonObject Tree { get; } = new();
    }
}
