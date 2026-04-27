// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.Insights.Models;

/// <summary>
/// Result returned by Insights commands. <see cref="Insights"/> contains one sentence per
/// derived insight. <see cref="Note"/> is an optional human-readable note describing
/// limitations or caveats (for example, when a command is a stub or the sampling response
/// could not be parsed cleanly).
/// </summary>
public sealed record InsightsResult(IReadOnlyList<string> Insights, string? Note = null);
