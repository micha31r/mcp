// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Mcp.Tools.Insights.Services.Models;

namespace Azure.Mcp.Tools.Insights.Services;

/// <summary>
/// Aggregates property value frequencies from Azure Resource Graph rows.
/// Faithful port of the Python prototype in <c>baseline.ipynb</c> cells 4-5
/// (filtering, walking, PII scrubbing, leaf whitelist, fraction emission).
///
/// Behavior summary:
/// - Drops "noise" resource types: AUTO_CREATED_TYPES, INTERNAL_MS_RP_PREFIXES,
///   AUTO_MANAGED_SUBRESOURCE_TYPES, MARKETPLACE_TYPES, plus ARM child types
///   (>= 2 slashes) for itemset-mining purposes.
/// - For each retained row, walks <c>location</c>, <c>kind</c>, <c>sku</c>,
///   <c>identity.type</c>, and <c>properties</c> (max depth = 5).
/// - Skips properties whose key matches the PII deny regex and scalar values
///   matching PII value patterns (IPv4/IPv6/GUID/URL/email/JWT/base64).
/// - Aggregates only leaves whose name is in <see cref="PropertyLeafWhitelist"/>.
/// - Emits the top-3 most-common values per leaf as fractions (count / total),
///   rounded to 3 decimals, ordered by descending count then ordinal key.
/// </summary>
internal static partial class PropertyAggregator
{
    internal const int MaxPropertyDepth = 5;
    internal const int TopValuesPerLeaf = 3;
    internal const bool DropArmChildTypes = true;

