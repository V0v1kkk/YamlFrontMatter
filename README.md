# YamlFrontMatter

Strongly-typed access to YAML front matter in Markdown files — via an **F# Type Provider** and a standalone parsing library.

The library is **F#-first** — all core types are idiomatic F# (discriminated unions, `Map`, `option`). C# consumers can use it directly via standard .NET interop; see the [C# section](#use-from-c) below.

Point the Type Provider at a directory of Markdown files and get compile-time IntelliSense with property names, types, and nullability inferred automatically from the actual data.

## Quick start

### Install

```shell
dotnet add package YamlFrontMatter
dotnet add package YamlFrontMatter.TypeProvider
```

### Use the Type Provider

Given a directory of Markdown files with YAML front matter:

```yaml
---
name: my-skill
description: Does something useful.
version: "2.0"
active: true
priority: 42
tags: [fsharp, dotnet]
metadata:
  author: Vladimir
  revision: 3
---
```

Reference the provider with a static directory path. By default the library treats the collection as a **generic** YAML front-matter directory — every field is optional. Pass `Mode = "agent-skill"` to enforce strict Agent Skills validation and project embedded metadata, or `Mode = "skill"` for relaxed validation:

```fsharp
open YamlFrontMatter

// Strict Agent Skills specification with embedded metadata projection:
type AgentSkills =
    FrontMatterProvider<
        RootDirectory = "/path/to/skills",
        Pattern = "SKILL.md",
        Mode = "agent-skill",
        EmbeddedMetadataKey = "dev.v-san.skills">

for s in AgentSkills.GetAll() do
    printfn "%s — %s" s.Name.Value s.Description.Value
    match s.ExtensionMetadata with
    | Some ext -> printfn "  origin: %A, version: %A" ext.Origin ext.Version
    | None -> ()

// Relaxed SKILL.md collection — name and description are required non-empty strings
type Skills = FrontMatterProvider<"/path/to/skills", Mode = "skill">
for s in Skills.GetAll() do
    printfn "%s — %s" s.Name.Value s.Description.Value

// Generic YAML-front-matter collection — every field is `option`
type Posts = FrontMatterProvider<"/path/to/posts", Pattern = "*.md">
for p in Posts.GetAll() do
    printfn "%A — %A" p.Title p.Date
```

The provider scans the directory at **compile time**, infers a cross-file schema, and generates:

- `FrontMatterDefinition` — an erased type with typed properties for every discovered YAML key. In `agent-skill` and `skill` modes, `Name` and `Description` are non-optional and typed as `SkillName` / `SkillDescription`. In `agent-skill` mode with `EmbeddedMetadataKey` configured, `ExtensionMetadata` provides strongly-typed projection of the embedded YAML block.
- `GetAll()` — returns `seq<FrontMatterDefinition>` for files that pass schema validation.
- `GetRejected()` — files that have front matter but failed the schema (missing required field, wrong type, empty string, invalid format, unknown field, invalid embedded YAML) along with the precise per-field failure list.
- `GetSkipped()` — files that aren't front-matter documents at all (no `---` block, malformed YAML, IO error).
- `Describe()` — returns the inferred schema as an F# record declaration. Useful for code generation, documentation, and quick auditing.

## Validation Modes

| Mode | Use Case | Validation Rules |
|---|---|---|
| `"agent-skill"` | Standard Agent Skills (`SKILL.md`) | Strict validation against the official Agent Skills specification. Only `name`, `description`, `license`, `compatibility`, `metadata`, and `allowed-tools` permitted. Enforces lowercase kebab-case `name` (1–64 chars), directory name equality, `description` (1–1024 chars), `compatibility` (1–500 chars), and string-only `metadata` map. Supports `EmbeddedMetadataKey`. |
| `"skill"` | Legacy / Loose Skills | Requires non-empty `name` and `description`, but allows arbitrary top-level fields (e.g. `tags`, `priority`, `active`) without naming or length constraints. |
| `"general"` *(default)* | Generic Front Matter | Permissive mode for blog posts, documentation, ADRs, recipes. Every field is optional. |

### Use the Core library directly

