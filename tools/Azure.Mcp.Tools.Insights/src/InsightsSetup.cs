// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Tools.Insights.Commands.Subscription;
using Azure.Mcp.Tools.Insights.Commands.Tenant;
using Azure.Mcp.Tools.Insights.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Mcp.Core.Areas;
using Microsoft.Mcp.Core.Commands;

namespace Azure.Mcp.Tools.Insights;

public class InsightsSetup : IAreaSetup
{
    public string Name => "insights";

    public string Title => "Derive Azure infrastructure insights";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IInsightsService, InsightsService>();
        services.AddSingleton<ISamplingService, SamplingService>();

        services.AddSingleton<SubscriptionGetCommand>();
        services.AddSingleton<TenantGetCommand>();
    }

    public CommandGroup RegisterCommands(IServiceProvider serviceProvider)
    {
        var insights = new CommandGroup(Name,
            """
            Insights operations - Commands for deriving architectural insights from existing
            Azure infrastructure. Aggregates Azure Resource Graph data and uses MCP sampling
            to surface dominant patterns (region, sku, security posture, tagging conventions,
            resource pairing) that inform downstream infrastructure planning.
            """,
            Title);

        var subscription = new CommandGroup("subscription",
            "Subscription-scoped insights - Derive insights from all user-managed resources in an Azure subscription.");
        insights.AddSubGroup(subscription);

        var tenant = new CommandGroup("tenant",
            "Tenant-scoped insights - Derive insights from resources across an Azure tenant.");
        insights.AddSubGroup(tenant);

        var subscriptionGet = serviceProvider.GetRequiredService<SubscriptionGetCommand>();
        subscription.AddCommand(subscriptionGet.Name, subscriptionGet);

        var tenantGet = serviceProvider.GetRequiredService<TenantGetCommand>();
        tenant.AddCommand(tenantGet.Name, tenantGet);

        return insights;
    }
}
