# HagiCode.Libs.Skills

`HagiCode.Libs.Skills` is a reusable .NET 10 package for skills-oriented integrations.

Its first shipped capability is the `OnlineApi` module, which wraps the documented remote skills endpoints with typed models and a provider-agnostic client.

## Included online API operations

- Search requests against the public `skills.sh` catalog.
- Well-known discovery for host-specific `/.well-known/skills/index.json` catalogs.
- Audit metadata requests for installed skills.
- Telemetry tracking with opt-out support.
- GitHub repository and tree metadata requests.

## Dependency injection

```csharp
using HagiCode.Libs.Skills;
using HagiCode.Libs.Skills.OnlineApi;
using HagiCode.Libs.Skills.OnlineApi.Models;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodeSkills(options =>
{
    options.DisableTelemetry = true;
});

await using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IOnlineApiClient>();

var searchResponse = await client.SearchAsync(new SearchSkillsRequest
{
    Query = "codex",
    Limit = 10,
});
```

## Provider abstraction

The package keeps endpoint resolution behind `IOnlineApiEndpointProvider`.
The built-in `VercelOnlineApiEndpointProvider` uses the documented defaults for `skills.sh`, `add-skill.vercel.sh`, and GitHub REST APIs, while consumers can replace the provider without changing the public client surface.
