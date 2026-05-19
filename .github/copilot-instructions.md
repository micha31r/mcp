# Coding Instructions for GitHub Copilot

## Core Rules

- Always use primary constructors in C#
- Always run `dotnet build` after making a change
- Always use `System.Text.Json` over Newtonsoft
- Always put new classes and interfaces in separate files
- Always make members static if they can be
- All generated code must be AOT-safe
- Always use file-scoped namespaces
- Always add the Microsoft copyright header to every new file:
  ```csharp
  // Copyright (c) Microsoft Corporation.
  // Licensed under the MIT License.
  ```
- Always review your own code for consistency, maintainability, and testability
- Always ask for clarifications if the request is ambiguous or lacks sufficient context

---

## Project Structure

Each Azure service toolset lives at `tools/Azure.Mcp.Tools.{Service}/` and follows this exact layout:

```
tools/Azure.Mcp.Tools.{Service}/
├── src/
│   ├── {Service}.csproj            # IsAotCompatible = true
│   ├── {Service}Setup.cs           # IAreaSetup — registers services and commands
│   ├── {Service}JsonContext.cs     # AOT JSON source generation context
│   ├── GlobalUsings.cs             # global using System.CommandLine;
│   ├── AssemblyInfo.cs
│   ├── Commands/
│   │   ├── Base{Service}Command.cs # Optional base command for shared option binding
│   │   └── {Resource}/
│   │       ├── {Resource}{Operation}Command.cs
│   │       └── ...
│   ├── Options/
│   │   ├── {Service}OptionDefinitions.cs  # All static Option<T> definitions
│   │   ├── Base{Service}Options.cs        # Optional shared options base
│   │   └── {Resource}/
│   │       ├── {Resource}{Operation}Options.cs
│   │       └── ...
│   ├── Services/
│   │   ├── I{Service}Service.cs
│   │   ├── {Service}Service.cs
│   │   └── Models/                        # Service-layer data models
│   └── Models/                            # Public result models
└── tests/
    ├── Azure.Mcp.Tools.{Service}.UnitTests/
    │   ├── {Resource}/
    │   │   └── {Resource}{Operation}CommandTests.cs
    │   └── Services/
    ├── Azure.Mcp.Tools.{Service}.LiveTests/
    │   └── assets.json
    ├── test-resources.bicep          # Azure test infrastructure
    └── test-resources-post.ps1       # Post-deployment RBAC setup
```

---

## Naming Conventions

| Artifact | Pattern | Example |
|---|---|---|
| Command class | `{Resource}{Operation}Command` | `AccountGetCommand` |
| Options class | `{Resource}{Operation}Options` | `AccountGetOptions` |
| Service interface | `I{Service}Service` | `IStorageService` |
| Service implementation | `{Service}Service` | `StorageService` |
| Setup class | `{Service}Setup` | `StorageSetup` |
| JSON context | `{Service}JsonContext` | `StorageJsonContext` |
| Option definitions class | `{Service}OptionDefinitions` | `StorageOptionDefinitions` |
| Command group name | concatenated lowercase, no dashes | `storage`, `account`, `blob` |
| CLI option parameter | `--kebab-case` | `--resource-group`, `--access-tier` |
| Option name constant | `{Param}Name` (const string) | `AccountName = "account"` |
| Result record | `{Command}Result` (internal, inside command class) | `AccountGetCommandResult` |

**Parameter naming rules:**
- Use `subscription` (never `subscriptionId`) — supports both IDs and names
- Use `resourceGroup` (not `resourceGroupName`)
- Use singular nouns for resource names: `--account`, not `--account-name`
- No redundant `-name` suffixes on identifiers

---

## Command Implementation Pattern

Every command follows this exact structure:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Mcp.Core.Commands.Subscription;
using Azure.Mcp.Tools.{Service}.Options;
using Azure.Mcp.Tools.{Service}.Options.{Resource};
using Azure.Mcp.Tools.{Service}.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Commands;
using Microsoft.Mcp.Core.Extensions;
using Microsoft.Mcp.Core.Models.Command;
using Microsoft.Mcp.Core.Models.Option;

namespace Azure.Mcp.Tools.{Service}.Commands.{Resource};

