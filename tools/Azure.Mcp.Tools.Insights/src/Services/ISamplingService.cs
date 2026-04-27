// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ModelContextProtocol.Server;

namespace Azure.Mcp.Tools.Insights.Services;

/// <summary>
/// Thin abstraction over <see cref="McpServer.SampleAsync"/> so that command logic that
/// requests an LLM completion via MCP sampling can be unit tested without a live server.
/// </summary>
public interface ISamplingService
{
    /// <summary>
    /// Sends a single text prompt to the host LLM via MCP sampling and returns the
    /// concatenated text content of the response, or <c>null</c> if the response had no
    /// text content.
    /// </summary>
    Task<string?> SampleTextAsync(
        McpServer mcpServer,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken);
}
