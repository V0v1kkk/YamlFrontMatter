# Erased vs Generative Type Providers

## Decision flowchart

Ask in this order:

1. **Does the consumer need to use the provided type at runtime via reflection** (e.g. `typeof<MyProvided>`, third-party serializer like `System.Text.Json` reflecting over properties, ORM mapping)?
   - **Yes** → Generative.
   - **No** → continue.
2. **Does the consumer need to inherit from a provided type, or use it as a base class for their own types**?
   - **Yes** → Generative.
   - **No** → continue.
3. **Does the consumer need to pass the provided type across an assembly boundary as a real .NET type** (e.g. a public API surface)?
   - **Yes** → Generative.
   - **No** → continue.
4. **Anything else** → Erased. This is the case ~80% of the time.

If you remain unsure, start erased and migrate later — the migration to generative is mostly mechanical (`isErased = false` + `ProvidedAssembly` + add a constructor + `provAsm.AddTypes`).

---

## Side-by-side comparison

| Aspect | Erased | Generative |
|---|---|---|
| `ProvidedTypeDefinition(..., isErased = ...)` | `true` (default) | `false` |
| Owning assembly object | `Assembly.GetExecutingAssembly()` | `ProvidedAssembly()` |
| Registration | `this.AddNamespace(ns, [t])` | `this.AddNamespace(ns, [t])` **plus** `provAsm.AddTypes [t]` |
| Constructor required? | No | Yes — at least one |
| `args.[0]` in member quotations | First explicit parameter | `this` |
| Runtime representation | `obj` (or whatever you pass as the erased base type) | Real .NET type with proper IL |
| Performance at compile time | Fast | Slower (IL emission) |
| Supports inheritance/serialization | No | Yes |
| Supports `null` literal control via `nonNullable` | Yes | Yes |
| Common in the wild | FSharp.Data.* (JsonProvider, CsvProvider, XmlProvider) | SQLProvider (parts), Microsoft Office providers |

---

## Minimal erased provider

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

        // ctor: invokeCode returns the value the erased base type will hold
        let ctor =
            ProvidedConstructor(
                [ ProvidedParameter("inner", typeof<string>) ],
                invokeCode = fun args -> <@@ (%%(args.[0]) : string) :> obj @@>)
        t.AddMember ctor

        // instance property: args.[0] is the erased base value
        let prop =
            ProvidedProperty(
                "Inner", typeof<string>,
                getterCode = fun args -> <@@ (%%(args.[0]) :> obj) :?> string @@>)
        t.AddMember prop

        [t]

    do this.AddNamespace(ns, createTypes())
```

User-visible result:

```fsharp
let v = MyProvider.Provided.MyType("hello")
printfn "%s" v.Inner   // "hello"
```

At runtime, `v` is genuinely a `string` (because that is what the constructor's `invokeCode` returned). The `MyType` identifier exists only at compile time.

---

## Minimal generative provider with a static parameter

```fsharp
[<TypeProvider>]
type MyGenerativeProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(
        config,
        assemblyReplacementMap = [("MyProvider.DesignTime", "MyProvider.Runtime")])

    let ns  = "MyProvider.Provided"
    let asm = Assembly.GetExecutingAssembly()

    let createType typeName (count: int) =
        let provAsm = ProvidedAssembly()
        let t =
            ProvidedTypeDefinition(provAsm, ns, typeName, Some typeof<obj>,
                                   isErased = false)

        // constructor is mandatory in generative types
        let ctor = ProvidedConstructor([], invokeCode = fun _ -> <@@ () @@>)
        t.AddMember ctor

        for i in 1 .. count do
            // generative: args.[0] is 'this'; we don't need it for a constant getter
            let prop =
                ProvidedProperty(
                    "Property" + string i, typeof<int>,
                    getterCode = fun _ -> <@@ i @@>)
            t.AddMember prop

        provAsm.AddTypes [t]    // mandatory!
        t

    let entry =
        let e = ProvidedTypeDefinition(asm, ns, "Schema", Some typeof<obj>,
                                       isErased = false)
        e.DefineStaticParameters(
            [ ProvidedStaticParameter("Count", typeof<int>) ],
            fun typeName args -> createType typeName (args.[0] :?> int))
        e

    do this.AddNamespace(ns, [entry])
```

User-visible result:

```fsharp
type Five = MyProvider.Provided.Schema<Count = 5>
let v = Five()
printfn "%d %d %d" v.Property1 v.Property2 v.Property5   // 1 2 5
```

`Five` is a real .NET type at runtime, with real IL implementing those properties. `typeof<Five>.GetProperties()` returns five `PropertyInfo`s.

---

## Migration path: erased → generative

Mechanical conversion when consumer requirements change:

1. Add `isErased = false` to every `ProvidedTypeDefinition` call.
2. Construct `let provAsm = ProvidedAssembly()` near the top of `createType`.
3. Pass `provAsm` (instead of `asm`) as the first argument of the *root* `ProvidedTypeDefinition`. Nested types continue to use the parent type as their owner.
4. Ensure every type has at least one `ProvidedConstructor`. If you previously had none, add `ProvidedConstructor([], invokeCode = fun _ -> <@@ () @@>)`.
5. Re-index every `args.[i]`: what was `args.[0]` (first param) becomes `args.[1]`; `this` is now `args.[0]`.
6. Add `provAsm.AddTypes [t]` after the type is fully populated. For nested types use `provAsm.AddNestedTypes([inner], ["Outer"])`.
7. Rebuild — the most common error during migration is a `NullReferenceException` when a quotation captures `args.[0]` expecting a parameter and gets `this`.

---

## What the erased base type controls

The third argument to `ProvidedTypeDefinition` is `Option<Type>`. For erased types, this is the *runtime representation* of values of the provided type. Common choices:

- `Some typeof<obj>` — generic; you'll cast inside quotations as `(%%(args.[0]) :> obj :?> T)`.
- `Some typeof<string>` / `Some typeof<MyRuntimeRecord>` — tighter typing; quotations don't need the cast.
- `None` — only valid for interface-like provided types; rarely needed.

For generative types the third argument is the **base class**, not a runtime representation:

- `Some typeof<obj>` — root in the .NET hierarchy.
- `Some (myBaseProvidedType :> System.Type)` — derive from another provided type (then set `BaseConstructorCall`).
- `Some typeof<Enum>` + `SetEnumUnderlyingType(typeof<int>)` — enum.

---

## When *not* to write a generative provider

- The provided type is short-lived in IDE editing only (e.g. CSV column shape) — erased is faster and simpler.
- You only need static members (no instances) — erased with `isStatic = true` properties is enough.
- You expose the provided type to F# code only and never reflect over it — erased is sufficient.

Generative providers cost compile-time speed and have more failure modes. Reach for them only when erased semantics actually break a real requirement.
