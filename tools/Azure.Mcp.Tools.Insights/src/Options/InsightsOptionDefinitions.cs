// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.Insights.Options;

public static class InsightsOptionDefinitions
{
    public const string QueryName = "query";

    public static readonly Option<string> Query = new($"--{QueryName}")
    {
        Description = "Optional free-form description of the user's infrastructure intent " +
                      "(e.g. 'Internal web app for the finance team with a relational database backend'). " +
                      "When provided, insights are tailored toward this scenario; when omitted, generic " +
                      "tenant-wide patterns are returned.",
        Required = false,
    };
}
