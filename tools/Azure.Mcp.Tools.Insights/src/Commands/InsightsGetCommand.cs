// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Mcp.Tools.Insights.Options;
using Azure.Mcp.Tools.Insights.Services;
using Azure.Mcp.Tools.Insights.Services.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Commands;
using Microsoft.Mcp.Core.Extensions;
using Microsoft.Mcp.Core.Helpers;
using Microsoft.Mcp.Core.Models.Command;
using Microsoft.Mcp.Core.Models.Option;

namespace Azure.Mcp.Tools.Insights.Commands;

public sealed class InsightsGetCommand(
    ILogger<InsightsGetCommand> logger,
    IInsightsService insightsService,
    ISamplingService samplingService)
    : GlobalCommand<InsightsGetOptions>()
{
    private const string CommandTitle = "Get Azure Infrastructure Insights";
    private const int SamplingMaxTokens = 4000;

    private const string SystemPrompt = """
        # Role and Objective
        You are an expert Azure Insight Agent. Your mission is to analyze the user's existing infrastructure data and produce insights that inform downstream infrastructure plan generation.

        # Input Data Format

        You will receive a JSON object with two sections:

        ## userQuery
        The user's infrastructure request. Focus your insights on patterns most relevant to this request, but draw from all tenant-wide data.

        ## resourceContext
        Per-resource-type property aggregations across the tenant.
        - Each key is an ARM resource type (e.g. "microsoft.storage/storageaccounts").
        - `totalCount`: how many instances of this type exist in the tenant.
        - `propertyAggregations`: nested object where each leaf is a dict of `{value: fraction}`.
          - `fraction` is the share of instances that have that value (0.0-1.0). The top 3 values are shown; the implied remainder is `1 - sum(fractions)`.
          - Example: `"location": {"eastus": 0.6, "westus2": 0.3}` means 60% of instances are in eastus, 30% in westus2, and 10% elsewhere.

        # Process
        1. Analyze the property aggregations to identify architectural conventions (SKU choices, security posture, region preferences, redundancy settings).
        2. Identify resource types that are relevant to the user's query and highlight their conventions.
        3. Include important tenant-wide conventions even if not directly query-related.
        4. Re-examine your insights for completeness and accuracy.

        # Insight Guidelines
        When selecting resource properties to base insights on:
        - Only consider properties that represent explicit user decisions affecting design.
        - Never include properties involving runtime, versions, implementation details, app settings, default values, operational settings, or boilerplate configurations.
        - Never include instance-specific properties of a resource.

        ### Structure of an Insight

        Each insight must contain three parts: an observed pattern, the reasoning behind it, and a planning implication.
        - The reasoning must be grounded in factual information from the data. Do not make assumptions.
        - The planning implication must state concrete actions or decisions for infra planning that align with the user's requirements.
        - The reasoning must clearly connect the observed pattern to the planning implication.

        ### Filtering

        Use the following areas as a guide when deciding which resource properties are meaningful:
        - Region
        - Resource pairing
        - Security posture
        - Cost
        - Naming and tagging conventions
        - Azure policies

        # Output

        Return a JSON object with an "insights" key containing an array of insight strings.

        ```json
        {
          "insights": [
            "Insight 1",
            "Insight 2",
            "Insight 3"
          ]
        }
        ```

        Each insight must be a single sentence with this structure: "[observed pattern]: [reasoning] [planning implication]".
        """;

    private readonly ILogger<InsightsGetCommand> _logger = logger;
    private readonly IInsightsService _insightsService = insightsService;
    private readonly ISamplingService _samplingService = samplingService;

    public override string Id => "8d6ac0a4-1b3e-4d2c-8d2a-3a8c1c52cf94";

    public override string Name => "get";

    public override string Description =>
        """
        Derives architectural insights from existing Azure infrastructure. Queries Azure
        Resource Graph for user-managed resources, filters out auto-created / marketplace /
        internal-1P plumbing, aggregates per-resource-type property value frequencies as
        fractions of the top-3 most-common observed values, and uses MCP sampling to ask the
        host LLM to return single-sentence insights describing dominant patterns.

        Optional parameters:
        - subscription: Subscription ID or name. When supplied, insights are scoped to that
          subscription. When omitted, insights are derived across every accessible subscription
          in the tenant.
        - query: Free-form description of the user's infrastructure intent. When provided,
          insights are tailored to this scenario; when omitted, generic patterns are returned.

        Returns an array of insight strings.

        Note: This command relies on the MCP sampling capability and is only available when
        the server is invoked over a transport whose client supports sampling.
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

    protected override void RegisterOptions(Command command)
    {
        base.RegisterOptions(command);
        // --subscription is optional here: presence selects subscription scope, absence selects tenant scope.
        command.Options.Add(OptionDefinitions.Common.Subscription);
        command.Options.Add(InsightsOptionDefinitions.Query.AsOptional());
    }

    protected override InsightsGetOptions BindOptions(ParseResult parseResult)
    {
        var options = base.BindOptions(parseResult);
        // Read the raw value (not CommandHelper.GetSubscription, which falls back to env/CLI defaults
        // and would defeat implicit tenant-scope detection).
        var subscription = parseResult.GetValueOrDefault<string>(OptionDefinitions.Common.SubscriptionName);
        if (!string.IsNullOrEmpty(subscription))
        {
            options.Subscription = subscription.Trim('"', '\'');
        }
        options.Query = parseResult.GetValueOrDefault<string>(InsightsOptionDefinitions.QueryName);
        return options;
    }

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
            context.Response.Message = "Insights require MCP sampling, which is not available in the current transport mode. Invoke this command from an MCP client that supports sampling.";
            return context.Response;
        }

        try
        {
            var aggregation = string.IsNullOrEmpty(options.Subscription)
                ? await _insightsService.AggregateTenantAsync(options.Tenant, options.RetryPolicy, cancellationToken)
                : await _insightsService.AggregateSubscriptionAsync(options.Subscription, options.Tenant, options.RetryPolicy, cancellationToken);

            var payloadJson = BuildPayload(aggregation, options.Query);

            var sampled = await _samplingService.SampleTextAsync(
                context.McpServer,
                SystemPrompt,
                payloadJson,
                SamplingMaxTokens,
                cancellationToken);

            var insights = ParseInsights(sampled);

            context.Response.Results = ResponseResult.Create(
                new InsightsGetCommandResult(insights),
                InsightsJsonContext.Default.InsightsGetCommandResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error deriving insights. Subscription: {Subscription}.",
                options.Subscription);
            HandleException(context, ex);
        }

        return context.Response;
    }

    /// <summary>
    /// Builds the JSON payload sent to the LLM:
    /// <c>{ userQuery?, resourceContext: { subscriptionCount, resourceGroupCount, resourceTypes } }</c>.
    /// </summary>
    internal static string BuildPayload(SubscriptionAggregation aggregation, string? userQuery)
    {
        ArgumentNullException.ThrowIfNull(aggregation);

        var resourceTypes = new JsonObject();
        foreach (var kvp in aggregation.ResourceTypes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            resourceTypes[kvp.Key] = new JsonObject
            {
                ["totalCount"] = kvp.Value.TotalCount,
                ["propertyAggregations"] = kvp.Value.PropertyAggregations.DeepClone(),
            };
        }

        var resourceContext = new JsonObject
        {
            ["subscriptionCount"] = aggregation.SubscriptionCount,
            ["resourceGroupCount"] = aggregation.ResourceGroupCount,
            ["resourceTypes"] = resourceTypes,
        };

        if (string.IsNullOrWhiteSpace(userQuery))
        {
            var root = new JsonObject
            {
                ["resourceContext"] = resourceContext,
            };
            return root.ToJsonString();
        }

        // userQuery first to match the expected payload order.
        var ordered = new JsonObject
        {
            ["userQuery"] = userQuery.Trim(),
            ["resourceContext"] = resourceContext,
        };
        return ordered.ToJsonString();
    }

    /// <summary>
    /// Parses the LLM response as <c>{ "insights": [...] }</c>, stripping a surrounding markdown
    /// code fence if present. Throws on malformed input so that the caller's <c>HandleException</c>
    /// returns a 500 with the underlying parse error.
    /// </summary>
    internal static IReadOnlyList<string> ParseInsights(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Sampling returned an empty response.");
        }

        var json = StripCodeFence(text.Trim());

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("insights", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Sampling response did not contain an 'insights' array.");
        }

        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    list.Add(s);
                }
            }
        }
        return list;
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var newline = text.IndexOf('\n');
        if (newline < 0)
        {
            return text;
        }

        var inner = text[(newline + 1)..];
        if (inner.EndsWith("```", StringComparison.Ordinal))
        {
            inner = inner[..^3];
        }
        return inner.Trim();
    }

    internal record InsightsGetCommandResult(IReadOnlyList<string> Insights);
}
