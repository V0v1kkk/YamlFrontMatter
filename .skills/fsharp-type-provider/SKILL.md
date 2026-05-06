---
name: fsharp-type-provider
description: >
  Guide for AI agents on building F# Type Providers with FSharp.TypeProviders.SDK.
  Use this skill whenever the user asks about creating, debugging, packaging, or testing
  an F# Type Provider (TP), uses terms like ProvidedTypeDefinition, ProvidedProperty,
  ProvidedMethod, ProvidedConstructor, ProvidedAssembly, TypeProviderForNamespaces,
  ProvidedStaticParameter, DefineStaticParameters, AddMembersDelayed, IsFSharpDesignTimeProvider,
  TypeProviderAssembly, ProvidedTypes.fs, erased vs generative providers, design-time vs runtime
  components (TPDTC / TPRTC), assemblyReplacementMap, ProvidedMeasureBuilder, ProvidedTypesTesting,
  CompilerServices.TypeProvider, F# quotations splicing in TPs, or works on an F# `.fsproj` that
  references FSharp.TypeProviders.SDK or sets the IsFSharpDesignTimeProvider MSBuild property.
  Trigger this skill even when the user only says "type provider" in an F#/.NET context — it is the
  authoritative reference for TP authoring in this environment.
metadata:
  author: Based on FSharp.TypeProviders.SDK official docs and examples
  version: '1.0'
  sources: >
    https://github.com/fsprojects/FSharp.TypeProviders.SDK/
    https://fsprojects.github.io/FSharp.TypeProviders.SDK/quick-start.html
    https://fsprojects.github.io/FSharp.TypeProviders.SDK/guide.html
    https://fsprojects.github.io/FSharp.TypeProviders.SDK/providing-types.html
    https://fsprojects.github.io/FSharp.TypeProviders.SDK/packaging.html
    https://fsprojects.github.io/FSharp.TypeProviders.SDK/units-of-measure.html
    https://fsprojects.github.io/FSharp.TypeProviders.SDK/debugging.html
    https://fsprojects.github.io/FSharp.TypeProviders.SDK/technical-notes.html
---

# F# Type Provider Authoring Skill

## When to Use This Skill

Apply this skill whenever the user is:

- Designing or building a new F# Type Provider (TP) from scratch
- Modifying or debugging an existing TP project
- Choosing between an **erased** and **generative** provider
- Setting up the **two-project layout** (`*.Runtime` + `*.DesignTime`) required for shipping a TP
- Packaging a TP as a NuGet package (`typeproviders/fsharp41/...` layout, `IsFSharpDesignTimeProvider`)
- Writing F# **quotations** for `invokeCode`, `getterCode`, `setterCode`, `adderCode`, `removerCode`
- Adding **static parameters** (`DefineStaticParameters`, `ProvidedStaticParameter`)
- Testing a TP via `ProvidedTypesTesting`, snapshot/golden tests, or integration tests
- Debugging "design-time DLL not loaded" / "could not load assembly" / "Type mismatch" / "metadata token" errors
- Working with **units of measure** in a provider (`ProvidedMeasureBuilder`)
- Bundling design-time third-party dependencies (e.g. `YamlDotNet`, `Newtonsoft.Json`) alongside the TPDTC

If the conversation already contains an `.fsproj` that references `FSharp.TypeProviders.SDK`, treat that as a strong trigger and load this skill.

---

## Mental Model: The Two-Component Architecture

Every shippable F# Type Provider is **two assemblies**, not one:

| Component | Acronym | Purpose | Where it ships in NuGet |
|---|---|---|---|
| Runtime | TPRTC | Referenced by user code; contains runtime helpers and the `TypeProviderAssembly` attribute pointing at the TPDTC | `lib/<tfm>/MyProvider.dll` |
| Design-Time | TPDTC | Loaded **into the F# compiler / IDE** at design time; produces the provided types | `typeproviders/fsharp41/<tfm>/MyProvider.DesignTime.dll` |

Why two? The compiler needs to **execute** TP code at design time. That code (and all its dependencies — YAML parsers, DB drivers, etc.) must not pollute the user's runtime closure, and conversely the user's runtime helpers must not be loaded into the IDE.

