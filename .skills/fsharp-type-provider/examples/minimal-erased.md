# Complete Minimal Erased Type Provider

The smallest fully-working erased provider. Three files; runs end-to-end.

## Project layout

```
MyProvider/
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

## src/MyProvider.Runtime/MyProvider.Runtime.fsproj

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

## src/MyProvider.Runtime/MyProvider.Runtime.fs

```fsharp
namespace MyProvider.Helpers

type Row(name: string) =
    member _.Name = name

#if !IS_DESIGNTIME
[<assembly: CompilerServices.TypeProviderAssembly("MyProvider.DesignTime.dll")>]
do ()
#endif
```

## src/MyProvider.DesignTime/MyProvider.DesignTime.fsproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
    <DefineConstants>IS_DESIGNTIME</DefineConstants>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
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

## src/MyProvider.DesignTime/MyProvider.Provider.fs

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
        let t =
            ProvidedTypeDefinition(asm, ns, "MyType", Some typeof<obj>,
                                   hideObjectMethods = true)

        // Constructor: name -> Row
        let ctor =
            ProvidedConstructor(
                [ ProvidedParameter("name", typeof<string>) ],
                invokeCode = fun args ->
                    <@@ MyProvider.Helpers.Row(%%(args.[0]) : string) :> obj @@>)
        ctor.AddXmlDoc "Creates a new MyType with the given name."
        t.AddMember ctor

        // Instance property: reads .Name from the underlying Row
        let nameProp =
            ProvidedProperty(
                "Name", typeof<string>,
                getterCode = fun args ->
                    <@@ ((%%(args.[0]) : obj) :?> MyProvider.Helpers.Row).Name @@>)
        nameProp.AddXmlDoc "The name supplied to the constructor."
        t.AddMember nameProp

        // Static method
        let greet =
            ProvidedMethod(
                "Greet", [ ProvidedParameter("who", typeof<string>) ],
                typeof<string>, isStatic = true,
                invokeCode = fun args ->
                    <@@ "Hello, " + (%%(args.[0]) : string) + "!" @@>)
        greet.AddXmlDoc "Returns a greeting for the given name."
        t.AddMember greet

        [t]

    do this.AddNamespace(ns, createTypes())

[<assembly: TypeProviderAssembly>]
do ()
```

## tests/MyProvider.Tests/MyProvider.Tests.fsproj

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

## tests/MyProvider.Tests/MyProvider.Tests.fs

```fsharp
module MyProvider.Tests
open MyProvider.Provided
open Xunit

[<Fact>]
let ``MyType ctor stores Name`` () =
    let v = MyType("alice")
    Assert.Equal("alice", v.Name)

[<Fact>]
let ``Greet returns greeting`` () =
    Assert.Equal("Hello, world!", MyType.Greet("world"))
```

## Build & run

```bash
dotnet build
dotnet test
```

Expected: all tests pass. If any test fails with "type does not exist", check that the test project's `<ProjectReference>` to DesignTime has `IsFSharpDesignTimeProvider=true`.
