// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Mcp.Tools.Insights.Models;

namespace Azure.Mcp.Tools.Insights.Commands;

[JsonSerializable(typeof(InsightsGetCommand.InsightsGetCommandResult))]
[JsonSerializable(typeof(InsightsResult))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
internal sealed partial class InsightsJsonContext : JsonSerializerContext
{
}