    // Mirrors AUTO_CREATED_TYPES from baseline.ipynb cell 4.
    internal static readonly FrozenSet<string> AutoCreatedTypes = new[]
    {
        "microsoft.alertsmanagement/smartdetectoralertrules",
        "microsoft.insights/actiongroups",
        "microsoft.alertsmanagement/prometheusrulegroups",
        "microsoft.security/automations",
        "microsoft.security/pricings",
        "microsoft.operationsmanagement/solutions",
        "microsoft.security/iotsecuritysolutions",
        "microsoft.network/networkwatchers",
        "microsoft.advisor/recommendations",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Mirrors INTERNAL_MS_RP_PREFIXES from baseline.ipynb cell 4.
    internal static readonly string[] InternalMsRpPrefixes =
    [
        "microsoft.portalservices/",
        "microsoft.cloudtest/",
        "microsoft.hydra/",
        "microsoft.swiftlet/",
        "microsoft.compute/swiftlets",
        "microsoft.fairfieldgardens/",
        "microsoft.footprintmonitoring/",
        "microsoft.saashub/",
        "microsoft.visualstudio/",
    ];

    // Mirrors AUTO_MANAGED_SUBRESOURCE_TYPES from baseline.ipynb cell 4.
    internal static readonly FrozenSet<string> AutoManagedSubresourceTypes = new[]
    {
        "microsoft.containerregistry/registries/replications",
        "microsoft.containerregistry/registries/webhooks",
        "microsoft.compute/capacityreservationgroups/capacityreservations",
        "microsoft.compute/hostgroups/hosts",
        "microsoft.compute/galleries/images/versions",
        "microsoft.network/networkmanagers/ipampools",
        "microsoft.network/networkmanagers/verifierworkspaces",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Mirrors MARKETPLACE_TYPES from baseline.ipynb cell 4.
    internal static readonly FrozenSet<string> MarketplaceTypes = new[]
    {
        "microsoft.solutions/applications",
        "microsoft.solutions/appliances",
        "microsoft.saas/resources",
        "microsoft.saashub/cloudservices",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Mirrors PROPERTY_LEAF_WHITELIST from baseline.ipynb cell 5.
    internal static readonly FrozenSet<string> PropertyLeafWhitelist = new[]
    {
        "location", "kind",
        "sku", "name", "tier", "family", "capacity", "size",
        "publicnetworkaccess", "restrictoutboundnetworkaccess",
        "publicnetworkaccessforingestion", "publicnetworkaccessforquery",
        "defaultaction", "bypass",
        "disablelocalauth", "enablerbacauthorization",
        "minimumtlsversion", "minimaltlsversion",
        "identity",
        "keysource", "enabledoubleencryption", "enablediskencryption",
        "infrastructureencryption", "requireinfrastructureencryption",
        "zoneredundant", "zoneredundancy", "redundancymode", "replication",
        "platformfaultdomaincount",
        "backupretentionintervalinhours", "backupintervalinminutes",
        "backupstorageredundancy",
        "softdeleteretentionindays", "enablesoftdelete", "enablepurgeprotection",
        "ostype", "hypervgeneration", "licensetype",
        "accesstier", "largefilesharesstate", "allowsharedkeyaccess",
        "enablehttpstrafficonly", "supportshttpstrafficonly",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Mirrors _KEY_DENY_RE from baseline.ipynb cell 5.
    [GeneratedRegex(
        @"(secret|password|credential|token|sas|certificate|thumbprint|fingerprint|connection|connstr|admin(istrator)?(user|login|name)|private(ip|key|address)|publicip|ipaddress|fqdn|hostname|host_name|endpoint|url|uri|email|mail|principalid|tenantid|subscriptionid|objectid|clientid|appid|customsubdomain|customdomain|key$|^key|accountkey|accesskey|primarykey|secondarykey|sharedkey)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyDenyRegex();

    // Mirrors _VALUE_DENY_PATTERNS from baseline.ipynb cell 5.
    private static readonly Regex[] ValueDenyPatterns =
    [
        new Regex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant),
        new Regex(@"^[0-9a-f:]{6,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex(@"^[0-9a-f]{8}-([0-9a-f]{4}-){3}[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex(@"^https?://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex(@"^[^@]+@[^@]+\.[^@]+$", RegexOptions.CultureInvariant),
        new Regex(@"^eyJ[A-Za-z0-9_-]+\.", RegexOptions.CultureInvariant),
        new Regex(@"^[A-Za-z0-9+/]{40,}={0,2}$", RegexOptions.CultureInvariant),
    ];

    private static bool IsPiiKey(string key) => KeyDenyRegex().IsMatch(key);

    private static bool IsPiiValue(string value)
    {
        for (int i = 0; i < ValueDenyPatterns.Length; i++)
        {
            if (ValueDenyPatterns[i].IsMatch(value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Mirrors Python <c>_is_noise</c>: drops a top-level resource type.</summary>
    internal static bool IsNoise(string armType)
    {
        if (string.IsNullOrEmpty(armType))
        {
            return false;
        }
        if (AutoCreatedTypes.Contains(armType)
            || AutoManagedSubresourceTypes.Contains(armType)
            || MarketplaceTypes.Contains(armType))
        {
            return true;
        }
        for (int i = 0; i < InternalMsRpPrefixes.Length; i++)
        {
            if (armType.StartsWith(InternalMsRpPrefixes[i], StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Mirrors Python <c>_is_auto_created</c>: superset of <see cref="IsNoise"/> that also
    /// drops ARM child types (>= 2 slashes) used for itemset mining.
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

        return IsNoise(armType);
    }

    /// <summary>
    /// Aggregate the given ARG rows into a <see cref="SubscriptionAggregation"/>.
    /// Rows whose ARM type matches <see cref="IsAutoCreated"/> are dropped before
    /// aggregation; their types are surfaced via
    /// <see cref="SubscriptionAggregation.FilteredAutoCreatedTypes"/> for diagnostics
    /// (this list is not forwarded to the LLM).
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
        // Top-level scalars: location, kind.
        var location = TryGetString(row, "location");
        if (!string.IsNullOrEmpty(location))
        {
            yield return (new[] { "location" }, location.ToLowerInvariant());
        }

        var kind = TryGetString(row, "kind");
        if (!string.IsNullOrEmpty(kind))
        {
            yield return (new[] { "kind" }, kind.ToLowerInvariant());
        }

        // sku: walk recursively under "sku".
        if (row.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in WalkObject(sku, new List<string> { "sku" }, depth: 1))
            {
                yield return pair;
            }
        }

        // identity: only the .type scalar (matches Python).
        if (row.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object)
        {
            var idType = TryGetString(identity, "type");
            if (!string.IsNullOrEmpty(idType))
            {
                yield return (new[] { "identity" }, idType);
            }
        }

        // properties: walk recursively under "properties".
        if (row.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in WalkObject(props, new List<string> { "properties" }, depth: 1))
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
        if (depth > MaxPropertyDepth)
        {
            yield break;
        }

        foreach (var prop in node.EnumerateObject())
        {
            var keyLower = prop.Name.ToLowerInvariant();
            if (IsPiiKey(keyLower))
            {
                continue;
            }

            var newPath = new List<string>(path) { keyLower };
            foreach (var pair in WalkValue(prop.Value, newPath, depth + 1))
            {
                yield return pair;
            }
        }
    }

    private static IEnumerable<(IReadOnlyList<string> Path, string Value)> WalkValue(
        JsonElement value,
        List<string> path,
        int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var pair in WalkObject(value, path, depth))
                {
                    yield return pair;
                }
                break;
            case JsonValueKind.Array:
                // Python iterates list elements with the same path; mirror that.
                foreach (var item in value.EnumerateArray())
                {
                    foreach (var pair in WalkValue(item, path, depth + 1))
                    {
                        yield return pair;
                    }
                }
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                yield break;
            default:
                var scalar = ScalarToString(value).Trim();
                if (!string.IsNullOrEmpty(scalar) && !IsPiiValue(scalar))
                {
                    yield return (path, scalar);
                }
                break;
        }
    }

    private static void Insert(JsonObject tree, IReadOnlyList<string> path, string value)
    {
        if (path.Count == 0)
        {
            return;
        }

        // Whitelist gate: only aggregate counters for whitelisted leaf names.
        var leafName = path[^1];
        if (!PropertyLeafWhitelist.Contains(leafName))
        {
            return;
        }

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
                if (IsCounter(obj))
                {
                    return;
                }
                cursor = obj;
            }
            else
            {
                return;
            }
        }

        if (!cursor.TryGetPropertyValue(leafName, out var leaf) || leaf is null)
        {
            cursor[leafName] = new JsonObject { [value] = JsonValue.Create(1) };
            return;
        }

        if (leaf is JsonObject leafCounter)
        {
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
                    var top = TopNFractions(child, TopValuesPerLeaf);
                    if (top.Count > 0)
                    {
                        result[kvp.Key] = top;
                    }
                }
                else
                {
                    var sub = Emit(child);
                    if (sub.Count > 0)
                    {
                        result[kvp.Key] = sub;
                    }
                }
            }
        }
        return result;
    }

    private static JsonObject TopNFractions(JsonObject counter, int n)
    {
        long total = 0;
        foreach (var kvp in counter)
        {
            if (kvp.Value is JsonValue jv && jv.TryGetValue<int>(out var c))
            {
                total += c;
            }
        }

        var result = new JsonObject();
        if (total <= 0)
        {
            return result;
        }

        var ordered = counter
            .Where(kvp => kvp.Value is JsonValue jv && jv.TryGetValue<int>(out _))
            .Select(kvp => (Key: kvp.Key, Count: kvp.Value!.GetValue<int>()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .Take(n);

        foreach (var (key, count) in ordered)
        {
            var fraction = Math.Round((double)count / total, 3, MidpointRounding.AwayFromZero);
            result[key] = JsonValue.Create(fraction);
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

    private static string? TryGetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private sealed class ResourceState
    {
        public int Count { get; set; }
        public JsonObject Tree { get; } = new JsonObject();
    }
}