```fsharp
open YamlFrontMatter.Types
open YamlFrontMatter.Schemas
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.FrontMatterReader
open YamlFrontMatter.Scanner

// Single-file read with schema validation
let path = AbsoluteFilePath.createUnsafe "/path/to/SKILL.md"
match tryRead Skill path with
| Ok raw                          -> printfn "valid: %A" raw.Path
| Error (ValidationFailed fs)     -> printfn "rejected: %A" fs
| Error other                     -> printfn "skipped: %A" other

// Schema discovery across a directory (no validation — just shape inference)
let report = discoverSchemaWithStats "/path/to/skills" "SKILL.md"
printfn "%s" (formatSchema report)

// Streaming scanner — three buckets via ScanItem DU
let opts = { RootDirectory = AbsoluteFilePath.createUnsafe "/path/to/skills"
             Pattern = "SKILL.md"; Parallelism = 8
             PathQueueCapacity = 256; ResultQueueCapacity = 256 }
let reader = scan Skill opts cancellationToken
// reader yields ScanItem = ItemValid raw | ItemRejected (path, failures) | ItemSkipped (path, reason)
```

### Skill-identity API: "is this file a skill, and what's its name?"

If you have a single file that *might* be a SKILL.md and you want a typed result with rich error reasons:

```fsharp
open YamlFrontMatter.Skill

match tryReadSkillIdentity (AbsoluteFilePath.createUnsafe path) with
| Ok id                          -> printfn "%s — %s" (SkillName.value id.Name) (SkillDescription.value id.Description)
| Error NoFrontMatter            -> printfn "not a skill (no front matter)"
| Error NameMissing              -> printfn "looks like a broken skill — missing name"
| Error (NameNotString actual)   -> printfn "name is not a string: %A" actual
| Error problem                  -> printfn "%A" problem
```

This is a thin specialisation of `tryRead Skill` — same parsing, but the error DU is narrowed to skill-specific cases (`NameMissing`, `NameEmpty`, `NameNotString`, `DescriptionMissing`, ...) and the success type is the typed `SkillIdentity` record.

See `[examples/FSharpExample/](examples/FSharpExample/)` for a complete working demo.

### Use from C#

The core `YamlFrontMatter` library works from C# without any wrappers. F# modules compile as static classes, and types are accessible as nested types within those classes.

```csharp
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using YamlFrontMatter;
using static YamlFrontMatter.Types;
using static YamlFrontMatter.Scanner;
using static YamlFrontMatter.SchemaInference;
```

**Read a single file:**

```csharp
var path = AbsoluteFilePathModule.createUnsafe("/path/to/SKILL.md");
var result = Scanner.tryReadOne(path);

if (result.IsOk && FSharpOption<RawSkillData>.get_IsSome(result.ResultValue))
{
    var skill = result.ResultValue.Value;
    Console.WriteLine(skill.Path.Value);

    // Extract typed values via pattern matching on YamlValue DU
    var nameKey = YamlKey.NewYamlKey("name");
    var name = MapModule.TryFind(nameKey, skill.Fields);
    if (FSharpOption<YamlValue>.get_IsSome(name) && name.Value is YamlValue.YString s)
        Console.WriteLine(s.Item);
}
```

**Schema inference:**

```csharp
var report = SchemaInference.discoverSchemaWithStats("/path/to/skills", "SKILL.md");
Console.WriteLine($"Scanned {report.FilesScanned} files");
Console.WriteLine(SchemaInference.formatSchema(report));
```

**Streaming scanner via Channels:**

```csharp
var options = new ScanOptions(
    rootDirectory: AbsoluteFilePathModule.createUnsafe("/path/to/skills"),
    pattern: "SKILL.md",
    parallelism: 8,
    pathQueueCapacity: 256,
    resultQueueCapacity: 256);

var reader = Scanner.scan(options, CancellationToken.None);
while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
{
    while (reader.TryRead(out var item))
    {
        if (item.IsOk && FSharpOption<RawSkillData>.get_IsSome(item.ResultValue))
        {
            var skill = item.ResultValue.Value;
            // process skill...
        }
    }
}
```

**C# interop notes:**


