// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Azure.Mcp.Tools.Insights.Commands;
using Azure.Mcp.Tools.Insights.Services;
using Azure.Mcp.Tools.Insights.Services.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Mcp.Core.Options;
using Microsoft.Mcp.Tests.Client;
using NSubstitute;
using Xunit;

namespace Azure.Mcp.Tools.Insights.UnitTests;

public class InsightsGetCommandTests : CommandUnitTestsBase<InsightsGetCommand, IInsightsService>
{
    public InsightsGetCommandTests()
    {
        Services.AddSingleton(Substitute.For<ISamplingService>());
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
    public async Task ExecuteAsync_TenantScope_AlsoRequiresSampling()
    {
        // No --subscription should still hit the sampling check before service calls.
        var response = await ExecuteCommandAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        await Service.DidNotReceive().AggregateSubscriptionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<RetryPolicyOptions?>(), Arg.Any<CancellationToken>());
        await Service.DidNotReceive().AggregateTenantAsync(
            Arg.Any<string?>(), Arg.Any<RetryPolicyOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_InitializesCommandCorrectly()
    {
        var command = Command.GetCommand();

        Assert.Equal("get", command.Name);
        Assert.NotEmpty(command.Description ?? string.Empty);
    }

    [Fact]
    public void ParseInsights_StripsMarkdownCodeFence()
    {
        var text = "```json\n[{\"id\":\"insight-001\",\"pattern\":\"p1\",\"implication\":\"i1\"}]\n```";

        var result = InsightsGetCommand.ParseInsights(text);

        Assert.Single(result);
        Assert.Equal("insight-001", result[0].Id);
        Assert.Equal("p1", result[0].Pattern);
        Assert.Equal("i1", result[0].Implication);
    }

    [Fact]
    public void ParseInsights_TopLevelArray_Succeeds()
    {
        var text = """
            [
              {"id":"insight-001","pattern":"p1","implication":"i1"},
              {"id":"insight-002","pattern":"p2","implication":"i2"}
            ]
            """;

        var result = InsightsGetCommand.ParseInsights(text);

        Assert.Equal(2, result.Count);
        Assert.Equal("insight-002", result[1].Id);
        Assert.Equal("p2", result[1].Pattern);
        Assert.Equal("i2", result[1].Implication);
    }

    [Fact]
    public void ParseInsights_WrappedInsightsObject_Succeeds()
    {
        var text = """{"insights":[{"id":"insight-001","pattern":"p","implication":"i"}]}""";

        var result = InsightsGetCommand.ParseInsights(text);

        Assert.Single(result);
        Assert.Equal("insight-001", result[0].Id);
    }

    [Fact]
    public void ParseInsights_SkipsEntriesMissingRequiredFields()
    {
        var text = """
            [
              {"id":"insight-001","pattern":"p","implication":"i"},
              {"id":"insight-002","pattern":"p"},
              {"pattern":"p","implication":"i"},
              "string entry",
              {"id":"insight-003","pattern":"p","implication":"i"}
            ]
            """;

        var result = InsightsGetCommand.ParseInsights(text);

        Assert.Equal(2, result.Count);
        Assert.Equal("insight-001", result[0].Id);
        Assert.Equal("insight-003", result[1].Id);
    }

    [Fact]
    public void ParseInsights_EmptyOrWhitespace_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => InsightsGetCommand.ParseInsights("   "));
    }

    [Fact]
    public void ParseInsights_NotArrayOrInsightsWrapper_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => InsightsGetCommand.ParseInsights("""{"other": ["a"]}"""));
    }

    [Fact]
    public void ParseInsights_NotJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => InsightsGetCommand.ParseInsights("Not JSON at all."));
    }

    [Fact]
    public void BuildPayload_OmitsUserQuery_WhenNotProvided()
    {
        var aggregation = SampleAggregation(subscriptionCount: 1);

        var json = InsightsGetCommand.BuildPayload(aggregation, userQuery: null);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("userQuery", out _));
        var ctx = doc.RootElement.GetProperty("resourceContext");
        Assert.Equal(1, ctx.GetProperty("subscriptionCount").GetInt32());
        Assert.Equal(2, ctx.GetProperty("resourceGroupCount").GetInt32());
        var rt = ctx.GetProperty("resourceTypes").GetProperty("microsoft.storage/storageaccounts");
        Assert.Equal(3, rt.GetProperty("totalCount").GetInt32());
        Assert.False(ctx.TryGetProperty("filteredAutoCreatedTypes", out _));
    }

    [Fact]
    public void BuildPayload_IncludesUserQuery_WhenProvided()
    {
        var aggregation = SampleAggregation(subscriptionCount: 1);

        var json = InsightsGetCommand.BuildPayload(aggregation, userQuery: "  finance web app  ");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("finance web app", doc.RootElement.GetProperty("userQuery").GetString());
        Assert.True(doc.RootElement.TryGetProperty("resourceContext", out _));
    }

    [Fact]
    public void BuildPayload_ReflectsTenantSubscriptionCount()
    {
        var aggregation = SampleAggregation(subscriptionCount: 7);

        var json = InsightsGetCommand.BuildPayload(aggregation, userQuery: null);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(7, doc.RootElement.GetProperty("resourceContext").GetProperty("subscriptionCount").GetInt32());
    }

    private static SubscriptionAggregation SampleAggregation(int subscriptionCount)
    {
        var props = new System.Text.Json.Nodes.JsonObject
        {
            ["location"] = new System.Text.Json.Nodes.JsonObject { ["eastus"] = 1.0 },
        };
        var resourceTypes = new Dictionary<string, ResourceTypeAggregation>
        {
            ["microsoft.storage/storageaccounts"] = new(
                "microsoft.storage/storageaccounts",
                3,
                props),
        };
        return new SubscriptionAggregation(
            resourceTypes,
            SubscriptionCount: subscriptionCount,
            ResourceGroupCount: 2,
            FilteredAutoCreatedTypes: []);
    }
}