public sealed class {Resource}{Operation}Command(
    ILogger<{Resource}{Operation}Command> logger,
    I{Service}Service {service}Service)
    : SubscriptionCommand<{Resource}{Operation}Options>()
{
    private const string CommandTitle = "{Human Readable Title}";
    private readonly ILogger<{Resource}{Operation}Command> _logger = logger;
    private readonly I{Service}Service _{service}Service = {service}Service;

    // Unique GUID — generate a new one for every command
    public override string Id => "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx";

    // Lowercase verb: get, create, list, delete, update
    public override string Name => "{operation}";

    // Multi-line raw string — detailed description for AI discoverability
    public override string Description =>
        """
        Detailed description of what the command does, inputs expected, and outputs returned.
        Include parameter names and valid values to aid AI agent routing.
        """;

    public override string Title => CommandTitle;

    // Required on every command — set all fields accurately
    public override ToolMetadata Metadata => new()
    {
        Destructive = false,   // true if operation can cause data loss
        Idempotent = true,     // true if calling multiple times has same effect
        OpenWorld = false,     // true if results may be incomplete / unbounded
        ReadOnly = true,       // false for create/update/delete
        LocalRequired = false, // true only if local file system access needed
        Secret = false         // true if result contains secrets/credentials
    };

    protected override void RegisterOptions(Command command)
    {
        base.RegisterOptions(command); // ALWAYS call base first
        command.Options.Add({Service}OptionDefinitions.{Param}.AsRequired());
        command.Options.Add({Service}OptionDefinitions.{OptionalParam}.AsOptional());
        // Use OptionDefinitions.Common.ResourceGroup for --resource-group
    }

    protected override {Resource}{Operation}Options BindOptions(ParseResult parseResult)
    {
        var options = base.BindOptions(parseResult); // ALWAYS call base first
        options.{Param} = parseResult.GetValueOrDefault<string>({Service}OptionDefinitions.{Param}.Name);
        return options;
    }

    public override async Task<CommandResponse> ExecuteAsync(
        CommandContext context, ParseResult parseResult, CancellationToken cancellationToken)
    {
        // 1. Validate — return early on failure
        if (!Validate(parseResult.CommandResult, context.Response).IsValid)
        {
            return context.Response;
        }

        // 2. Bind options
        var options = BindOptions(parseResult);

        try
        {
            // 3. Call service
            var result = await _{service}Service.{Operation}Async(
                options.{Param}!,
                options.Subscription!,
                options.Tenant,
                options.RetryPolicy,
                cancellationToken);

            // 4. Serialize result using AOT-safe JSON context
            context.Response.Results = ResponseResult.Create(
                new(result),
                {Service}JsonContext.Default.{Resource}{Operation}CommandResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error in {Operation}. {Param}: {{Param}}, Subscription: {{Subscription}}.",
                options.{Param}, options.Subscription);
            HandleException(context, ex); // ALWAYS call this in catch
        }

        return context.Response;
    }

    // Override for service-specific HTTP status mapping
    protected override string GetErrorMessage(Exception ex) => ex switch
    {
        RequestFailedException reqEx when reqEx.Status == (int)HttpStatusCode.NotFound =>
            "Resource not found. Verify the resource exists and you have access.",
        RequestFailedException reqEx when reqEx.Status == (int)HttpStatusCode.Forbidden =>
            $"Authorization failed. Details: {reqEx.Message}",
        RequestFailedException reqEx when reqEx.Status == (int)HttpStatusCode.Conflict =>
            "Resource already exists. Choose a different name.",
        RequestFailedException reqEx => reqEx.Message,
        _ => base.GetErrorMessage(ex)
    };

    // Strongly-typed result — always internal, always a record, always inside the command class
    internal record {Resource}{Operation}CommandResult(/* typed fields */);
}
```

### Key rules for commands
- Class must be `sealed` (unless designed as a base class)
- `Id` must be a unique GUID — generate a new one, never reuse
- `base.RegisterOptions(command)` must be called first in `RegisterOptions`
- `base.BindOptions(parseResult)` must be called first in `BindOptions`
- `HandleException(context, ex)` must always be called in the `catch` block
- Result record is `internal` and declared inside the command class
- `base.Dispose()` must be called when overriding `Dispose`

---

## Option Definitions Pattern

All option definitions for a service live in a single static class:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.{Service}.Options;

public static class {Service}OptionDefinitions
{
    // 1. Declare a const string for each option name (used for binding)
    public const string AccountName = "account";
    public const string LocationName = "location";

    // 2. Declare a static readonly Option<T> for each option
    public static readonly Option<string> Account = new($"--{AccountName}")
    {
        Description = "The name of the Azure Storage account (e.g., 'mystorageaccount').",
        Required = true
    };

    public static readonly Option<string> Location = new($"--{LocationName}")
    {
        Description = "The Azure region (e.g., 'eastus', 'westus2').",
        Required = true
    };

    public static readonly Option<bool> EnableFeature = new($"--enable-feature")
    {
        Description = "Whether to enable the feature.",
        DefaultValueFactory = _ => false,
        Required = false
    };
}
```