| F# type                                             | C# access                                                      |
| --------------------------------------------------- | -------------------------------------------------------------- |
| Module functions (`Scanner.scan`)                   | Static methods on the module class                             |
| Types in modules (`RawSkillData`)                   | Nested types: `Scanner.RawSkillData`                           |
| DU cases (`YamlValue.YString`)                      | Subtypes for `is`/`switch`: `value is YamlValue.YString s`     |
| Single-case DU (`YamlKey`)                          | Factory: `YamlKey.NewYamlKey("x")`, access: `.Value`           |
| F# `Map<K,V>`                                       | `FSharpMap<K,V>` — use `MapModule.TryFind(key, map)`           |
| F# `option<T>`                                      | `FSharpOption<T>` — check with `FSharpOption<T>.get_IsSome(x)` |
| F# `Result<T,E>`                                    | `FSharpResult<T,E>` — check `.IsOk` / `.IsError`               |
| Companion modules (`AbsoluteFilePath.createUnsafe`) | `AbsoluteFilePathModule.createUnsafe(s)`                       |


See `[examples/CSharpExample/](examples/CSharpExample/)` for a complete working demo.

## How it works

### Schema inference

The library reads YAML front matter from every matching file and builds a unified schema using a **type-widening lattice**:


| Narrowest | &rarr;               | Widest   |
| --------- | -------------------- | -------- |
| `bool`    | `int` &rarr; `float` | `string` |


- Fields present in **all** files are marked `PresentInAll = true`
- Nested YAML mappings become nested record types
- Lists are element-typed (`string list`, `int list`, etc.)
- Conflicting types across files are widened to the safest common type

### Type Provider architecture

