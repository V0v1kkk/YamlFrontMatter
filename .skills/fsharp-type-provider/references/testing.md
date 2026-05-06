# Testing F# Type Providers

Three layers, each catching different failure modes.

## Layer 1 — Unit tests with `ProvidedTypesTesting`

The SDK ships `ProvidedTypesTesting.fs` (in `tests/` of the SDK repo) — a helper that simulates a `TypeProviderConfig` so you can instantiate your provider in-process without invoking `fsc`.

```fsharp
open ProviderImplementation.ProvidedTypes
open ProviderImplementation.ProvidedTypesTesting

let refs = Targets.DotNetStandard20FSharpRefs()
let cfg  = Testing.MakeSimulatedTypeProviderConfig(__SOURCE_DIRECTORY__, refs.[0], refs)
let tp   = MyErasingProvider(cfg) :> TypeProviderForNamespaces

let providedNs   = tp.Namespaces.[0]
let providedType = providedNs.GetTypes().[0]

// Apply static parameters
let applied =
    (tp :> Microsoft.FSharp.Core.CompilerServices.ITypeProvider)
        .ApplyStaticArguments(
            providedType,
            [| "MySchema,\"5\"" |],
            [| box 5 |])

// For generative providers — extract IL bytes and load
let bytes =
    (tp :> Microsoft.FSharp.Core.CompilerServices.ITypeProvider)
        .GetGeneratedAssemblyContents(applied.Assembly)
let asm  = System.Reflection.Assembly.Load(bytes)
let real = asm.GetType("MyProvider.Provided.MySchema,\"5\"")
```

Catches: provider construction errors, member shape errors, missing members, exceptions from `DefineStaticParameters` callbacks.

Misses: anything that depends on the F# compiler actually splicing the quotations into consumer code (i.e. quotation-correctness bugs).

## Layer 2 — Snapshot / golden tests

`Testing.FormatProvidedType` renders a type's members as text, suitable for approval-style tests:

```fsharp
let tp, t =
    Testing.GenerateProvidedTypeInstantiation(
        __SOURCE_DIRECTORY__, refs.[0], refs,
        (fun cfg -> MyErasingProvider(cfg) :> _),
        [| box 3 |])

let snapshot = Testing.FormatProvidedType(tp, t, useQualifiedNames = true)
Assert.Equal(expectedSnapshot, snapshot.Trim())
```

Catches: accidental API changes — a renamed property, a flipped parameter order, a removed XML doc. These don't fail compilation but break consumer source compatibility.

Strategy: commit `expected.txt` files alongside your test source; on intentional API change, update the snapshot in the same commit. The test fails loudly when the diff is unintentional.

## Layer 3 — Integration tests via a real consumer project

The strongest layer. A separate xUnit project that:
- references the **Runtime** project (so the consumer can use `MyProvider.Provided.MyType`)
- references the **DesignTime** project as `IsFSharpDesignTimeProvider=true` (so the compiler invokes the provider during the test build)
- writes ordinary tests that **construct and exercise** the provided types

This is exactly what `examples/BasicProvider.Tests` does in the SDK repo:

```fsharp
module MyProvider.Tests
open MyProvider.Provided
open Xunit

[<Fact>]
let ``ctor produces value`` () =
    Assert.Equal("hello", MyType("hello").Inner)

[<Fact>]
let ``static method works`` () =
    Assert.Equal("Hello, world!", MyType.Greet("world"))
```

For static-parameterised providers:

```fsharp
type Five = MyProvider.Provided.Schema<Count = 5>

[<Fact>]
let ``schema with count 5 has Property5`` () =
    let v = Five()
    Assert.Equal(5, v.Property5)
```

Catches: quotation splicing errors, `assemblyReplacementMap` bugs, missing `provAsm.AddTypes`, packaging-style errors that break design-time loading.

The Tests project must reference the DesignTime project with `IsFSharpDesignTimeProvider=true` — without that flag, the compiler links only against the Runtime DLL and skips the provider entirely. Tests pass for the wrong reason ("MyType is not defined" — but worse: if you have a regular type with the same name, you bind to that one silently).

## Recommended split

| Concern | Layer 1 | Layer 2 | Layer 3 |
|---|---|---|---|
| "Does it construct?" | ✓ | ✓ | ✓ |
| "Are members shaped right?" | ✓ | ✓ | ✓ |
| "Are member contracts stable?" | – | ✓ | partial |
| "Do quotations splice correctly?" | – | – | ✓ |
| "Do generative IL bytes work?" | partial | – | ✓ |
| "Does packaging work?" | – | – | partial — need real `dotnet pack` consumer |

In practice: a small Layer 1 + Layer 2 suite for fast feedback during development, plus a Layer 3 project for confidence that the provider actually works as a TP. Add a manual or CI smoke test that creates a NuGet package and consumes it from a fresh project to catch packaging regressions.

## Performance and flakiness

Type provider tests can be slow because every test build re-invokes the design-time host. Mitigations:
- Keep test schemas small (don't reuse a 10MB JSON sample for unit tests).
- Use `AddMembersDelayed` in the provider so unused members don't materialize in test runs.
- Run integration tests in parallel only if your provider has no shared mutable design-time state.

If a test suddenly fails with "could not load file or assembly", the issue is almost always packaging / dependency bundling, not the test itself — re-run after `dotnet clean && dotnet build` to be sure.