The `TypeProviderAssembly` attribute on the runtime DLL tells the compiler where to find the design-time DLL:

```fsharp
[<assembly: CompilerServices.TypeProviderAssembly("MyProvider.DesignTime.dll")>]
do ()
```

If you forget this attribute, the compiler will reference your runtime DLL but never invoke your provider.

### Single-project shortcut (rare)

You may set `<IsFSharpDesignTimeProvider>true</IsFSharpDesignTimeProvider>` on **one** project and ship a single DLL. Acceptable for small zero-dependency providers; use the two-project layout otherwise — it is the canonical pattern in `FSharp.TypeProviders.SDK/examples/BasicProvider.*`.

---

## Mental Model: Erased vs Generative

This is the **first decision** when starting a new provider. Pick erased unless something forces generative.

| | Erased | Generative |
|---|---|---|
| Types exist at runtime? | No — replaced by erased base type (usually `obj`) | Yes — emitted as real .NET IL into a `ProvidedAssembly` |
| Inheritance / serialization / runtime reflection over the provided type? | ✗ | ✓ |
| Constructor required on every type? | No | **Yes — at least one** |
| Performance overhead | Lower | Higher (IL generation) |
| `args.[0]` in quotations is | first explicit parameter | `this` |
| Typical use case | schema-driven data access (JSON/CSV/DB shapes) | code generation, DTOs, mocking, anything reflected at runtime |

**Default to erased.** Only choose generative when the user *must* be able to do runtime reflection, serialize the provided type with a third-party serializer, inherit from it, or pass it across an assembly boundary that requires real metadata.

For a deeper comparison and decision checklist, see [references/erased-vs-generative.md](references/erased-vs-generative.md).

---

## Authoring Workflow (Happy Path)

Follow this order. Skipping the project-layout step is the most common reason TPs "build but don't work".

### 1. Scaffold the project layout

Either run the official template:

```text
dotnet new install FSharp.TypeProviders.Templates
dotnet new typeprovider -n MyProvider -lang F#
cd MyProvider
dotnet tool restore
dotnet paket update
dotnet build -c Release
dotnet test  -c Release
```

…or hand-write the layout (preferred when adding a TP into an existing solution). Three projects:

```
MyProvider.Runtime/      ← shipped to users; runtime helpers + TypeProviderAssembly attr
MyProvider.DesignTime/   ← loaded by compiler; ProvidedTypes.fs[i] + your provider class
MyProvider.Tests/        ← references only Runtime
```

The Runtime project file looks like:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
    <FSharpToolsDirectory>typeproviders</FSharpToolsDirectory>
    <PackagePath>typeproviders</PackagePath>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="MyProvider.Runtime.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\MyProvider.DesignTime\MyProvider.DesignTime.fsproj">
      <IsFSharpDesignTimeProvider>true</IsFSharpDesignTimeProvider>
      <PrivateAssets>all</PrivateAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

`IsFSharpDesignTimeProvider=true` + `PrivateAssets=all` is the magic combo: it
- triggers the SDK's `CollectFSharpDesignTimeTools` / `PackageFSharpDesignTimeTools` MSBuild targets,
- bundles the design-time DLL into `typeproviders/fsharp41/<tfm>/`,
- prevents the design-time project from leaking into the consumer's NuGet dependency graph.

The DesignTime project either compiles `ProvidedTypes.fs[i]` directly:

```xml
<Compile Include="$(NuGetPackageRoot)/fsharp.typeproviders.sdk/8.10.0/src/ProvidedTypes.fsi" />
<Compile Include="$(NuGetPackageRoot)/fsharp.typeproviders.sdk/8.10.0/src/ProvidedTypes.fs" />
<Compile Include="MyProvider.Provider.fs" />
```
…with `<ExcludeAssets>compile;runtime</ExcludeAssets>` on the SDK PackageReference, **or** uses the SDK as a normal package reference. The `examples/BasicProvider.DesignTime` project in the SDK repo uses the source-include form.

