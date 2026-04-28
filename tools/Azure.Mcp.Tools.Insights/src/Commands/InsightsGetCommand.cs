// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Mcp.Core.Commands.Subscription;
using Azure.Mcp.Tools.Insights.Models;
using Azure.Mcp.Tools.Insights.Options;
using Azure.Mcp.Tools.Insights.Services;
using Azure.Mcp.Tools.Insights.Services.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Commands;
using Microsoft.Mcp.Core.Extensions;
using Microsoft.Mcp.Core.Models.Command;
using Microsoft.Mcp.Core.Models.Option;

namespace Azure.Mcp.Tools.Insights.Commands;

public sealed class InsightsGetCommand(
    ILogger<InsightsGetCommand> logger,
    IInsightsService insightsService,
    ISamplingService samplingService)
    : SubscriptionCommand<InsightsGetOptions>()
{
    private const string CommandTitle = "Get Azure Infrastructure Insights";
    private const int SamplingMaxTokens = 4000;

    // Verbatim port of INSIGHTS_PROMPT from baseline.ipynb cell 7.
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
          - `fraction` is the share of instances that have that value (0.0–1.0). The top 3 values are shown; the implied remainder is `1 - sum(fractions)`.
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
        Derives architectural insights from an Azure subscription's existing infrastructure.
        Queries Azure Resource Graph for all user-managed resources, filters out auto-created
        / marketplace / internal-1P plumbing, aggregates per-resource-type property value
        frequencies (region, sku, security posture, identity, redundancy, tagging conventions,
        etc.) as fractions of the top-3 most-common observed values, and uses MCP sampling to
        ask the host LLM to produce a JSON object of single-sentence insights describing the
        dominant patterns.

        Required parameters:
        - subscription: Subscription ID or name to analyze.

        Optional parameters:
        - query: Free-form description of the user's infrastructure intent. When provided,
          insights are tailored to be relevant to this scenario; when omitted the LLM returns
          generic tenant-wide architectural insights.

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

    protected override void RegisterOptions(Command command)
    {
        base.RegisterOptions(command);
        command.Options.Add(InsightsOptionDefinitions.Query.AsOptional());
    }

    protected override InsightsGetOptions BindOptions(ParseResult parseResult)
    {
        var options = base.BindOptions(parseResult);
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
            var aggregation = await _insightsService.AggregateSubscriptionAsync(
                options.Subscription!,
                options.Tenant,
                options.RetryPolicy,
                cancellationToken);

            var payloadJson = BuildPayload(aggregation, options.Query);

            var sampled = await _samplingService.SampleTextAsync(
                context.McpServer,
                SystemPrompt,
                payloadJson,
                SamplingMaxTokens,
                cancellationToken);

            var result = ParseSamplingResponse(sampled);

            context.Response.Results = ResponseResult.Create(
                new InsightsGetCommandResult(result),
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
    /// Builds the JSON payload sent to the LLM, mirroring the Python prototype's
    /// <c>{userQuery, resourceContext: {subscriptionCount, resourceGroupCount, resourceTypes}}</c>
    /// shape from <c>baseline.ipynb</c> cell 6. <paramref name="userQuery"/> is omitted when
    /// the caller did not supply <c>--query</c>.
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
            ["subscriptionCount"] = 1,
            ["resourceGroupCount"] = aggregation.ResourceGroupCount,
            ["resourceTypes"] = resourceTypes,
        };

        var root = new JsonObject
        {
            ["resourceContext"] = resourceContext,
        };

        if (!string.IsNullOrWhiteSpace(userQuery))
        {
            // Insert userQuery first so the field order matches the Python payload.
            var ordered = new JsonObject
            {
                ["userQuery"] = userQuery.Trim(),
                ["resourceContext"] = resourceContext.DeepClone(),
            };
            return ordered.ToJsonString();
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// Parses the LLM response, matching the Python prototype's fallback chain:
    /// 1. Top-level JSON array of strings.
    /// 2. JSON object with key <c>insights</c>/<c>Insights</c>/<c>items</c>/<c>results</c>.
    /// 3. JSON object whose first list-of-strings value contains the insights.
    /// 4. Otherwise, return the raw text as a single insight with a note.
    /// </summary>
    internal static InsightsResult ParseSamplingResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new InsightsResult([], "Sampling returned an empty response.");
        }

        var trimmed = text.Trim();
        var jsonSlice = ExtractJsonSlice(trimmed);

        if (jsonSlice is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonSlice);
                var insights = ExtractInsights(doc.RootElement);
                if (insights is not null)
                {
                    return new InsightsResult(insights);
                }
            }
            catch (JsonException)
            {
                // fall through to raw-text fallback
            }
        }

        return new InsightsResult(
            [trimmed],
            "Sampling response could not be parsed as a JSON insights payload; returning raw text.");
    }

    private static IReadOnlyList<string>? ExtractInsights(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                return ToStringList(root);

            case JsonValueKind.Object:
                foreach (var key in new[] { "insights", "Insights", "items", "results" })
                {
                    if (root.TryGetProperty(key, out var match) && match.ValueKind == JsonValueKind.Array)
                    {
                        return ToStringList(match);
                    }
                }

                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var asStrings = ToStringList(prop.Value);
                        if (asStrings.Count > 0)
                        {
                            return asStrings;
                        }
                    }
                }
                return null;

            default:
                return null;
        }
    }

    private static List<string> ToStringList(JsonElement array)
    {
        var list = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
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

    private static string? ExtractJsonSlice(string text)
    {
        // Try the trimmed text as-is first.
        if ((text.StartsWith('{') && text.EndsWith('}')) ||
            (text.StartsWith('[') && text.EndsWith(']')))
        {
            return text;
        }

        // Otherwise look for the outermost {...} or [...] span.
        int objStart = text.IndexOf('{');
        int objEnd = text.LastIndexOf('}');
        int arrStart = text.IndexOf('[');
        int arrEnd = text.LastIndexOf(']');

        bool hasObj = objStart >= 0 && objEnd > objStart;
        bool hasArr = arrStart >= 0 && arrEnd > arrStart;

        if (hasObj && hasArr)
        {
            // Prefer whichever appears first in the string.
            return objStart <= arrStart
                ? text.Substring(objStart, objEnd - objStart + 1)
                : text.Substring(arrStart, arrEnd - arrStart + 1);
        }

        if (hasObj)
        {
            return text.Substring(objStart, objEnd - objStart + 1);
        }

        if (hasArr)
        {
            return text.Substring(arrStart, arrEnd - arrStart + 1);
        }

        return null;
    }

    internal record InsightsGetCommandResult(InsightsResult Result);
}
