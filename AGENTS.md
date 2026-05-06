# AGENTS.md — Repository Guide for AI Coding Agents

This file is for AI assistants and human contributors working **on** this repository.
For users **consuming** the library, see [README.md](README.md).
For F# coding style and Type Provider authoring guidance, two repo-bundled skills live in [`.skills/`](.skills/) — see [Skill references](#skill-references-for-ai-agents-in-this-repo) below.

---

## What this repo is

A small F# library + Type Provider + global CLI tool that reads YAML front matter from Markdown files (`SKILL.md` and similar). Three deliverables that ship as separate NuGet packages:

| Package | Project | What it ships |
|---|---|---|
| `YamlFrontMatter` | [src/YamlFrontMatter](src/YamlFrontMatter) | Core library — types, YAML parser (VYaml-backed), schema inference, parallel `ChannelReader`-based scanner |
| `YamlFrontMatter.TypeProvider` | [src/YamlFrontMatter.TypeProvider](src/YamlFrontMatter.TypeProvider) + [src/YamlFrontMatter.TypeProvider.DesignTime](src/YamlFrontMatter.TypeProvider.DesignTime) | F# Type Provider; ships as one NuGet (Runtime + DesignTime bundled per the canonical TP layout) |
| `dotnet-yamlfm` | [src/dotnet-yamlfm](src/dotnet-yamlfm) | .NET global tool — CLI wrapper around the core library |

Tests live in [tests/YamlFrontMatter.Tests](tests/YamlFrontMatter.Tests). Examples in [examples/](examples/) (`FSharpExample`, `CSharpExample`, plus shared `Skills/` fixtures).

---

## Where to put new code

A decision tree for "I want to add X — which project?":

