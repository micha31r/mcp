// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Mcp.Core.Models.Command;

/// <summary>
/// Provides context for command execution including service access and response management
/// </summary>
public class CommandContext
{
    /// <summary>
    /// The service provider for dependency injection
    /// </summary>
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// The response object that will be returned to the client
    /// </summary>
    public CommandResponse Response { get; }

    /// <summary>
    /// Current telemetry context if there is one available.
    /// </summary>
    public Activity? Activity { get; }

    /// <summary>
    /// The MCP server handling the current tool call, when the command was invoked through the
    /// MCP protocol. <c>null</c> when the command is invoked outside of an MCP request (e.g. CLI).
    /// Commands that need to use MCP capabilities such as sampling or elicitation should access
    /// the server through this property and gracefully handle the <c>null</c> case.
    /// </summary>
    public McpServer? McpServer { get; }

    /// <summary>
    /// Creates a new command context
    /// </summary>
    /// <param name="serviceProvider">The service provider for dependency injection</param>
    /// <param name="activity">Optional telemetry activity associated with the request.</param>
    public CommandContext(IServiceProvider serviceProvider, Activity? activity = default)
        : this(serviceProvider, activity, null)
    {
    }

    /// <summary>
    /// Creates a new command context with access to the active MCP server for protocol features
    /// such as sampling and elicitation.
    /// </summary>
    /// <param name="serviceProvider">The service provider for dependency injection</param>
    /// <param name="activity">Optional telemetry activity associated with the request.</param>
    /// <param name="mcpServer">The MCP server handling the current tool call, or <c>null</c> when not invoked via MCP.</param>
    public CommandContext(IServiceProvider serviceProvider, Activity? activity, McpServer? mcpServer)
    {
        _serviceProvider = serviceProvider;
        Activity = activity;
        McpServer = mcpServer;
        Response = new CommandResponse
        {
            Status = HttpStatusCode.OK,
            Message = "Success"
        };
    }

    /// <summary>
    /// Gets a required service from the service provider
    /// </summary>
    /// <typeparam name="T">The type of service to retrieve</typeparam>
    /// <returns>The requested service instance</returns>
    /// <exception cref="InvalidOperationException">Thrown if the service is not registered</exception>
    public T GetService<T>() where T : class
    {
        return _serviceProvider.GetRequiredService<T>();
    }
}
