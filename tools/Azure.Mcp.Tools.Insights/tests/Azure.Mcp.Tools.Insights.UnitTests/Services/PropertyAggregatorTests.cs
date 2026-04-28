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
    public void Aggregate_LowercasesType_AndCountsRows_AndEmitsFractions()
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
        var locationFractions = (JsonObject)entry.PropertyAggregations["location"]!;
        Assert.Equal(0.667, (double)locationFractions["eastus"]!, 3);
        Assert.Equal(0.333, (double)locationFractions["westus"]!, 3);
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
    public void Aggregate_NullValuesAreSkipped()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "properties": { "kind": null, "location": "eastus" } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);
        var props = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["properties"]!;
        Assert.False(props.ContainsKey("kind"));
        Assert.True(props.ContainsKey("location"));
    }

    [Fact]
    public void Aggregate_DropsArmChildTypes_AndSurfacesThem()
    {
        var rows = new[]
        {
            Row("""{ "type": "Microsoft.Compute/virtualMachines", "resourceGroup": "rg1", "location": "eastus" }"""),
            Row("""{ "type": "Microsoft.Compute/virtualMachines/extensions", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "Microsoft.Storage/storageAccounts/blobServices/containers", "resourceGroup": "rg1" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Single(result.ResourceTypes);
        Assert.True(result.ResourceTypes.ContainsKey("microsoft.compute/virtualmachines"));
        Assert.Equal(
            new[]
            {
                "microsoft.compute/virtualmachines/extensions",
                "microsoft.storage/storageaccounts/blobservices/containers",
            },
            result.FilteredAutoCreatedTypes);
    }

    [Fact]
    public void Aggregate_DropsAutoCreatedDenylistedTypes()
    {
        var rows = new[]
        {
            Row("""{ "type": "microsoft.alertsmanagement/smartdetectoralertrules", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "microsoft.security/automations", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "microsoft.network/networkwatchers", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "microsoft.storage/storageaccounts", "resourceGroup": "rg1" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Single(result.ResourceTypes);
        Assert.True(result.ResourceTypes.ContainsKey("microsoft.storage/storageaccounts"));
        Assert.Contains("microsoft.alertsmanagement/smartdetectoralertrules", result.FilteredAutoCreatedTypes);
        Assert.Contains("microsoft.security/automations", result.FilteredAutoCreatedTypes);
        Assert.Contains("microsoft.network/networkwatchers", result.FilteredAutoCreatedTypes);
    }

    [Fact]
    public void Aggregate_DropsInternalMsRpPrefixes()
    {
        var rows = new[]
        {
            Row("""{ "type": "microsoft.portalservices/dashboards", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "microsoft.cloudtest/foo", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "microsoft.storage/storageaccounts", "resourceGroup": "rg1" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Single(result.ResourceTypes);
        Assert.True(result.ResourceTypes.ContainsKey("microsoft.storage/storageaccounts"));
    }

    [Fact]
    public void Aggregate_DropsMarketplaceTypes()
    {
        var rows = new[]
        {
            Row("""{ "type": "microsoft.solutions/applications", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "microsoft.saas/resources", "resourceGroup": "rg1" }"""),
            Row("""{ "type": "microsoft.storage/storageaccounts", "resourceGroup": "rg1" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Single(result.ResourceTypes);
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
            Row("""{ "type": "x/y" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        Assert.Equal(3, result.ResourceGroupCount);
    }

    [Fact]
    public void Aggregate_WalksKindAtTopLevel()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "kind": "StorageV2" }"""),
            Row("""{ "type": "x/y", "kind": "storagev2" }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var kindFractions = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["kind"]!;
        Assert.Equal(1.0, (double)kindFractions["storagev2"]!);
    }

    [Fact]
    public void Aggregate_WalksIdentityType_Only()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "identity": { "type": "SystemAssigned", "principalId": "should-not-leak" } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var fractions = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["identity"]!;
        Assert.Equal(1.0, (double)fractions["SystemAssigned"]!);
    }

    [Fact]
    public void Aggregate_PropertyLeafWhitelist_GatesAggregation()
    {
        // "foo" is not in PROPERTY_LEAF_WHITELIST; "location" is.
        var rows = new[]
        {
            Row("""{ "type": "x/y", "properties": { "foo": "bar", "location": "eastus" } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var props = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["properties"]!;
        Assert.False(props.ContainsKey("foo"));
        Assert.True(props.ContainsKey("location"));
    }

    [Fact]
    public void Aggregate_PiiKeys_AreScrubbed()
    {
        var rows = new[]
        {
            Row("""{ "type": "x/y", "properties": { "primaryKey": "secret-but-also-not-whitelisted", "accessTier": "Hot" } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var props = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["properties"]!;
        Assert.False(props.ContainsKey("primaryKey"));
        Assert.False(props.ContainsKey("primarykey"));
        Assert.True(props.ContainsKey("accesstier"));
    }

    [Fact]
    public void Aggregate_PiiValues_AreScrubbed()
    {
        // "size" leaf is whitelisted, but values matching IPv4/GUID/URL/email/JWT/base64 must be skipped.
        var rows = new[]
        {
            Row("""{ "type": "x/y", "sku": { "size": "192.168.1.1" } }"""),
            Row("""{ "type": "x/y", "sku": { "size": "12345678-1234-1234-1234-123456789012" } }"""),
            Row("""{ "type": "x/y", "sku": { "size": "https://example.com" } }"""),
            Row("""{ "type": "x/y", "sku": { "size": "user@example.com" } }"""),
            Row("""{ "type": "x/y", "sku": { "size": "Standard" } }"""),
        };

        var result = PropertyAggregator.Aggregate(rows);

        var sku = (JsonObject)result.ResourceTypes["x/y"].PropertyAggregations["sku"]!;
        var size = (JsonObject)sku["size"]!;
        Assert.Single(size);
        Assert.True(size.ContainsKey("Standard"));
    }

    [Theory]
    [InlineData("microsoft.compute/virtualmachines/extensions", true)]
    [InlineData("microsoft.alertsmanagement/smartdetectoralertrules", true)]
    [InlineData("microsoft.solutions/applications", true)]
    [InlineData("microsoft.cloudtest/foo", true)]
    [InlineData("microsoft.storage/storageaccounts", false)]
    [InlineData("", false)]
    public void IsAutoCreated_MatchesPythonPredicate(string armType, bool expected)
    {
        Assert.Equal(expected, PropertyAggregator.IsAutoCreated(armType));
    }
}