The provider follows the canonical **two-project layout** recommended by [FSharp.TypeProviders.SDK](https://github.com/fsprojects/FSharp.TypeProviders.SDK):


| Component                                     | NuGet path                               | Purpose                                            |
| --------------------------------------------- | ---------------------------------------- | -------------------------------------------------- |
| `YamlFrontMatter.TypeProvider.dll` (Runtime)  | `lib/netstandard2.0/`                    | Runtime helpers + `TypeProviderAssembly` attribute |
| `YamlFrontMatter.TypeProvider.DesignTime.dll` | `typeproviders/fsharp41/netstandard2.0/` | Loaded by the F# compiler at design time           |


All design-time dependencies (VYaml, etc.) are bundled alongside the design-time DLL and do **not** pollute the consumer's runtime closure beyond `YamlFrontMatter`.

### Static parameters

| Parameter             | Type     | Default      | Description                                                                                     |
| --------------------- | -------- | ------------ | ----------------------------------------------------------------------------------------------- |
| `RootDirectory`       | `string` | *(required)* | Absolute path to the directory to scan                                                          |
| `Pattern`             | `string` | `"SKILL.md"` | File name glob pattern                                                                          |
| `Mode`                | `string` | `"general"`  | Validation mode: `"agent-skill"`, `"skill"`, or `"general"`                                     |
| `EmbeddedMetadataKey` | `string` | `""`         | Metadata key containing embedded YAML mapping to project into `ExtensionMetadata` (agent-skill) |

## Project structure

```
src/
  YamlFrontMatter/                       Core library: types, YAML parser, schema inference, parallel scanner
  YamlFrontMatter.TypeProvider/          Runtime assembly (NuGet package entry point)
  YamlFrontMatter.TypeProvider.DesignTime/  Design-time assembly (provider logic, loaded by F# compiler)
  dotnet-yamlfm/                         CLI tool (global dotnet tool) for scanning and schema inspection
tests/
  YamlFrontMatter.Tests/                 xUnit tests for schema inference and the type provider
examples/
  Skills/                                Shared sample SKILL.md fixtures for both examples
  FSharpExample/                         Idiomatic F# console app using the core library
  CSharpExample/                         C# console app demonstrating interop with the core library
```

## Supported YAML types

| YAML value       | Inferred F# type      | Property type        |
| ---------------- | --------------------- | -------------------- |
| `true` / `false` | `bool`                | `bool option`        |
| `42`, `-7`       | `int`                 | `int option`         |
| `3.14`           | `float`               | `float option`       |
| `"hello"`        | `string`              | `string option`      |
| `[a, b, c]`      | `string list`         | `string list option` |
| nested mapping   | generated record type | `XxxData option`     |

In `agent-skill` and `skill` modes, `Name` and `Description` are validated and exposed as typed `SkillName` / `SkillDescription`. In `agent-skill` mode with `EmbeddedMetadataKey`, the embedded mapping is projected into `ExtensionMetadata : ExtensionMetadataData option`.

## CLI tool

Install as a global tool:

```shell
dotnet tool install -g dotnet-yamlfm
```

Then use:

```shell
# Dump parsed metadata with validation
yamlfm /path/to/skills --mode agent-skill --embedded-key dev.v-san.skills

# Print the inferred F# record schema
yamlfm /path/to/skills --schema --mode agent-skill --embedded-key dev.v-san.skills
```

Or run from source:

```shell
dotnet run --project src/dotnet-yamlfm -- /path/to/skills --mode agent-skill --embedded-key dev.v-san.skills
dotnet run --project src/dotnet-yamlfm -- /path/to/skills --schema --mode agent-skill --embedded-key dev.v-san.skills
```

## Building

```shell
dotnet build
dotnet test
```

## Versioning

Major and minor version are fixed in `Directory.Build.props` (`VersionPrefix`). The patch number is auto-incremented by CI using the GitHub Actions run number.

## Publishing to NuGet

Publishing is done via GitHub Actions (`workflow_dispatch`):

1. Go to **Actions** &rarr; **Publish to NuGet**
2. Click **Run workflow**
3. Optionally provide a version override

The workflow runs tests, packs `YamlFrontMatter`, `YamlFrontMatter.TypeProvider`, and `dotnet-yamlfm` (global tool), pushes to NuGet via [Trusted Publishing](https://devblogs.microsoft.com/dotnet/enhanced-security-is-here-with-the-new-trust-publishing-on-nuget-org/) (OIDC, no API keys needed), and creates a GitHub Release.

**One-time setup:**

1. On [nuget.org](https://www.nuget.org) &rarr; Account &rarr; Trusted Publishing &rarr; create a policy:
  - Repository owner: `V0v1kkk`, Repository: `YamlFrontMatter`, Workflow: `publish.yml`
2. In GitHub repository secrets, add `NUGET_USER` with your nuget.org profile name

## AI-agent skill: analysing your collection

If you use an AI coding assistant (Claude Code, OpenAI Codex, etc.), this repo ships a **product skill** that teaches the assistant how to use this Type Provider in `dotnet fsi` scripts to inspect and audit any directory of YAML-front-matter Markdown files:

- [`skill/yamlfm-collection-analysis/`](skill/yamlfm-collection-analysis/) — the describe-first-then-query workflow, with four worked example scripts (`describe.fsx`, `count_by_category.fsx`, `find_outliers.fsx`, `audit.fsx`) all validated against a real collection.

Point your assistant at this skill the first time you ask it to analyse a Markdown collection — it'll produce typed F# scripts with autocomplete-friendly field access and surface real findings (missing fields, alternate spellings, versioning inconsistencies, authorship distributions) without guessing the schema.

## Contributing

See [AGENTS.md](AGENTS.md) for repository structure, conventions, and contribution guidance — written for both AI coding assistants and human contributors.

For agents working **on** this repository (not consumers of the package), two **dev-time skills** live under [`.skills/`](.skills/):

- [`.skills/fsharp-style/`](.skills/fsharp-style/) — F# coding-style guide capturing the conventions this codebase follows (single-case DU shape, active patterns, computation expressions, anti-patterns).
- [`.skills/fsharp-type-provider/`](.skills/fsharp-type-provider/) — comprehensive F# Type Provider authoring guide: project layout, erased vs generative, packaging, debugging, common pitfalls. Includes deeper reference material under `references/`.

These dev-time skills are versioned with the source so any AI assistant that clones the repo to **modify the codebase** gets the same opinionated guidance the maintainer's agent uses. They are distinct from the [product skill above](#ai-agent-skill-analysing-your-collection), which is for **consumers** of the published package.

## License

[MIT](LICENSE)