| You're adding… | Goes in |
|---|---|
| A new domain type (single-case DU, record, ADT) | `YamlFrontMatter/Types.fs` |
| A pure transformation over the data model | `YamlFrontMatter/SchemaInference.fs` (or a new file in `YamlFrontMatter/`) |
| Anything that touches the file system, network, or threads | `YamlFrontMatter/SkillScanner.fs` (or a sibling) |
| A new TP-generated member, schema rule, or static parameter | `YamlFrontMatter.TypeProvider.DesignTime/SkillCollectionProvider.fs` |
| A runtime helper that TP quotations call into | `YamlFrontMatter.TypeProvider/RuntimeHelpers.fs` |
| A CLI subcommand or flag | `dotnet-yamlfm/Program.fs` |
| A test (unit or integration) | `tests/YamlFrontMatter.Tests/` |
| A demo/example | `examples/FSharpExample/` or `examples/CSharpExample/` (don't add new top-level example projects without a clear reason) |
| Sample SKILL.md fixtures | `examples/Skills/` (shared across both example apps) and `tests/YamlFrontMatter.Tests/Fixtures/` (used by tests) |

### F# file ordering inside a project

F# compiles top-to-bottom. The `<Compile Include>` order in each `.fsproj` is load-bearing: `Types.fs` first, pure code before effectful code, composition root last. **Do not reorder without checking the dependency direction.**

---

## Build, test, run

```shell
dotnet restore
dotnet build              # all projects, Debug
dotnet build -c Release   # for packaging / smoke

dotnet test               # all xUnit tests in tests/YamlFrontMatter.Tests

# Run the CLI from source against test fixtures:
dotnet run --project src/dotnet-yamlfm -- tests/YamlFrontMatter.Tests/Fixtures
dotnet run --project src/dotnet-yamlfm -- tests/YamlFrontMatter.Tests/Fixtures --schema

# Run the F# example against shared sample skills:
dotnet run --project examples/FSharpExample
```

The solution is `YamlFrontMatter.sln`. Single command `dotnet build` / `dotnet test` always operates on it.

---

## Code conventions

Style lives in [`.skills/fsharp-style/SKILL.md`](.skills/fsharp-style/SKILL.md) — single-case DUs with `[<Struct>] + private + with member this.Value + module`, modules over classes, `task { }` over `async { }`, `Result` over exceptions, etc. The skill triggers on any `.fs`/`.fsproj` edit; agents should rely on it rather than reinventing rules here.

Project-specific points the skill doesn't cover:

- **`Directory.Build.props`** is the single source of truth for `VersionPrefix`, `Authors`, `RepositoryUrl`, `PackageLicenseExpression`. Don't duplicate these in individual `.fsproj` files; they're inherited.
- **`netstandard2.0`** is the target framework for all `src/` projects. This is required for the Type Provider (compiler tooling loads design-time DLLs as netstandard2.0). Don't bump the lib targets to net8/net10 — only the test project and CLI tool target net10.0.
- **YAML parser is VYaml**, not YamlDotNet. There's a [historical reason](#known-gotchas-rider--yamldotnet-version-conflict) — switching back will reintroduce a Rider design-time conflict. Don't add YamlDotNet as a dependency.
- **The `.NET global tool` lives at [src/dotnet-yamlfm](src/dotnet-yamlfm)** (the `dotnet-` prefix is the convention). The output binary name is `yamlfm`. Don't rename without updating the publish workflow.

---

## Tests

xUnit + tests live in [tests/YamlFrontMatter.Tests](tests/YamlFrontMatter.Tests). Two test surfaces:

| File | Tests |
|---|---|
| `SchemaInferenceTests.fs` | Pure unit tests against `inferNodeType`, `mergeTypes`, `inferSchema`. Inputs constructed as plain CLR objects (`box`, `Dictionary<obj,obj>`, `List<obj>`) — no YAML text round-tripping. |
| `Tests.fs` | End-to-end tests against the Type Provider, instantiated with `type Fixtures = SkillCollectionProvider<__SOURCE_DIRECTORY__ + "/Fixtures">`. Compile-time shape assertions (`let _propIsType (s: Fixtures.SkillDefinition) : ExpectedType = s.Prop`) plus xUnit `[<Fact>]` runtime assertions. |

### Conventions

- **Test names**: backtick form, natural-language sentence: `` ` ``GetAll returns exactly 3 skill definitions`` ` ``.
- **Compile-time shape tests** look like unused `let _xxx ...` bindings prefixed with underscore; the type annotation is the assertion.
- **Fixtures**: `tests/YamlFrontMatter.Tests/Fixtures/{minimal,rich,complex}/SKILL.md`. The directory layout is sniffed by the TP via `__SOURCE_DIRECTORY__` — don't reorganize without updating `Tests.fs`.
- **Adding a fixture changes the inferred schema** → may break compile-time shape tests. That's a feature: when the schema changes, those tests are the canary.

### Running a single test

```shell
dotnet test --filter "FullyQualifiedName~minimal_skill_has_required_fields"
```

xUnit's `--filter` matches against `FullyQualifiedName` (case-sensitive); use `~` for substring match. Spaces in backticked test names become underscores in the FQN.

---

## Type Provider authoring

The TP follows the **two-project layout** that's the official `FSharp.TypeProviders.SDK` recommendation:

- [`YamlFrontMatter.TypeProvider`](src/YamlFrontMatter.TypeProvider) — Runtime DLL (TPRTC). Ships in `lib/netstandard2.0/` of the NuGet. Contains `RuntimeHelpers` (called from spliced quotations) and the `[<assembly: TypeProviderAssembly>]` attribute.
- [`YamlFrontMatter.TypeProvider.DesignTime`](src/YamlFrontMatter.TypeProvider.DesignTime) — Design-time DLL (TPDTC). Ships in `typeproviders/fsharp41/netstandard2.0/`. Contains the `[<TypeProvider>]` class. **Compiles `ProvidedTypes.fs[i]` from the SDK NuGet directly into itself** (`<Compile Include="$(NuGetPackageRoot)/fsharp.typeproviders.sdk/8.10.0/src/...">`).

The Runtime references the DesignTime via `<IsFSharpDesignTimeProvider>true</IsFSharpDesignTimeProvider>` + `<PrivateAssets>all</PrivateAssets>` — that's what the SDK packaging targets look for.

For everything deeper (erased vs generative, `args.[i]` semantics, debugging inside Rider/VS Code, the canonical pitfall list), use [`.skills/fsharp-type-provider/SKILL.md`](.skills/fsharp-type-provider/SKILL.md) — it's the authoritative reference, with deeper material under `.skills/fsharp-type-provider/references/` (debugging, packaging, pitfalls, etc.). Saves repeating ~500 lines of TP arcana here.

---

## Known gotchas: Rider + YamlDotNet version conflict

**Symptom (historical, since fixed):** when the TP referenced YamlDotNet, JetBrains Rider's `ReSharperHost.NetCore` process — which preloads its own bundled `YamlDotNet.dll` v5.2.1 — would resolve the TP's `YamlDotNet` reference to the old version, causing `Could not load type 'YamlDotNet.Helpers.IOrderedDictionary\`2'` at design time. VS Code and `dotnet build` worked fine; only Rider broke.

**Fix in this repo:** YAML parsing was migrated to **VYaml**, which Rider does not bundle. Different assembly name → no simple-name unification → no conflict.

**What this means for agents:**
- Don't add `YamlDotNet` as a dependency anywhere in `src/`. Use VYaml.
- If you need a YAML feature VYaml doesn't have, evaluate whether a small custom parser would be cheaper than reintroducing the conflict.
- The same pattern applies to any popular library Rider/VS might preload — check before adding.

[`.skills/fsharp-type-provider/references/pitfalls.md`](.skills/fsharp-type-provider/references/pitfalls.md) has the full catalogue of host-process assembly conflicts and the `addDefaultProbingLocation` flag.

---

## CI and release

[.github/workflows/ci.yml](.github/workflows/ci.yml) — runs on every PR and push to `main`:
- `dotnet restore` / `dotnet build -c Release` / `dotnet test -c Release`
- Uploads `.trx` test results as an artifact

[.github/workflows/publish.yml](.github/workflows/publish.yml) — manual `workflow_dispatch` only:
- Re-runs tests
- Packs `YamlFrontMatter`, `YamlFrontMatter.TypeProvider`, `dotnet-yamlfm`
- Pushes to nuget.org via [Trusted Publishing](https://devblogs.microsoft.com/dotnet/enhanced-security-is-here-with-the-new-trust-publishing-on-nuget-org/) (OIDC, no API key)
- Creates a GitHub Release

**Versioning:** `VersionPrefix` (major.minor) is fixed in [Directory.Build.props](Directory.Build.props); the patch is the GitHub Actions run number. Don't bump versions inside `.fsproj` files.

**For agents preparing a release:** run tests locally first, then trigger `publish.yml` from the Actions tab. Don't push to NuGet from a developer machine — the workflow's OIDC trust scope is the only authorized publisher.

---

## Don't-do list

| Action | Why not |
|---|---|
| Add `<PackageReference Include="YamlDotNet" />` anywhere | Reintroduces the Rider design-time conflict. Use VYaml. |
| Bump `src/` projects from `netstandard2.0` to net8/net10 | Breaks Type Provider loading by the F# compiler. |
| Move TP design-time logic into the runtime project | Defeats the canonical two-project split; will leak design-time deps into consumers. |
| Add `<Version>` or `<Authors>` to individual `.fsproj` files | Duplicates `Directory.Build.props`; inevitable drift. |
| Reorder `<Compile Include>` items in a `.fsproj` | F# compiles top-to-bottom; reordering will break references. |
| Reach for OO inheritance to model domain logic | See `fsharp-style` skill — DUs and pattern matching. |
| Use `try ... with _ -> default` for control flow | Use `Option` / `Result`. Exceptions are for invariants/IO. |
| Add new top-level files without considering whether they belong in a project | If it's per-package metadata it goes in `Directory.Build.props`; if it's a shared resource it goes in `examples/Skills/` or test fixtures. |
| Edit generated files in `bin/` or `obj/` | Self-evident; they're rewritten on every build. |
| Push directly to `main` if CI fails | The CI workflow is the gate — don't bypass. |

---

## Skill references (for AI agents in this repo)

Two skills are **bundled with this repository** under [`.skills/`](.skills/) — they travel with the code so any agent (or human reader) that clones the repo gets the exact style guide and TP authoring guide it was written against:

| Skill | Location | When to load |
|---|---|---|
| `fsharp-style` | [`.skills/fsharp-style/SKILL.md`](.skills/fsharp-style/SKILL.md) | On any `.fs`/`.fsi`/`.fsx`/`.fsproj` edit. Captures the coding preferences this repo follows (single-case DU shape, active patterns, CE usage, anti-patterns). |
| `fsharp-type-provider` | [`.skills/fsharp-type-provider/SKILL.md`](.skills/fsharp-type-provider/SKILL.md) + [`.skills/fsharp-type-provider/references/`](.skills/fsharp-type-provider/references/) | When touching anything in `YamlFrontMatter.TypeProvider*` projects, debugging design-time errors, or packaging changes for the TP. |

These two skills are versioned with the repo. If the codebase's conventions evolve, update the skill alongside the code.

Two further skills exist as Vladimir's personal skills (not bundled here, but commonly available to his agent):

- `domain-modeling-functional-ddd` — when discussing whether something *should* be modelled differently (bounded contexts, workflow shapes, persistence trade-offs). Substitute: [Scott Wlaschin's book](https://pragprog.com/titles/swdddf/domain-modeling-made-functional/).
- `fsharp-csharp-interop` — when the C# example breaks, or when adjusting the public surface of `YamlFrontMatter` for cross-language consumption. Substitute: the [F# / C# interop docs](https://learn.microsoft.com/en-us/dotnet/fsharp/style-guide/component-design-guidelines).

Skills follow progressive-disclosure: `SKILL.md` is the entry, individual `references/*.md` files inside the skill folder are loaded only when needed.

---

## Quick orientation if you're starting cold

1. Read this file (you're here).
2. Skim [README.md](README.md) — public API and usage.
3. Read [src/YamlFrontMatter/Types.fs](src/YamlFrontMatter/Types.fs) — domain types in 50 lines, sets the style.
4. Read [src/YamlFrontMatter.TypeProvider.DesignTime/SkillCollectionProvider.fs](src/YamlFrontMatter.TypeProvider.DesignTime/SkillCollectionProvider.fs) — the Type Provider's body.
5. Read [tests/YamlFrontMatter.Tests/Tests.fs](tests/YamlFrontMatter.Tests/Tests.fs) — what "working correctly" looks like end-to-end.
6. `dotnet test`.

That's the whole onboarding.
