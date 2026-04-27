// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Tools.Insights.Commands;
using Azure.Mcp.Tools.Insights.Commands.Tenant;
using Azure.Mcp.Tools.Insights.Services;
using Microsoft.Mcp.Tests.Client;
using Xunit;

namespace Azure.Mcp.Tools.Insights.UnitTests.Tenant;

public class TenantGetCommandTests : CommandUnitTestsBase<TenantGetCommand, IInsightsService>
{
    [Fact]
    public async Task ExecuteAsync_ReturnsStubResponseWithNote()
    {
        var response = await ExecuteCommandAsync();

        var result = ValidateAndDeserializeResponse(response, InsightsJsonContext.Default.TenantGetCommandResult);

        Assert.NotNull(result);
        Assert.Empty(result.Result.Insights);
        Assert.Equal("Tenant-level insights are not yet implemented.", result.Result.Note);
    }
}