- Use `.AsRequired()` / `.AsOptional()` extension methods when registering in `RegisterOptions` to override the default `Required` flag per-command
- Option names use `--kebab-case`
- Descriptions should be helpful to both humans and AI agents
- Never use `readonly` *fields* in command classes for options — always use static `OptionDefinitions`

---

## Options Classes Pattern

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using Microsoft.Mcp.Core.Options;

namespace Azure.Mcp.Tools.{Service}.Options.{Resource};

// Extend SubscriptionOptions (most common), or service-specific base options
public class {Resource}{Operation}Options : SubscriptionOptions
{
    // Use [JsonPropertyName] with the const string from OptionDefinitions
    [JsonPropertyName({Service}OptionDefinitions.AccountName)]
    public string? Account { get; set; }

    [JsonPropertyName({Service}OptionDefinitions.LocationName)]
    public string? Location { get; set; }
}
```

- Base class is `SubscriptionOptions` unless the service has a shared base (e.g., `BaseStorageOptions : SubscriptionOptions`)
- Never redefine properties already on the base class
- All properties nullable unless the field is definitionally required
- Use `[JsonPropertyName]` pointing at the `const string` in `OptionDefinitions`, not a hardcoded string

---

## Service Pattern

### Interface

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Mcp.Core.Options;

namespace Azure.Mcp.Tools.{Service}.Services;

public interface I{Service}Service
{
    // Read operations return ResourceQueryResults<T> or List<T>
    Task<ResourceQueryResults<{Model}>> List{Resources}Async(
        string subscription,
        string? resourceGroup = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default);

    // Write operations return the created/updated model
    Task<{Model}> Create{Resource}Async(
        string name,
        string resourceGroup,
        string subscription,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default);
}
```

### Implementation

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Mcp.Tools.{Service}.Services;

// Extend BaseAzureResourceService for Resource Graph-based read queries (preferred)
// Extend BaseAzureService for write-only or non-ARM services
public class {Service}Service(
    ISubscriptionService subscriptionService,
    ITenantService tenantService,
    ILogger<{Service}Service> logger)
    : BaseAzureResourceService(subscriptionService, tenantService), I{Service}Service
{
    private readonly ILogger<{Service}Service> _logger = logger;

    public async Task<ResourceQueryResults<{Model}>> List{Resources}Async(
        string subscription,
        string? resourceGroup = null,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        // Always validate required parameters first
        ValidateRequiredParameters((nameof(subscription), subscription));

        return await ExecuteResourceQueryAsync(
            "Microsoft.{Provider}/{ResourceType}",
            resourceGroup,
            subscription,
            retryPolicy,
            ConvertToModel,
            tenant: tenant,
            cancellationToken: cancellationToken);
    }

    public async Task<{Model}> Create{Resource}Async(
        string name,
        string resourceGroup,
        string subscription,
        string? tenant = null,
        RetryPolicyOptions? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredParameters(
            (nameof(name), name),
            (nameof(resourceGroup), resourceGroup),
            (nameof(subscription), subscription));

        var armClient = await CreateArmClientWithApiVersionAsync(
            "Microsoft.{Provider}/{ResourceType}", "{api-version}",
            tenant, retryPolicy, cancellationToken);

        // ... ARM operation ...
    }
}
```

**Service rules:**
- `ValidateRequiredParameters` at the top of every public method
- Required params (non-nullable) come first; optional params (`string?`, `RetryPolicyOptions?`) come last with defaults
- Use `BaseAzureResourceService` + `ExecuteResourceQueryAsync` for list/get via Resource Graph
- Use `ExecuteSingleResourceQueryAsync` when fetching one specific named resource
- Use `CreateArmClientWithApiVersionAsync` for write operations
- Static readonly sets for valid enum values (e.g., valid SKU names)
- `EscapeKqlString(value)` when embedding user input in KQL filter strings

---

## JSON Serialization Context (AOT Safety)

Every toolset must have a JSON context registering all result types:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Mcp.Tools.{Service}.Commands.{Resource};
using Azure.Mcp.Tools.{Service}.Models;

namespace Azure.Mcp.Tools.{Service}.Commands;

[JsonSerializable(typeof({Resource}GetCommand.{Resource}GetCommandResult))]
[JsonSerializable(typeof({Resource}CreateCommand.{Resource}CreateCommandResult))]
[JsonSerializable(typeof({Model}))]
[JsonSerializable(typeof(List<{Model}>))]
[JsonSerializable(typeof(JsonElement))]            // Include if service returns raw JSON
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
internal sealed partial class {Service}JsonContext : JsonSerializerContext
{
}
```