For full project files, see [references/project-layout.md](references/project-layout.md).

### 2. Pick the runtime helpers

Anything the **compiled user program** will need at runtime goes in the Runtime DLL. For erased providers this is small (often just data classes used inside quotations). For generative providers there is typically nothing — generated IL is self-contained.

Do **not** put schema-discovery / parsing / I/O code in the Runtime DLL — that runs at design time only and belongs in DesignTime.

### 3. Write the provider class

Minimum viable erased provider:

```fsharp
namespace MyProvider.DesignTime

open System.Reflection
open ProviderImplementation.ProvidedTypes
open Microsoft.FSharp.Core.CompilerServices

[<TypeProvider>]
type MyErasingProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(
        config,
        assemblyReplacementMap =
            [("MyProvider.DesignTime", "MyProvider.Runtime")],
        addDefaultProbingLocation = true)

    let ns  = "MyProvider.Provided"
    let asm = Assembly.GetExecutingAssembly()

    let createTypes () =
        let t = ProvidedTypeDefinition(asm, ns, "MyType", Some typeof<obj>)
        let prop =
            ProvidedProperty(
                "Greeting", typeof<string>, isStatic = true,
                getterCode = fun _ -> <@@ "Hello from MyType!" @@>)
        t.AddMember prop
        [t]

    do this.AddNamespace(ns, createTypes())
```

