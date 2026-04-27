// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Tools.Insights.Services.Models;
using Microsoft.Mcp.Core.Options;

namespace Azure.Mcp.Tools.Insights.Services;

/// <summary>
/// Queries Azure Resource Graph for resources in a subscription, then aggregates
/// per-resource-type property value frequencies suitable for downstream LLM analysis.
/// </summary>
public interface IInsightsService
{
    /// <summary>
    /// Aggregates resources in the given subscription by ARM resource type, returning
    /// the top-3 most-common observed values for each property leaf.
    /// </summary>
    /// <param name="subscription">Subscription ID or name.</param>
    /// <param name="tenant">Optional tenant scope.</param>
    /// <param name="retryPolicy">Optional retry policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SubscriptionAggregation> AggregateSubscriptionAsync(
        string subscription,
        string? tenant,
        RetryPolicyOptions? retryPolicy,
        CancellationToken cancellationToken);
}