- Register **every** type that is serialized as a response result
- Register model types used inside result records
- Class must be `internal sealed partial`
- `csproj` must set `<IsAotCompatible>true</IsAotCompatible>`
- Use `[DynamicallyAccessedMembers(TrimAnnotations.CommandAnnotations)]` on generic type parameters in abstract command base classes

---

## Setup / Registration

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Mcp.Core.Areas;
using Microsoft.Mcp.Core.Commands;

namespace Azure.Mcp.Tools.{Service};

public class {Service}Setup : IAreaSetup
{
    // Lowercase, no dashes — this becomes the top-level CLI command group name
    public string Name => "{service}";

    public string Title => "Manage Azure {Service}";

    public void ConfigureServices(IServiceCollection services)
    {
        // Register service first, then all commands as singletons
        services.AddSingleton<I{Service}Service, {Service}Service>();
        services.AddSingleton<{Resource}GetCommand>();
        services.AddSingleton<{Resource}CreateCommand>();
    }

    public CommandGroup RegisterCommands(IServiceProvider serviceProvider)
    {
        // Root command group — description should be usable by AI to route requests
        var root = new CommandGroup(Name,
            """
            {Service} operations - Commands for managing {resource description}.
            """,
            Title);

        // Sub-groups by resource type
        var resourceGroup = new CommandGroup("{resource}", "{Service} {resource} operations - ...");
        root.AddSubGroup(resourceGroup);

        // Resolve and register each command
        var getCmd = serviceProvider.GetRequiredService<{Resource}GetCommand>();
        resourceGroup.AddCommand(getCmd.Name, getCmd);

        return root;
    }
}
```

- Register the `{Service}Setup` in the appropriate server's `Program.cs` / setup file
- Command group names: concatenated lowercase, no dashes (`storage`, `account`, `blob`)
- `CommandGroup` descriptions should be informative enough for AI routing

---

## GlobalUsings.cs

Every toolset's `src/GlobalUsings.cs` must contain:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

global using System.CommandLine;
```

Add additional global usings only when a type is used pervasively across the toolset.

---

## Unit Test Pattern

All command tests extend `CommandUnitTestsBase<TCommand, IService>` and use xUnit + NSubstitute.

### Required test methods for every command

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Azure.Mcp.Tools.{Service}.Commands.{Resource};
using Azure.Mcp.Tools.{Service}.Commands;
using Azure.Mcp.Tools.{Service}.Services;
using Microsoft.Mcp.Core.Options;
using Microsoft.Mcp.Tests.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Azure.Mcp.Tools.{Service}.UnitTests.{Resource};

