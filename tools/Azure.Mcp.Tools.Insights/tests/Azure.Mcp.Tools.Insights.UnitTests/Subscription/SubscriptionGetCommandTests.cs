// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Nodes;
using Azure.Mcp.Tools.Insights.Commands.Subscription;
using Azure.Mcp.Tools.Insights.Services;
using Azure.Mcp.Tools.Insights.Services.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Mcp.Tests.Client;
using NSubstitute;
using Xunit;

namespace Azure.Mcp.Tools.Insights.UnitTests.Subscription;

public class SubscriptionGetCommandTests : CommandUnitTestsBase<SubscriptionGetCommand, IInsightsService>
{
    public SubscriptionGetCommandTests()
        : base(services => services.AddSingleton(Substitute.For<ISamplingService>()))
    {
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsBadRequest_WhenSubscriptionMissing()
    {
        var response = await ExecuteCommandAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsBadRequest_WhenSamplingUnavailable()
    {
        // Default Context.McpServer is null in unit tests.
        var response = await ExecuteCommandAsync("--subscription", "sub123");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.NotNull(response.Message);
        Assert.Contains("sampling", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSamplingResponse_StrictJsonArray_Succeeds()
    {
        var text = "[\"insight one\", \"insight two\"]";

        var result = SubscriptionGetCommand.ParseSamplingResponse(text);

        Assert.Equal(2, result.Insights.Count);
        Assert.Equal("insight one", result.Insights[0]);
        Assert.Null(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_ExtractsArrayFromSurroundingText()
    {
        var text = "Sure! Here are the insights: [\"a\", \"b\", \"c\"] -- end.";

        var result = SubscriptionGetCommand.ParseSamplingResponse(text);

        Assert.Equal(3, result.Insights.Count);
        Assert.Equal(["a", "b", "c"], result.Insights);
        Assert.Null(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_FallsBackToRawText_WhenNotJson()
    {
        var text = "Not JSON at all.";

        var result = SubscriptionGetCommand.ParseSamplingResponse(text);

        Assert.Single(result.Insights);
        Assert.Equal("Not JSON at all.", result.Insights[0]);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_EmptyOrWhitespace_ReturnsEmptyWithNote()
    {
        var result = SubscriptionGetCommand.ParseSamplingResponse("   ");

        Assert.Empty(result.Insights);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void SerializeAggregations_ProducesStableShape()
    {
        var aggregation = new SubscriptionAggregation(
            new Dictionary<string, ResourceTypeAggregation>
            {
                ["microsoft.storage/storageaccounts"] = new(
                    "microsoft.storage/storageaccounts",
                    2,
                    new JsonObject
                    {
                        ["location"] = new JsonObject { ["eastus"] = 2 },
                    }),
            },
            ResourceGroupCount: 5,
            FilteredAutoCreatedTypes: new[]
            {
                "microsoft.alertsmanagement/smartdetectoralertrules",
                "microsoft.compute/virtualmachines/extensions",
            });

        var json = SubscriptionGetCommand.SerializeAggregations(aggregation);

        var parsed = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(5, (int)parsed["resourceGroupCount"]!);

        var filtered = parsed["filteredAutoCreatedTypes"]!.AsArray();
        Assert.Equal(2, filtered.Count);
        Assert.Equal("microsoft.alertsmanagement/smartdetectoralertrules", (string?)filtered[0]);
        Assert.Equal("microsoft.compute/virtualmachines/extensions", (string?)filtered[1]);

        var resourceTypes = parsed["resourceTypes"]!.AsObject();
        var entry = resourceTypes["microsoft.storage/storageaccounts"]!.AsObject();
        Assert.Equal("microsoft.storage/storageaccounts", (string?)entry["armResourceType"]);
        Assert.Equal(2, (int)entry["totalCount"]!);
        var location = entry["propertyAggregations"]!["location"]!.AsObject();
        Assert.Equal(2, (int)location["eastus"]!);
    }
}
