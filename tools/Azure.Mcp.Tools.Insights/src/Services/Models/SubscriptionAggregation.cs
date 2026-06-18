// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.Insights.Services.Models;

/// <summary>
/// Aggregated infrastructure data for a single scope (one subscription or all subscriptions in
/// a tenant). Pairs the per-resource-type property aggregations with scope-wide counts.
/// </summary>
/// <param name="ResourceTypes">Per-ARM-resource-type property value frequencies.</param>
/// <param name="SubscriptionCount">Number of subscriptions covered by the aggregation.</param>
/// <param name="ResourceGroupCount">Distinct lowercased resource group names observed across
/// the user-managed resources in the scope (after filtering).</param>
/// <param name="FilteredAutoCreatedTypes">Sorted list of ARM types that were dropped because
/// they are auto-created side-effect resources carrying no design signal. Diagnostic only.</param>
public sealed record SubscriptionAggregation(
    IReadOnlyDictionary<string, ResourceTypeAggregation> ResourceTypes,
    int SubscriptionCount,
    int ResourceGroupCount,
    IReadOnlyList<string> FilteredAutoCreatedTypes);
