// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Tools.Insights.Models;
using Azure.Mcp.Tools.Insights.Options.Tenant;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Commands;
using Microsoft.Mcp.Core.Models.Command;
using Microsoft.Mcp.Core.Models.Option;

namespace Azure.Mcp.Tools.Insights.Commands.Tenant;

public sealed class TenantGetCommand(ILogger<TenantGetCommand> logger)
    : GlobalCommand<TenantGetOptions>()
{
    private const string CommandTitle = "Get Tenant Insights";
    private const string NotImplementedNote = "Tenant-level insights are not yet implemented.";

    private readonly ILogger<TenantGetCommand> _logger = logger;

    public override string Id => "0a8c4a2b-7e8e-4cb7-8b0d-2c9f8a7e5f3c";

    public override string Name => "get";

    public override string Description =>
        """
        Derives architectural insights for an Azure tenant.

        Tenant-level insights are not yet implemented; this command currently returns an
        empty insights array along with a note indicating the limitation. The shape of the
        response is intentionally identical to `azmcp insights subscription get` so that
        callers can adopt this command without changes once it is implemented.
        """;

    public override string Title => CommandTitle;

    public override ToolMetadata Metadata => new()
    {
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        LocalRequired = false,
        Secret = false,
    };

    public override Task<CommandResponse> ExecuteAsync(
        CommandContext context,
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        if (!Validate(parseResult.CommandResult, context.Response).IsValid)
        {
            return Task.FromResult(context.Response);
        }

        _logger.LogInformation("Tenant insights requested; returning stub response.");

        var result = new InsightsResult([], NotImplementedNote);
        context.Response.Results = ResponseResult.Create(
            new TenantGetCommandResult(result),
            Commands.InsightsJsonContext.Default.TenantGetCommandResult);

        return Task.FromResult(context.Response);
    }

    internal record TenantGetCommandResult(InsightsResult Result);
}
