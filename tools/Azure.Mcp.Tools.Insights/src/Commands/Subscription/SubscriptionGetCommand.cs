// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Mcp.Core.Commands.Subscription;
using Azure.Mcp.Tools.Insights.Models;
using Azure.Mcp.Tools.Insights.Options.Subscription;
using Azure.Mcp.Tools.Insights.Services;
using Azure.Mcp.Tools.Insights.Services.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Commands;
using Microsoft.Mcp.Core.Models.Command;
using Microsoft.Mcp.Core.Models.Option;

namespace Azure.Mcp.Tools.Insights.Commands.Subscription;

public sealed class SubscriptionGetCommand(
    ILogger<SubscriptionGetCommand> logger,
    IInsightsService insightsService,
    ISamplingService samplingService)
    : SubscriptionCommand<SubscriptionGetOptions>()
{
    private const string CommandTitle = "Get Subscription Insights";
    private const int SamplingMaxTokens = 4000;

    private const string SystemPrompt = """
        # Role and Objective
        You are an expert Azure Insight Agent. Your mission is to analyze the user's existing
        infrastructure and produce insights that inform downstream infrastructure plan generation.

        # Process
        1. Analyze the provided aggregated infrastructure data and derive insights from
           dominant patterns in the user's existing infrastructure.
        2. Re-examine the insights you produced. Check them for completeness and accuracy,
           and improve any that fall short.
        3. Once satisfied, return the insights array.

        # Insight Guidelines
        When selecting resource properties to base insights on:
        - Only consider properties that represent explicit user decisions affecting design.
        - Never include properties involving runtime, versions, implementation details, app
          settings, default values, operational settings, or boilerplate configurations.
        - Never include instance-specific properties of a resource.

        ### Structure of an Insight
        Each insight must contain three parts: an observed pattern, the reasoning behind it,
        and a planning implication.
        - The reasoning must be grounded in factual information from the data. Do not make
          assumptions.
        - The planning implication must state concrete actions or decisions for infra
          planning.
        - The reasoning must clearly connect the observed pattern to the planning implication.

        ### Filtering
        Use the following areas as a guide when deciding which resource properties are
        meaningful:
        - Region
        - Resource pairing
        - Security posture
        - Cost
        - Naming and tagging conventions
        - Azure policies

        # Rules
        - You are an internal agent focused solely on gathering infrastructure insights.
        - Return your Insights object when complete.

        # Output
        Return ONLY a JSON array of single-sentence insight strings, structured as:
        "[observed pattern]: [reasoning] [planning implication]".

        Example: ["Insight 1", "Insight 2", "Insight 3"]
        """;

    private readonly ILogger<SubscriptionGetCommand> _logger = logger;
    private readonly IInsightsService _insightsService = insightsService;
    private readonly ISamplingService _samplingService = samplingService;

    public override string Id => "f87c9d7a-3e2f-4b9a-9d51-2c2bf7d4a6e1";

    public override string Name => "get";

    public override string Description =>
        """
        Derives architectural insights for an Azure subscription. Queries Azure Resource
        Graph for all user-managed resources in the subscription, aggregates property value
        frequencies per resource type (region, sku, security-related properties, tagging
        conventions, etc.), and uses MCP sampling to ask the host LLM to produce a JSON
        array of single-sentence insights describing the dominant patterns.

        Required parameters:
        - subscription: Subscription ID or name to analyze.

        Returns an object with an `insights` array of human-readable insight sentences.

        Note: This command relies on the MCP sampling capability and is only available when
        the server is invoked over a transport whose client supports sampling (e.g. an MCP
        host). It is not available from the plain CLI invocation path.
        """;

    public override string Title => CommandTitle;

    public override ToolMetadata Metadata => new()
    {
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        ReadOnly = true,
        LocalRequired = false,
        Secret = false,
    };

    public override async Task<CommandResponse> ExecuteAsync(
        CommandContext context,
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        if (!Validate(parseResult.CommandResult, context.Response).IsValid)
        {
            return context.Response;
        }

        var options = BindOptions(parseResult);

        if (context.McpServer is null)
        {
            context.Response.Status = System.Net.HttpStatusCode.BadRequest;
            context.Response.Message = "Subscription insights require MCP sampling, which is not available in the current transport mode. Invoke this command from an MCP client that supports sampling.";
            return context.Response;
        }

        try
        {
            var aggregation = await _insightsService.AggregateSubscriptionAsync(
                options.Subscription!,
                options.Tenant,
                options.RetryPolicy,
                cancellationToken);

            var aggregationsJson = SerializeAggregations(aggregation);
            var userPrompt = "# Aggregated Infrastructure Data\n" + aggregationsJson;

            var sampled = await _samplingService.SampleTextAsync(
                context.McpServer,
                SystemPrompt,
                userPrompt,
                SamplingMaxTokens,
                cancellationToken);

            var result = ParseSamplingResponse(sampled);

            context.Response.Results = ResponseResult.Create(
                new SubscriptionGetCommandResult(result),
                InsightsJsonContext.Default.SubscriptionGetCommandResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error deriving subscription insights. Subscription: {Subscription}.",
                options.Subscription);
            HandleException(context, ex);
        }

        return context.Response;
    }

    internal static string SerializeAggregations(SubscriptionAggregation aggregation)
    {
        var resourceTypes = new JsonObject();
        foreach (var (key, value) in aggregation.ResourceTypes)
        {
            resourceTypes[key] = new JsonObject
            {
                ["armResourceType"] = value.ArmResourceType,
                ["totalCount"] = value.TotalCount,
                ["propertyAggregations"] = value.PropertyAggregations.DeepClone(),
            };
        }

        var filteredTypes = new JsonArray();
        foreach (var t in aggregation.FilteredAutoCreatedTypes)
        {
            filteredTypes.Add((JsonNode?)JsonValue.Create(t));
        }

        var root = new JsonObject
        {
            ["resourceGroupCount"] = aggregation.ResourceGroupCount,
            ["filteredAutoCreatedTypes"] = filteredTypes,
            ["resourceTypes"] = resourceTypes,
        };
        return root.ToJsonString();
    }

    internal static InsightsResult ParseSamplingResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new InsightsResult([], "Sampling returned an empty response.");
        }

        var trimmed = text.Trim();

        if (TryParseInsightsArray(trimmed, out var insights))
        {
            return new InsightsResult(insights);
        }

        var arrayStart = trimmed.IndexOf('[');
        var arrayEnd = trimmed.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
        {
            var slice = trimmed.Substring(arrayStart, arrayEnd - arrayStart + 1);
            if (TryParseInsightsArray(slice, out insights))
            {
                return new InsightsResult(insights);
            }
        }

        return new InsightsResult(
            [trimmed],
            "Sampling response could not be parsed as a JSON array of insights; returning raw text.");
    }

    private static bool TryParseInsightsArray(string json, out IReadOnlyList<string> insights)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(json, InsightsJsonContext.Default.StringArray);
            if (parsed is not null)
            {
                insights = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        insights = [];
        return false;
    }

    internal record SubscriptionGetCommandResult(InsightsResult Result);
}
