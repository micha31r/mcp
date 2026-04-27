// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Mcp.Tools.Insights.Services;
using Xunit;

namespace Azure.Mcp.Tools.Insights.UnitTests.Services;

public class PropertyAggregatorTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Aggregate_SkipsRowsWithoutType()
    {
        var rows = new[]
        {
            Row("""{ "type": "" }"""),
            Row("""{ "name": "no-type" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Empty(result.ResourceTypes);
        Assert.Equal(0, result.ResourceGroupCount);
        Assert.Empty(result.FilteredAutoCreatedTypes);
    }

    [Fact]
    public void Aggregate_LowercasesType_AndCountsRows()
    {
        var rows = new[]
        {
            Row("""{ "type": "Microsoft.Storage/storageAccounts", "location": "eastus" }"""),
            Row("""{ "type": "MICROSOFT.STORAGE/storageAccounts", "location": "eastus" }"""),
            Row("""{ "type": "microsoft.storage/storageAccounts", "location": "westus" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Single(result.ResourceTypes);
        var entry = result.ResourceTypes["microsoft.storage/storageaccounts"];
        Assert.Equal("microsoft.storage/storageaccounts", entry.ArmResourceType);
        Assert.Equal(3, entry.TotalCount);
        var locationCounts = (JsonObject)entry.PropertyAggregations["location"]!;
        Assert.Equal(2, (int)locationCounts["eastus"]!);
        Assert.Equal(1, (int)locationCounts["westus"]!);
    }

    [Fact]
    public void Aggregate_EmitsTopThreeOnly_OrderedByCountThenKey()
    {
        var rows = new List<JsonElement>();
        for (var i = 0; i < 5; i++)
        {
            rows.Add(Row("""{ "type": "x/y", "location": "a" }"""));
        }
        for (var i = 0; i < 4; i++)
        {
            rows.Add(Row("""{ "type": "x/y", "location": "b" }"""));
        }
        for (var i = 0; i < 3; i++)
        {
            rows.Add(Row("""{ "type": "x/y", "location": "c" }"""));
        }
        rows.Add(Row("""{ "type": "x/y", "location": "d" }"""));

        var result = PropertyAggregator.Aggregate(rows);

        var locations = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["location"]!;
        Assert.Equal(3, locations.Count);
        Assert.Collection(
            locations,
            kv => Assert.Equal("a", kv.Key),
            kv => Assert.Equal("b", kv.Key),
            kv => Assert.Equal("c", kv.Key));
    }

    [Fact]
    public void Aggregate_TopThree_TieBreaksByOrdinalKey()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "location": "zeta" }"""),
            Row("""{ "type": "x/y", "location": "alpha" }"""),
            Row("""{ "type": "x/y", "location": "beta" }"""),
            Row("""{ "type": "x/y", "location": "gamma" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var locations = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["location"]!;
        Assert.Equal(3, locations.Count);
        Assert.Collection(
            locations,
            kv => Assert.Equal("alpha", kv.Key),
            kv => Assert.Equal("beta", kv.Key),
            kv => Assert.Equal("gamma", kv.Key));
    }

    [Fact]
    public void Aggregate_ListsBecomeScalarPlaceholder()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "properties": { "items": [1, 2, 3] } }"""),
            Row("""{ "type": "x/y", "properties": { "items": [1, 2, 3] } }"""),
            Row("""{ "type": "x/y", "properties": { "items": [9] } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var items = (JsonObject)((JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["properties"]!)["items"]!;
        Assert.Equal(2, (int)items["<list[3]>"]!);
        Assert.Equal(1, (int)items["<list[1]>"]!);
    }

    [Fact]
    public void Aggregate_RespectsMaxDepth()
    {
        // depth 1: properties; 2: a; 3: b; 4: c; 5: d (allowed). depth-6 (e) must be dropped.
        // Include a sibling scalar at c so the c branch is materialized.
        var rows = new[]
        {
            Row("""{ "type": "x/y", "properties": { "a": { "b": { "c": { "shallow": "kept", "d": { "e": "tooDeep" } } } } } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var node = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["properties"]!;
        node = (JsonObject)node["a"]!;
        node = (JsonObject)node["b"]!;
        node = (JsonObject)node["c"]!;
        Assert.True(node.ContainsKey("shallow"));
        // At depth 5 we hit the cap; recursion must stop before walking "d" further.
        Assert.False(node.ContainsKey("d") && node["d"] is JsonObject deeper && deeper.ContainsKey("e"));
    }

    [Fact]
    public void Aggregate_FirstSeenShapeWins_OnTypeCollision()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "properties": { "kind": "v1" } }"""),
            Row("""{ "type": "x/y", "properties": { "kind": { "nested": "v2" } } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var props = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["properties"]!;
        // First-seen was a scalar leaf, so "kind" must remain a counter object with v1=1
        var kind = (JsonObject)props["kind"]!;
        Assert.Equal(1, (int)kind["v1"]!);
        Assert.False(kind.ContainsKey("nested"));
    }

    [Fact]
    public void Aggregate_NullValuesAreSkipped()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "properties": { "k": null, "j": "v" } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);
        var props = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["properties"]!;
        Assert.False(props.ContainsKey("k"));
        Assert.True(props.ContainsKey("j"));
    }

    [Fact]
    public void Aggregate_DropsArmChildTypes()
    {
        // A child type has >=2 slashes (e.g. ".../virtualmachines/extensions").
        var rows = new[]
        {
            Row("""{ "type": "Microsoft.Compute/virtualMachines", "resourceGroup": "rg1", "location": "eastus" }"""),
            Row("""{ "type": "Microsoft.Compute/virtualMachines/extensions", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "Microsoft.Storage/storageAccounts/blobServices/containers", "resourceGroup": "rg1" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Single(result.ResourceTypes);
        Assert.True(result.ResourceTypes.ContainsKey("microsoft.compute/virtualmachines"));
        // The two child types are dropped and surfaced via FilteredAutoCreatedTypes (sorted, distinct).
        Assert.Equal(
            new[]
            {
                "microsoft.compute/virtualmachines/extensions",
                "microsoft.storage/storageaccounts/blobservices/containers",
            },
            result.FilteredAutoCreatedTypes);
    }

    [Fact]
    public void Aggregate_DropsDenylistedAutoCreatedTypes()
    {
        var rows = new[]
        {
            Row("""{ "type": "microsoft.alertsmanagement/smartdetectoralertrules", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "microsoft.insights/components", "resourceGroup": "rg1" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Single(result.ResourceTypes);
        Assert.True(result.ResourceTypes.ContainsKey("microsoft.insights/components"));
        Assert.Contains("microsoft.alertsmanagement/smartdetectoralertrules", result.FilteredAutoCreatedTypes);
    }

    [Fact]
    public void Aggregate_TracksDistinctResourceGroupCount_CaseInsensitive()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "x/y", "resourceGroup": "RG1" }"""),
            Row("""{ "type": "x/y", "resourceGroup": "rg-2" }"""),
            Row("""{ "type": "x/y", "resourceGroup": "RG-3" }"""),
            Row("""{ "type": "x/y" }"""), // no resourceGroup; ignored for the count.
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Equal(3, result.ResourceGroupCount);
    }

    [Fact]
    public void Aggregate_ResourceGroupCount_ExcludesFilteredRows()
    {
        // Filtered (auto-created) rows must not contribute to the resource-group count.
        var rows = new[]
        {
            Row("""{ "type": "Microsoft.Compute/virtualMachines/extensions", "resourceGroup": "rg-only-on-extension" }"""),
            Row("""{ "type": "x/y", "resourceGroup": "rg-real" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Equal(1, result.ResourceGroupCount);
    }

    [Theory]
    [InlineData("microsoft.compute/virtualmachines/extensions", true)]
    [InlineData("microsoft.storage/storageaccounts/blobservices/containers", true)]
    [InlineData("microsoft.alertsmanagement/smartdetectoralertrules", true)]
    [InlineData("microsoft.compute/virtualmachines", false)]
    [InlineData("microsoft.storage/storageaccounts", false)]
    [InlineData("", false)]
    public void IsAutoCreated_MatchesPythonPredicate(string armType, bool expected)
    {
        Assert.Equal(expected, PropertyAggregator.IsAutoCreated(armType));
    }
}
