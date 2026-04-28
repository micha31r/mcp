// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Azure.Mcp.Tools.Insights.Commands;
using Azure.Mcp.Tools.Insights.Services;
using Azure.Mcp.Tools.Insights.Services.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Mcp.Tests.Client;
using NSubstitute;
using Xunit;

namespace Azure.Mcp.Tools.Insights.UnitTests;

public class InsightsGetCommandTests : CommandUnitTestsBase<InsightsGetCommand, IInsightsService>
{
    public InsightsGetCommandTests()
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
    public void Constructor_InitializesCommandCorrectly()
    {
        var command = Command.GetCommand();

        Assert.Equal("get", command.Name);
        Assert.NotEmpty(command.Description ?? string.Empty);
    }

    [Fact]
    public void ParseSamplingResponse_InsightsObject_Succeeds()
    {
        var text = """{"insights": ["one", "two", "three"]}""";

        var result = InsightsGetCommand.ParseSamplingResponse(text);

        Assert.Equal(3, result.Insights.Count);
        Assert.Equal(["one", "two", "three"], result.Insights);
        Assert.Null(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_StrictJsonArray_Succeeds()
    {
        var text = """["a", "b"]""";

        var result = InsightsGetCommand.ParseSamplingResponse(text);

        Assert.Equal(["a", "b"], result.Insights);
        Assert.Null(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_AlternateKeys_Succeeds()
    {
        var text = """{"items": ["x", "y"]}""";

        var result = InsightsGetCommand.ParseSamplingResponse(text);

        Assert.Equal(["x", "y"], result.Insights);
        Assert.Null(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_FirstListOfStrings_FallbackSucceeds()
    {
        // No recognised key, but value is a list of strings.
        var text = """{"unrecognised": ["alpha", "beta"]}""";

        var result = InsightsGetCommand.ParseSamplingResponse(text);

        Assert.Equal(["alpha", "beta"], result.Insights);
        Assert.Null(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_ExtractsObjectFromSurroundingText()
    {
        var text = """Here is the JSON: {"insights": ["only-one"]} -- end.""";

        var result = InsightsGetCommand.ParseSamplingResponse(text);

        Assert.Equal(["only-one"], result.Insights);
        Assert.Null(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_FallsBackToRawText_WhenNotJson()
    {
        var text = "Not JSON at all.";

        var result = InsightsGetCommand.ParseSamplingResponse(text);

        Assert.Single(result.Insights);
        Assert.Equal("Not JSON at all.", result.Insights[0]);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void ParseSamplingResponse_EmptyOrWhitespace_ReturnsEmptyWithNote()
    {
        var result = InsightsGetCommand.ParseSamplingResponse("   ");

        Assert.Empty(result.Insights);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void BuildPayload_OmitsUserQuery_WhenNotProvided()
    {
        var aggregation = SampleAggregation();

        var json = InsightsGetCommand.BuildPayload(aggregation, userQuery: null);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("userQuery", out _));
        var ctx = doc.RootElement.GetProperty("resourceContext");
        Assert.Equal(1, ctx.GetProperty("subscriptionCount").GetInt32());
        Assert.Equal(2, ctx.GetProperty("resourceGroupCount").GetInt32());
        var rt = ctx.GetProperty("resourceTypes").GetProperty("microsoft.storage/storageaccounts");
        Assert.Equal(3, rt.GetProperty("totalCount").GetInt32());
        // armResourceType / filteredAutoCreatedTypes must NOT be in the LLM payload.
        Assert.False(rt.TryGetProperty("armResourceType", out _));
        Assert.False(ctx.TryGetProperty("filteredAutoCreatedTypes", out _));
    }

    [Fact]
    public void BuildPayload_IncludesUserQuery_WhenProvided()
    {
        var aggregation = SampleAggregation();

        var json = InsightsGetCommand.BuildPayload(aggregation, userQuery: "  finance web app  ");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("finance web app", doc.RootElement.GetProperty("userQuery").GetString());
        Assert.True(doc.RootElement.TryGetProperty("resourceContext", out _));
    }

    private static SubscriptionAggregation SampleAggregation()
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
        return new SubscriptionAggregation(resourceTypes, ResourceGroupCount: 2, FilteredAutoCreatedTypes: []);
    }
}
