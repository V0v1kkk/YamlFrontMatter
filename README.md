# YamlFrontMatter

Strongly-typed access to YAML front matter in Markdown files — via an **F# Type Provider** and a standalone parsing library.

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

Reference the provider with a static directory path:

```fsharp
open YamlFrontMatter

type Skills = FrontMatterProvider<"/path/to/skills">

for skill in Skills.GetAll() do
    printfn "%s (v%s, active=%A)" skill.Name.Value skill.Version skill.Active
```

The provider scans the directory at **compile time**, infers a cross-file schema, and generates:

- `FrontMatterDefinition` — an erased type with typed properties for every discovered YAML key
- `GetAll()` — returns `seq<FrontMatterDefinition>` by scanning the directory at runtime
- `Describe()` — returns the inferred schema as an F# record declaration (useful for code generation and documentation)

### Use the Core library directly

```fsharp
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Scanner

// Infer schema from a directory
let report = discoverSchemaWithStats "/path/to/skills" "SKILL.md"
printfn "%s" (formatSchema report)

// Stream-scan files in parallel via System.Threading.Channels
let reader = scan scanOptions cancellationToken
// ... consume ChannelReader<Result<RawSkillData option, ScanError>>
```

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


| Parameter       | Type     | Default      | Description                            |
| --------------- | -------- | ------------ | -------------------------------------- |
| `RootDirectory` | `string` | *(required)* | Absolute path to the directory to scan |
| `Pattern`       | `string` | `"SKILL.md"` | File name glob pattern                 |


## Project structure

```
src/
  YamlFrontMatter/                       Core library: types, YAML parser, schema inference, parallel scanner
  YamlFrontMatter.TypeProvider/          Runtime assembly (NuGet package entry point)
  YamlFrontMatter.TypeProvider.DesignTime/  Design-time assembly (provider logic, loaded by F# compiler)
  dotnet-yamlfm/                         CLI tool for scanning and schema inspection
tests/
  YamlFrontMatter.Tests/                 xUnit tests for schema inference and the type provider
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


The `Name` and `Description` fields are treated as **required** and exposed as `SkillName` / `SkillDescription` (single-case DU wrappers), not options.

## CLI tool

```shell
# Dump every SKILL.md's parsed metadata (parallel streaming)
dotnet run --project src/dotnet-yamlfm -- /path/to/skills

# Print the inferred F# record type
dotnet run --project src/dotnet-yamlfm -- /path/to/skills --schema
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

The workflow runs tests, packs both `YamlFrontMatter` and `YamlFrontMatter.TypeProvider`, pushes to NuGet via [Trusted Publishing](https://devblogs.microsoft.com/dotnet/enhanced-security-is-here-with-the-new-trust-publishing-on-nuget-org/) (OIDC, no API keys needed), and creates a GitHub Release.

**One-time setup:**

1. On [nuget.org](https://www.nuget.org) &rarr; Account &rarr; Trusted Publishing &rarr; create a policy:
  - Repository owner: `V0v1kkk`, Repository: `YamlFrontMatter`, Workflow: `publish.yml`
2. In GitHub repository secrets, add `NUGET_USER` with your nuget.org profile name

## License

[MIT](LICENSE)