Key invariants:
- `[<TypeProvider>]` attribute on the class (the F# compiler scans for it).
- `inherit TypeProviderForNamespaces(config, ...)` — never instantiate it without `config`.
- `assemblyReplacementMap` rewrites references in spliced quotations from the design-time assembly to the runtime assembly.
- `addDefaultProbingLocation = true` is recommended in modern .NET SDK projects so the design-time host can resolve sibling DLLs.
- `this.AddNamespace(ns, [...])` is the only way provided types become visible to consumers.

For generative providers and parameterized providers, see [references/erased-vs-generative.md](references/erased-vs-generative.md) and [references/members-deep-dive.md](references/members-deep-dive.md).

### 4. Add members

The five member kinds and their canonical signatures:

```fsharp
// Property (erased: args.[0] = this; setterCode optional)
ProvidedProperty(name, t, getterCode = fun args -> <@@ ... @@>, ?setterCode, ?isStatic)

// Method (erased: args.[0] = first param; generative: args.[0] = this)
ProvidedMethod(name, [ProvidedParameter("p", typeof<int>)], returnType,
               invokeCode = fun args -> <@@ ... @@>, ?isStatic)

// Constructor (generative: at least one is mandatory)
ProvidedConstructor(parameters, invokeCode = fun args -> <@@ ... @@>)

// Field (generative only)
ProvidedField("_x", typeof<int>)
ProvidedField.Literal("MaxSize", typeof<int>, 100)   // enum constants

// Event
ProvidedEvent(name, typeof<EventHandler>,
              adderCode = ..., removerCode = ...)
```

Three details that bite people:
- **`args.[0]` semantics differ** between erased and generative providers. Erased: it is the first explicit parameter. Generative: it is `this` (so add 1 to every index for parameters). The same quotation written for the wrong kind crashes at design time with confusing splicing errors.
- **`%%(args.[0]) : T`** is how you both splice the captured expression *and* assert its type. The `%%` operator unsplices an `Expr` into the surrounding quotation; the `:T` annotation tells the compiler the erased base type's expected real shape.
- For **large schemas**, prefer `myType.AddMembersDelayed(fun () -> [...])` over `AddMember` in a loop — members are then materialised on first IntelliSense / type-check demand instead of eagerly.

For the full member catalogue — including XML docs (`AddXmlDoc`, `AddXmlDocDelayed`), custom attributes (`AddCustomAttribute`), interfaces, enums, abstract classes, units of measure, and `BaseConstructorCall` — see [references/members-deep-dive.md](references/members-deep-dive.md).

### 5. Static parameters

Lets a consumer write `MyProvider.Schema<Count = 5>`:

```fsharp
let containerType = ProvidedTypeDefinition(asm, ns, "Schema", Some typeof<obj>)
containerType.DefineStaticParameters(
    [ ProvidedStaticParameter("Count", typeof<int>) ],
    fun typeName args -> createType typeName (args.[0] :?> int))
this.AddNamespace(ns, [containerType])
```

Optional static parameters: `ProvidedStaticParameter("Prefix", typeof<string>, parameterDefaultValue = "Col")`. If **all** parameters have defaults, the SDK emits a warning — that is intentional, not a bug.

The container type itself is still visible (un-applied); it is only useful as the parameter target. Common pattern: name it after the consumer-facing entry point (`JsonProvider`, `SqlDataConnection`, `Schema`).

### 6. Generative IL emission

Skip if you are writing an erased provider.

```fsharp
let provAsm = ProvidedAssembly()
let myType  = ProvidedTypeDefinition(provAsm, ns, typeName, Some typeof<obj>, isErased = false)
// ...add members, including at least one ProvidedConstructor...
provAsm.AddTypes [myType]
```

Two non-negotiable rules:
1. Every generative type **must** have at least one `ProvidedConstructor` (even an empty one).
2. Every generative type **must** be registered with `provAsm.AddTypes` — forgetting this produces an empty assembly and a "type not found" error at compile time of the consumer.

For nested generative types use `provAsm.AddNestedTypes([innerType], ["Outer"])`.

Inside quotations that build generic types, **always** use `ProvidedTypeBuilder.MakeGenericType(...)` and `ProvidedTypeBuilder.MakeGenericMethod(...)`. The plain `Type.MakeGenericType` works locally but breaks once the assembly is loaded by the compiler (cryptic "Type mismatch" or `InvalidOperationException` on metadata token access).

### 7. Test

The SDK ships `ProvidedTypesTesting` (in `tests/ProvidedTypesTesting.fs` or available via the SDK package) which simulates a `TypeProviderConfig` so you can drive the provider without invoking `fsc`:

```fsharp
open ProviderImplementation.ProvidedTypesTesting
let refs = Targets.DotNetStandard20FSharpRefs()
let cfg  = Testing.MakeSimulatedTypeProviderConfig(__SOURCE_DIRECTORY__, refs.[0], refs)
let tp   = MyErasingProvider(cfg) :> TypeProviderForNamespaces
```

For end-to-end behaviour, the simplest working setup is a separate **Tests** project that references only the **Runtime** project plus the design-time provider via the same `IsFSharpDesignTimeProvider` mechanism, then writes ordinary xUnit tests over the provided types — exactly like `examples/BasicProvider.Tests` in the SDK repo:

```fsharp
open BasicProvider.Provided
[<Fact>]
let ``ctor produces value`` () =
    Assert.Equal("My internal state", MyType().InnerState)
```

For snapshot/golden tests, debugging, and full test patterns, see [references/testing.md](references/testing.md).

### 8. Package

Top of mind: the **runtime project** owns the package; the design-time project is consumed only as a `<ProjectReference>` with `IsFSharpDesignTimeProvider=true`.

Resulting NuGet layout (produced automatically by the SDK targets when properties are set correctly):

```
lib/netstandard2.0/MyProvider.dll                              (TPRTC)
typeproviders/fsharp41/netstandard2.0/MyProvider.DesignTime.dll (TPDTC)
typeproviders/fsharp41/netstandard2.0/<all design-time deps>.dll
```

Critical: every design-time third-party dependency (e.g. `YamlDotNet.dll`, `Newtonsoft.Json.dll`) must land **next to** `MyProvider.DesignTime.dll`. The compiler probes only that directory. Achieve this with `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` on the design-time project, or by explicit `<Content Include="...dll" CopyToOutputDirectory="PreserveNewest" />` items.

Full packaging walkthrough including `paket.template`, multi-targeting, and "design-time dep not found" troubleshooting: [references/packaging.md](references/packaging.md).

### 9. Debug

Standard recipe:

1. Run `dotnet build -v:n` against the consumer project; copy the recorded `fsc` invocation and its `@args.txt` response file.
2. `devenv /debugexe "<path>/dotnet.exe" "<path>/fsc.exe" @args.txt` — set the debug type to ".NET Core".
3. Enable first-chance CLR exceptions (`Ctrl+Alt+E`) and disable Just-My-Code.
4. Place a breakpoint inside `createTypes` / `DefineStaticParameters` callbacks.

Common errors and their roots are catalogued in [references/debugging.md](references/debugging.md) and [references/pitfalls.md](references/pitfalls.md).

---

## The Six Most Common Pitfalls

Memorise these — they account for most "TP builds but doesn't work" reports.

| # | Pitfall | Fix |
|---|---|---|
| 1 | `args.[0]` semantics swapped | Erased: first param. Generative: `this`. Re-index. |
| 2 | Plain `Type.MakeGenericType` inside quotations | Use `ProvidedTypeBuilder.MakeGenericType` / `MakeGenericMethod` |
| 3 | Generative type with no constructor | Add at least one `ProvidedConstructor` (may be empty) |
| 4 | Forgot `provAsm.AddTypes [myType]` | Required for every generative type — including nested via `AddNestedTypes` |
| 5 | Missing `[<assembly: TypeProviderAssembly("...DesignTime.dll")>]` on Runtime DLL | Add it; otherwise compiler never invokes the provider |
| 6 | Design-time dependency not bundled | Set `CopyLocalLockFileAssemblies=true` on DesignTime, or `<Content Include="..." CopyToOutputDirectory="PreserveNewest" />` |

A longer catalogue with reproductions and root-cause diagnostics: [references/pitfalls.md](references/pitfalls.md).

---

## Style and Communication Guidance

When helping a user build a TP:

- **Lead with the choice**: erased or generative? Don't write code until that is settled.
- **Show the project layout first**, then the provider class. Layout problems are invisible to grep and waste hours.
- **Quote runtime behaviour as F# quotations from the start** — don't write "imperative" pseudo-code that you then translate. The shape of the quotation drives the API.
- **Explain `args.[i]` indexing explicitly** every time you write a member, until the user is comfortable. The single most common cause of "works locally, breaks in IDE" reports.
- **Default to small reproductions**: a 30-line erased provider that returns `42` from a single property is a better starting point than scaffolding a generative provider with static parameters and seven member kinds.
- For users with an existing TP project, **read the actual `.fsproj` files** before suggesting a layout change — many real projects use the source-include form (`Compile Include="$(NuGetPackageRoot)/.../ProvidedTypes.fs"`) which is fully supported but easy to misread.

---

## Reference Index

When the user's question is deep in one of these areas, load the matching reference file:

- [references/project-layout.md](references/project-layout.md) — full `.fsproj` examples for Runtime, DesignTime, Tests; SDK source-include vs PackageReference; multi-targeting
- [references/erased-vs-generative.md](references/erased-vs-generative.md) — decision flowchart, behavioural differences, full code samples for both kinds
- [references/members-deep-dive.md](references/members-deep-dive.md) — every member kind, XML docs, custom attributes, delayed members, interfaces, enums, abstract classes, units of measure
- [references/quotations.md](references/quotations.md) — `<@@ ... @@>`, `%%`, splicing rules, `args.[i]` semantics, `Expr.CallUnchecked`
- [references/static-parameters.md](references/static-parameters.md) — single & multiple, optional with defaults, `ApplyStaticArguments`
- [references/packaging.md](references/packaging.md) — NuGet layout, `IsFSharpDesignTimeProvider`, `paket.template`, dependency bundling
- [references/testing.md](references/testing.md) — `ProvidedTypesTesting`, snapshot/golden, integration tests
- [references/debugging.md](references/debugging.md) — attaching to `fsc.exe` / `dotnet fsi` / VS / Ionide, common error messages
- [references/pitfalls.md](references/pitfalls.md) — extended catalogue of failure modes and fixes
- [examples/minimal-erased.md](examples/minimal-erased.md) — complete working "Hello World" erased provider
- [examples/generative-with-static-params.md](examples/generative-with-static-params.md) — complete working generative provider with `Count` static parameter
