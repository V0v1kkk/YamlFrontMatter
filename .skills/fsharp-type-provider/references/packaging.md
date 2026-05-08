# Packaging F# Type Providers as NuGet

## Target NuGet layout

```
MyProvider.<version>.nupkg
├── lib/
│   ├── netstandard2.0/MyProvider.dll        (TPRTC)
│   └── net8.0/MyProvider.dll
└── typeproviders/
    └── fsharp41/
        ├── netstandard2.0/
        │   ├── MyProvider.DesignTime.dll    (TPDTC)
        │   ├── YamlDotNet.dll               (bundled design-time dep)
        │   └── ...                          (every other design-time dep)
        └── net8.0/
            └── (same set, framework-specific copies)
```

The compiler probes only `typeproviders/fsharp41/<tfm>/` for the design-time DLL and its dependencies. Anything not in that directory at consumer compile time is invisible to the provider.

## How `IsFSharpDesignTimeProvider` produces this layout

Three places it is meaningful:

### 1. On a `<ProjectReference>` from Runtime → DesignTime

```xml
<ProjectReference Include="..\MyProvider.DesignTime\MyProvider.DesignTime.fsproj">
  <IsFSharpDesignTimeProvider>true</IsFSharpDesignTimeProvider>
  <PrivateAssets>all</PrivateAssets>
</ProjectReference>
```

This triggers the SDK's `CollectFSharpDesignTimeTools` and `PackageFSharpDesignTimeTools` MSBuild targets when the runtime project is packed. Without it, `dotnet pack` emits a normal `lib/` package and your TP doesn't ship.

`PrivateAssets=all` keeps the design-time project from appearing as a NuGet *dependency* in the consumer's resolved graph.

### 2. As a project-level property on a single-DLL TP

```xml
<PropertyGroup>
  <IsFSharpDesignTimeProvider>true</IsFSharpDesignTimeProvider>
</PropertyGroup>
```

Use only for very simple zero-dependency providers shipped as one DLL. The DLL ends up in both `lib/` and `typeproviders/fsharp41/<tfm>/`. Not recommended for anything non-trivial — the two-project split is cleaner.

### 3. On a `<PackageReference>` to another package whose DesignTime is published separately

Rare; only matters if the design-time component is itself shipped as a NuGet package consumed by your runtime package.

## Path properties

| Property | Default | Should be |
|---|---|---|
| `FSharpToolsDirectory` | `tools` | `typeproviders` (set on the Runtime project) |
| `FSharpDesignTimeProtocol` | `fsharp41` | leave as default |
| `PackagePath` | computed | `typeproviders` (often set alongside `FSharpToolsDirectory`) |

```xml
<PropertyGroup>
  <FSharpToolsDirectory>typeproviders</FSharpToolsDirectory>
  <PackagePath>typeproviders</PackagePath>
</PropertyGroup>
```

Older SDKs emitted the design-time DLL under `tools/` rather than `typeproviders/`. Modern SDK versions (2020+) recognize both, but `typeproviders` is the canonical path and the only one new tooling guarantees support for.

## Bundling design-time dependencies

The single most common packaging bug: the TP design-time DLL has a `PackageReference` to (say) `YamlDotNet`, but the resulting `.nupkg` has `YamlDotNet` listed as a regular runtime dependency rather than physically present in `typeproviders/fsharp41/<tfm>/`. The compiler then fails to load the TP at consumer build time.

Two ways to fix:

### A. `CopyLocalLockFileAssemblies`

```xml
<PropertyGroup>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
```

On the **DesignTime** project. Forces all transitive dependencies to be copied into `bin/$(Configuration)/$(TargetFramework)/`. The SDK's packaging targets then sweep that folder into the package's `typeproviders/` layout.

### B. Explicit `<Content>` items

```xml
<ItemGroup>
  <Content Include="..\..\packages\YamlDotNet\lib\netstandard2.0\YamlDotNet.dll"
           CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Use only when (A) doesn't work — for example, when the dependency is a non-NuGet binary. Brittle: hard-codes paths and breaks when versions change.

### Make the dep "private" so consumers don't transitively reference it

```xml
<PackageReference Include="YamlDotNet" Version="17.1.0">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

`PrivateAssets=all` keeps `YamlDotNet` out of the consumer's NuGet dependency graph. Combined with `CopyLocalLockFileAssemblies=true`, the dep is bundled into `typeproviders/` for the design-time host and invisible to runtime.

