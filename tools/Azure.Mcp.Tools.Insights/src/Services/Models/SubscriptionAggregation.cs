// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.Insights.Services.Models;

/// <summary>
/// Aggregated infrastructure data for a single subscription. Pairs the per-resource-type
/// property aggregations with subscription-scoped context (distinct resource group count
/// and the list of ARM types that were filtered out as auto-created plumbing).
/// </summary>
/// <param name="ResourceTypes">Per-ARM-resource-type property value frequencies.</param>
/// <param name="ResourceGroupCount">Distinct lowercased resource group names observed
/// across the subscription's user-managed resources (after filtering).</param>
/// <param name="FilteredAutoCreatedTypes">Sorted, distinct list of ARM types that were
/// dropped from the aggregation because they are auto-created side-effect resources
/// (e.g. ARM child types like <c>.../virtualmachines/extensions</c>) carrying no design
/// signal.</param>
public sealed record SubscriptionAggregation(
    IReadOnlyDictionary<string, ResourceTypeAggregation> ResourceTypes,
    int ResourceGroupCount,
    IReadOnlyList<string> FilteredAutoCreatedTypes);
