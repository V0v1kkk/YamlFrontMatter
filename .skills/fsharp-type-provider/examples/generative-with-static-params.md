# Complete Generative Type Provider with Static Parameter

Same project layout as [minimal-erased.md](minimal-erased.md). Only the provider source differs.

## src/MyProvider.DesignTime/MyProvider.Provider.fs

```fsharp
namespace MyProvider.DesignTime

open System.Reflection
open ProviderImplementation.ProvidedTypes
open Microsoft.FSharp.Core.CompilerServices

[<TypeProvider>]
type MyGenerativeProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(
        config,
        assemblyReplacementMap =
            [("MyProvider.DesignTime", "MyProvider.Runtime")],
        addDefaultProbingLocation = true)

    let ns  = "MyProvider.Provided"
    let asm = Assembly.GetExecutingAssembly()

    // For each requested Count, build a generative type with N integer properties.
    let createType typeName (count: int) =
        let provAsm = ProvidedAssembly()
        let t =
            ProvidedTypeDefinition(provAsm, ns, typeName, Some typeof<obj>,
                                   isErased = false,
                                   hideObjectMethods = true)

        // Generative types REQUIRE at least one ProvidedConstructor.
        // args.[0] is 'this' in generative providers; we don't need it here.
        let ctor = ProvidedConstructor([], invokeCode = fun _ -> <@@ () @@>)
        t.AddMember ctor

        for i in 1 .. count do
            let prop =
                ProvidedProperty(
                    "Property" + string i, typeof<int>,
                    getterCode = fun _ -> <@@ i @@>)
            prop.AddXmlDoc(sprintf "Returns the integer literal %d." i)
            t.AddMember prop

        // Mandatory: register the type with the ProvidedAssembly.
        provAsm.AddTypes [t]
        t

    let entry =
        let e =
            ProvidedTypeDefinition(asm, ns, "Schema", Some typeof<obj>,
                                   isErased = false)
        e.DefineStaticParameters(
            [ ProvidedStaticParameter("Count", typeof<int>) ],
            fun typeName args -> createType typeName (args.[0] :?> int))
        e

    do this.AddNamespace(ns, [entry])

[<assembly: TypeProviderAssembly>]
do ()
```

## tests/MyProvider.Tests/MyProvider.Tests.fs

```fsharp
module MyProvider.Tests
open Xunit

type Five  = MyProvider.Provided.Schema<Count = 5>
type Three = MyProvider.Provided.Schema<Count = 3>

[<Fact>]
let ``Five has 5 sequential properties`` () =
    let v = Five()
    Assert.Equal(1, v.Property1)
    Assert.Equal(3, v.Property3)
    Assert.Equal(5, v.Property5)

[<Fact>]
let ``Three has only 3 properties`` () =
    let v = Three()
    Assert.Equal(1, v.Property1)
    Assert.Equal(3, v.Property3)
    // v.Property4 would not compile - the type doesn't have it

[<Fact>]
let ``Reflecting over Five sees real .NET properties`` () =
    let props = typeof<Five>.GetProperties()
    Assert.Equal(5, props.Length)   // generative => real reflection

[<Fact>]
let ``Two specializations produce distinct types`` () =
    Assert.NotEqual<System.Type>(typeof<Five>, typeof<Three>)
```

## What this demonstrates

- `isErased = false` and `ProvidedAssembly()` for generative types
- The mandatory `ProvidedConstructor` (without it, consumer cannot do `Five()`)
- `provAsm.AddTypes [t]` — forgetting this is the most common generative bug
- `DefineStaticParameters` with one required `int` parameter
- Real .NET metadata: `typeof<Five>.GetProperties()` returns 5 real `PropertyInfo`s

## Compare to the erased version

If you replace `isErased = false` with `isErased = true` and remove `ProvidedAssembly` / `provAsm.AddTypes`, the consumer can still write `Five()` and access `Property5`, but `typeof<Five>.GetProperties()` returns whatever the erased base type exposes (probably nothing useful for `obj`). For data-access scenarios this is fine; for code-generation or serialization it is not.

## Try it

```bash
dotnet build
dotnet test
```

If the reflection test fails ("Expected 5, got 0"), check that `provAsm.AddTypes [t]` is present and that `isErased = false` on the *generated* type (not just on the static-parameter container).
