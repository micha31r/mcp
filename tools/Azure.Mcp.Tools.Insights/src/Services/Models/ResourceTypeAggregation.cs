// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Azure.Mcp.Tools.Insights.Services.Models;

/// <summary>
/// Aggregated property value frequencies for a single ARM resource type within a scope.
/// </summary>
/// <param name="ArmResourceType">Lowercase ARM resource type (e.g. "microsoft.storage/storageaccounts").</param>
/// <param name="TotalCount">Total number of resources of this type observed in the scope.</param>
/// <param name="PropertyAggregations">
/// Nested JSON object mirroring the resource's property shape; each scalar leaf is replaced
/// by an object mapping the top-3 most common observed values to their counts.
/// </param>
public sealed record ResourceTypeAggregation(
    string ArmResourceType,
    int TotalCount,
    JsonObject PropertyAggregations);