## paket.template (if using Paket)

```text
type
    project
id
    MyProvider
authors
    You
description
    My Type Provider
files
    bin/Release/netstandard2.0/MyProvider.dll                 ==> lib/netstandard2.0
    ../MyProvider.DesignTime/bin/Release/netstandard2.0/      ==> typeproviders/fsharp41/netstandard2.0
    ../MyProvider.DesignTime/bin/Release/net8.0/              ==> typeproviders/fsharp41/net8.0
```

The trailing `/` on a source path means "all files in this directory" — handy for sweeping in design-time deps without listing each.

## Multi-targeting

When the TP supports multiple TFMs, the runtime project's `<TargetFrameworks>` drives the `lib/` layout, and the design-time project's `<TargetFrameworks>` drives the `typeproviders/fsharp41/` layout. They don't have to match, but it's simplest if they do.

```xml
<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
```

Some projects target only `netstandard2.0` for the runtime (broadest reach) and add `net8.0` only for the design-time component (fastest design-time host).

## Quick verification checklist after `dotnet pack`

```bash
unzip -l bin/Release/MyProvider.<version>.nupkg | grep -E '(\.dll$|MyProvider)'
```

Confirm:
- `lib/<tfm>/MyProvider.dll` is present
- `typeproviders/fsharp41/<tfm>/MyProvider.DesignTime.dll` is present
- Every design-time dep DLL appears next to it
- No `MyProvider.DesignTime.dll` accidentally in `lib/`

If a dep is missing, check `bin/Release/<tfm>/` of the design-time project — was it copied? If not, fix `CopyLocalLockFileAssemblies` or `Content Include`. If it's there but didn't make it into the package, the `IsFSharpDesignTimeProvider` link from Runtime → DesignTime is missing or malformed.

## CI gotcha: prefer `dotnet pack <sln>` over `dotnet pack <fsproj>`

Field-tested observation (2026): on Linux GitHub Actions runners with `dotnet 10.0.x`, packing the runtime project individually (`dotnet pack src/MyProvider.TypeProvider/MyProvider.TypeProvider.fsproj -c Release /p:Version=X.Y.Z -o artifacts`) **intermittently drops the design-time DLL** from the resulting `.nupkg`. The published package shows up with only `lib/<tfm>/MyProvider.TypeProvider.dll` — no `typeproviders/` folder at all, even though the same commit + same SDK version on a developer machine packs the package correctly with all design-time DLLs included.

The exact root cause is unclear (likely a race between MSBuild's incremental rebuild triggered by `/p:Version=...` and the SDK target `CollectFSharpDesignTimeTools` which depends on the design-time project's bin output being present), but the workaround is reliable:

```yaml
# Bad — works locally, intermittently produces empty packages on CI:
- run: dotnet pack src/MyProvider/MyProvider.fsproj -c Release /p:Version=...
- run: dotnet pack src/MyProvider.TypeProvider/MyProvider.TypeProvider.fsproj -c Release /p:Version=...

# Good — single command, packs every IsPackable=true project in solution
# in one MSBuild invocation, no cross-pack timing window:
- run: dotnet pack MyProvider.sln -c Release /p:Version=...
```

To keep individual projects out of the artifact set, mark them `<IsPackable>false</IsPackable>` in their `.fsproj` (DesignTime and Tests projects already need this anyway).

**How to spot the bug**: after `dotnet pack`, inspect the package size. A correct TPDTC-bundled package is typically several hundred KB to multiple MB (it carries `ProvidedTypes.fs[i]` compiled in, plus all design-time deps). A broken one with only the runtime stub is usually under 50 KB. If your TP package is 25-30 KB, check the layout — it's almost certainly missing the design-time component.

## Common error: `Could not load file or assembly 'YamlDotNet'`

Surface: consumer compiles, F# emits "could not load assembly YamlDotNet" pointing at the design-time DLL.

Diagnosis steps:
1. `unzip -l <consumer>/obj/<config>/<tfm>/<asset>.deps.json` — is YamlDotNet there?
2. `find ~/.nuget/packages/myprovider/<version>/typeproviders -name '*.dll'` — is YamlDotNet bundled?
3. If not bundled: fix the design-time project's `CopyLocalLockFileAssemblies` or `Content Include`, repack.
4. If bundled but compiler still complains: the version in `typeproviders/` differs from a version the design-time host already has loaded — use `<PrivateAssets>all</PrivateAssets>` to prevent the mismatch.
