# Project Layout for an F# Type Provider

This reference contains the canonical `.fsproj` files for the three projects that make up a shippable F# Type Provider, plus the two main variants (SDK as source-include vs SDK as PackageReference). Use whichever variant matches the user's existing setup.

---

## Solution layout

```
MyProvider.sln
├── src/
│   ├── MyProvider.Runtime/
│   │   ├── MyProvider.Runtime.fsproj
│   │   └── MyProvider.Runtime.fs
│   └── MyProvider.DesignTime/
│       ├── MyProvider.DesignTime.fsproj
│       └── MyProvider.Provider.fs
└── tests/
    └── MyProvider.Tests/
        ├── MyProvider.Tests.fsproj
        └── MyProvider.Tests.fs
```

The exact directory names are convention; `MyProvider.Runtime` and `MyProvider.DesignTime` map to the names used inside `assemblyReplacementMap` and the `TypeProviderAssembly` attribute, so keep them aligned.

---

## Variant A — SDK as PackageReference (recommended for new projects)

### `MyProvider.Runtime.fsproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
    <FSharpToolsDirectory>typeproviders</FSharpToolsDirectory>
    <PackagePath>typeproviders</PackagePath>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
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

### `MyProvider.Runtime.fs`

```fsharp
namespace MyProvider.Helpers

type SomeRuntimeHelper() =
    static member Help() = "help"

#if !IS_DESIGNTIME
[<assembly: CompilerServices.TypeProviderAssembly("MyProvider.DesignTime.dll")>]
do ()
#endif
```

The `IS_DESIGNTIME` guard prevents the attribute from being emitted into the design-time DLL when the same source is compiled into both projects (a common pattern; see DesignTime project below).

### `MyProvider.DesignTime.fsproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
    <DefineConstants>IS_DESIGNTIME</DefineConstants>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <!-- Include a copy of runtime helpers in the design-time DLL -->
    <Compile Include="..\MyProvider.Runtime\MyProvider.Runtime.fs" />
    <Compile Include="MyProvider.Provider.fs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="FSharp.TypeProviders.SDK" Version="8.10.0">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

`CopyLocalLockFileAssemblies=true` is what makes third-party design-time dependencies (YamlDotNet, Newtonsoft.Json, …) end up next to the design-time DLL on disk. Without it the compiler will fail to load them at design time.

---

## Variant B — SDK as source-include (matches `examples/` in the SDK repo)

Use this when you want zero runtime dependency on the SDK package and full control over the `ProvidedTypes` source version.

### `MyProvider.DesignTime.fsproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
    <DefineConstants>IS_DESIGNTIME</DefineConstants>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="$(NuGetPackageRoot)/fsharp.typeproviders.sdk/8.10.0/src/ProvidedTypes.fsi" />
    <Compile Include="$(NuGetPackageRoot)/fsharp.typeproviders.sdk/8.10.0/src/ProvidedTypes.fs"  />
    <Compile Include="..\MyProvider.Runtime\MyProvider.Runtime.fs" />
    <Compile Include="MyProvider.Provider.fs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="FSharp.TypeProviders.SDK" Version="8.10.0">
      <ExcludeAssets>compile;runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

`<ExcludeAssets>compile;runtime</ExcludeAssets>` keeps the SDK *package* present (so `$(NuGetPackageRoot)` resolves) but prevents the SDK *DLL* from being referenced — the `ProvidedTypes.fs[i]` source files are compiled directly into the design-time assembly.

This is the layout used by the `BasicProvider` examples in the SDK repository and by many real-world TPs (e.g. SQLProvider).

---

## The Tests project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="MyProvider.Tests.fs" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\MyProvider.Runtime\MyProvider.Runtime.fsproj" />
    <ProjectReference Include="..\..\src\MyProvider.DesignTime\MyProvider.DesignTime.fsproj">
      <IsFSharpDesignTimeProvider>true</IsFSharpDesignTimeProvider>
      <PrivateAssets>all</PrivateAssets>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

Note: the Tests project must **also** reference the design-time project with `IsFSharpDesignTimeProvider=true` — without that flag, the compiler picks up only the runtime DLL and never invokes the provider, so all your tests instantiate plain runtime types and pass for the wrong reason.

---

## Multi-targeting

`netstandard2.0` is the safe baseline for the runtime DLL — it is loadable from .NET Framework, .NET Core, and .NET 5+. The design-time DLL also targets `netstandard2.0` historically; modern SDKs increasingly add `net8.0` so the design-time host (which is .NET in modern `dotnet` SDKs) can load a more recent TFM.

Multi-target both projects when in doubt:

```xml
<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
```

The packaging targets place each TFM in its own `typeproviders/fsharp41/<tfm>/` subfolder automatically.

---

## Solution file gotcha

If you use `dotnet sln add`, make sure the Runtime project is added **before** the DesignTime project, and that the Tests project lists Runtime first in its references. The build order matters when `IsFSharpDesignTimeProvider` is used with `PrivateAssets=all`: incorrect order can produce "could not resolve project reference" warnings during `dotnet pack`.