public class {Resource}{Operation}CommandTests
    : CommandUnitTestsBase<{Resource}{Operation}Command, I{Service}Service>
{
    // 1. Constructor / metadata
    [Fact]
    public void Constructor_InitializesCommandCorrectly()
    {
        var command = Command.GetCommand();
        Assert.Equal("{operation}", command.Name);
        Assert.NotNull(command.Description);
        Assert.NotEmpty(command.Description);
    }

    // 2. Input validation — cover all required/optional combinations
    [Theory]
    [InlineData("--{param} value --subscription sub123", true)]
    [InlineData("--subscription sub123", false)] // missing required param
    [InlineData("", false)]                      // no parameters
    public async Task ExecuteAsync_ValidatesInputCorrectly(string args, bool shouldSucceed)
    {
        if (shouldSucceed)
        {
            Service.{Operation}Async(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<RetryPolicyOptions>(), Arg.Any<CancellationToken>())
                .Returns(/* expected result */);
        }

        var response = await ExecuteCommandAsync(args);

        Assert.Equal(shouldSucceed ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.Status);
        if (!shouldSucceed)
            Assert.Contains("required", response.Message.ToLower());
    }

    // 3. Happy path — verify service call and result deserialization
    [Fact]
    public async Task ExecuteAsync_ReturnsExpectedResult()
    {
        // Arrange
        Service.{Operation}Async(...)
            .Returns(expectedResult);

        // Act
        var response = await ExecuteCommandAsync("--{param}", "value", "--subscription", "sub123");

        // Assert
        var result = ValidateAndDeserializeResponse(
            response, {Service}JsonContext.Default.{Resource}{Operation}CommandResult);
        Assert.NotNull(result);
        // Assert specific fields
    }

    // 4. Generic error → 500
    [Fact]
    public async Task ExecuteAsync_HandlesServiceErrors()
    {
        Service.{Operation}Async(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<RetryPolicyOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Test error"));

        var response = await ExecuteCommandAsync("--{param}", "value", "--subscription", "sub123");

        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        Assert.Contains("Test error", response.Message);
        Assert.Contains("troubleshooting", response.Message);
    }

    // 5. 404 Not Found
    [Fact]
    public async Task ExecuteAsync_HandlesNotFound()
    {
        Service.{Operation}Async(...)
            .ThrowsAsync(new RequestFailedException((int)HttpStatusCode.NotFound, "Not found"));

        var response = await ExecuteCommandAsync(...);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
    }

    // 6. 403 Authorization failure
    [Fact]
    public async Task ExecuteAsync_HandlesAuthorizationFailure()
    {
        Service.{Operation}Async(...)
            .ThrowsAsync(new RequestFailedException((int)HttpStatusCode.Forbidden, "Forbidden"));

        var response = await ExecuteCommandAsync(...);

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    // 7. Verify exact service call parameters (for write commands)
    [Fact]
    public async Task ExecuteAsync_CallsServiceWithCorrectParameters()
    {
        // ...set up mock...
        await ExecuteCommandAsync("--{param}", "value", "--subscription", "sub123");

        await Service.Received(1).{Operation}Async(
            "value", "sub123", null, Arg.Any<RetryPolicyOptions>(), Arg.Any<CancellationToken>());
    }
}
```

**Test rules:**
- Use `ExecuteCommandAsync(args)` helper (space-delimited string or individual string args)
- Use `ValidateAndDeserializeResponse(response, JsonContext.Default.{Result})` for deserialization
- Use `NSubstitute` for all mocking — `Service.Method(...).Returns(...)` / `.ThrowsAsync(...)`
- Cover: 200 happy path, 400 validation, 404 not found, 403 forbidden, 409 conflict (if applicable), 500 generic
- Test class names: `{Resource}{Operation}CommandTests`
- Test method names: `ExecuteAsync_{Scenario}`

---

## Live Test Infrastructure

Every toolset that wraps an Azure service needs:

### `tests/test-resources.bicep`

```bicep
targetScope = 'resourceGroup'

@minLength(3)
@maxLength(24)
@description('The base resource name.')
param baseName string = resourceGroup().name

@description('The location of the resource.')
param location string = resourceGroup().location

@description('The tenant ID to which the application and resources belong.')
param tenantId string = '72f988bf-86f1-41af-91ab-2d7cd011db47'

@description('The client OID to grant access to test resources.')
param testApplicationOid string

resource myResource 'Microsoft.{Provider}/{Type}@{api-version}' = {
  name: baseName
  location: location
  // ...
}

// Grant test principal the required role
resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: myResource
  name: guid(myResource.id, testApplicationOid, roleDefinition.id)
  properties: {
    principalId: testApplicationOid
    roleDefinitionId: roleDefinition.id
    principalType: 'ServicePrincipal'
  }
}
```

### `tests/assets.json`

```jsonc
{
  "AssetsRepo": "Azure/azure-sdk-assets",
  "AssetsRepoPrefixPath": "",
  "TagPrefix": "Azure.Mcp.Tools.{Service}.LiveTests",
  "Tag": ""
}
```

### Live test classes

- Extend `RecordedCommandTestsBase` (not `CommandTestsBase`)
- See `docs/recorded-tests.md` for full guide on converting and running recorded tests

---

## Error Handling

```csharp
// In ExecuteAsync — always use this pattern
catch (Exception ex)
{
    _logger.LogError(ex,
        "Error {Operation}. {Context}: {{ContextValue}}, Subscription: {{Subscription}}.",
        options.{Context}, options.Subscription);
    HandleException(context, ex); // Never skip this
}

// Override GetErrorMessage for service-specific mappings (switch expression)
protected override string GetErrorMessage(Exception ex) => ex switch
{
    RequestFailedException reqEx when reqEx.Status == (int)HttpStatusCode.NotFound =>
        "Resource not found. Verify the resource exists and you have access.",
    RequestFailedException reqEx when reqEx.Status == (int)HttpStatusCode.Forbidden =>
        $"Authorization failed. Details: {reqEx.Message}",
    RequestFailedException reqEx when reqEx.Status == (int)HttpStatusCode.Conflict =>
        "Resource already exists. Choose a different name.",
    RequestFailedException reqEx => reqEx.Message,
    _ => base.GetErrorMessage(ex)
};
```

- Always call `HandleException` — never swallow exceptions silently
- Log before calling `HandleException` so context is captured in logs
- Use structured log parameters (`{ParameterName}`) for searchability

---

## Engineering System

- Use `./eng/scripts/Build-Local.ps1 -UsePaths -VerifyNpx` to verify changes to PowerShell, C# project files and npm packages
- Don't run local builds to check pipeline YAML files (e.g., files in `eng/pipelines/` with `.yml` extension)
- Prefer file-scoped builds: `dotnet build tools/Azure.Mcp.Tools.{Service}/src` over building the full solution
- Run spelling: `.\eng\common\spelling\Invoke-Cspell.ps1`

---

## Pull Request Guidelines

- Ensure all tests pass
- Follow the [contribution guidelines](https://github.com/microsoft/mcp/blob/main/CONTRIBUTING.md)
- Include appropriate documentation
- Include tests that cover your changes
- Update CHANGELOG.md with your changes — see `docs/changelog-entries.md` for format. Always use `-ChangelogPath` parameter
- Run `.\eng\common\spelling\Invoke-Cspell.ps1`
- Submit one tool per pull request
- Create the auto-generated PR body as normal, but `copilot` should add an additional section after all of its regular PR body content. The contents should be:
  ```
  ## Invoking Livetests

  Copilot submitted PRs are not trustworthy by default. Users with `write` access to the repo need to validate the contents of this PR before leaving a comment with the text `/azp run mcp - pullrequest - live`. This will trigger the necessary livetest workflows to complete required validation.
  ```

---

## Transitioning Live Tests to Recorded Tests

- Always convert `tool` services to inject `IHttpClientFactory` into its clients and use `IHttpClientFactory.CreateClient` method to instantiate the `HttpClient` for usage in the tool classes' methods.
  - If `IHttpClientFactory` is already injected into the client, ensure that `IHttpClientFactory.CreateClient` is used to instantiate the `HttpClient`. If this is done, then no further action is needed.
- Always re-parent test classes parented by `CommandTestsBase` to `RecordedCommandTestsBase`. This will require minor fixture adjustments.
- Always generate a new `assets.json` file alongside the livetest csproj file if one does not exist. This file should contain the following content:
  ```jsonc
  {
    "AssetsRepo": "Azure/azure-sdk-assets",
    "AssetsRepoPrefixPath": "",
    "TagPrefix": "<LiveTestCsProjFileNameWithoutExtension>", // e.g., "Azure.Mcp.Tools.KeyVault.Tests"
    "Tag": ""
  }
  ```
- Copilot should utilize the [recorded test documentation](https://github.com/microsoft/mcp/blob/main/docs/recorded-tests.md) in `docs/recorded-tests.md` for more details on how to convert and validate recorded tests.
