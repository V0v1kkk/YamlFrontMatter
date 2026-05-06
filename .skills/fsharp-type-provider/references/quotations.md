# Quotations in Type Providers

## What quotations are doing

The body of every member callback (`invokeCode`, `getterCode`, `setterCode`, `adderCode`, `removerCode`, `BaseConstructorCall`) is an F# **quotation** — `Microsoft.FSharp.Quotations.Expr` — that the compiler **splices** into the consumer's call site at compile time. The provider returns code-as-data; the compiler treats it as if the user had written it directly.

This is the entire model. There is no IL emission for erased members; the quotation *is* the implementation.

---

## The two key operators

| Syntax | Meaning |
|---|---|
| `<@@ expr @@>` | Build an untyped quotation (`Expr`). |
| `%%(e)` | Splice an `Expr` into the surrounding quotation, untyped. |
| `<@ expr @>` | Build a typed quotation (`Expr<'T>`). Used less often. |
| `%(e)` | Splice a typed `Expr<'T>`. |

In type providers, **untyped** quotations (`<@@ ... @@>` / `%%`) are dominant because parameter `Expr` values are weakly typed.

The idiom `%%(args.[0]) : T` is "splice this expression and tell the compiler to treat it as type `T`". It's both a splice and a type ascription.

---

## `args.[i]` semantics

The `args` parameter to every code callback is an `Expr list`. Each element is an expression that, at the call site, will represent one of:

- the receiver (`this`), if any
- a parameter passed by the caller

The exact mapping depends on whether the type is erased or generative, and whether the member is static or instance:

| Member | Erased | Generative |
|---|---|---|
| Static method/constructor | `args.[0]` = first param | `args.[0]` = first param |
| Instance method/property | `args.[0]` = receiver, `args.[1]` = first param | `args.[0]` = `this`, `args.[1]` = first param |
| Static property getter | first param… but typically no params | same |
| Generative ctor | n/a | `args.[0]` = `this`, `args.[1]` = first param |
| Erased ctor | `args.[0]` = first param (no `this`) | n/a |

Practical rule:
- **Erased ctors are special — no `this`.** Indexing starts at the first explicit parameter.
- **Everything else with a receiver puts the receiver at index 0.**
- **Static members never have a receiver.**

When you can't remember, write the simplest possible quotation, build, and read the splicing error — F# will tell you the mismatched index.

---

## Reading from the erased base type

For an erased instance property whose erased base is `obj` and whose actual runtime value is a `string`:

```fsharp
let prop =
    ProvidedProperty("Inner", typeof<string>,
        getterCode = fun args ->
            // args.[0] is the obj receiver
            <@@ ((%%(args.[0]) : obj) :?> string) @@>)
```

If your erased base is a custom runtime helper:

```fsharp
let prop =
    ProvidedProperty("Name", typeof<string>,
        getterCode = fun args ->
            <@@ ((%%(args.[0]) : obj) :?> MyProvider.Helpers.Row).Name @@>)
```

The `MyProvider.Helpers.Row` type lives in the **Runtime** DLL — that's why the design-time DLL needs to compile-include the runtime source (or reference the runtime DLL): the quotation must be valid F# at the call site, where only the runtime DLL exists.

The `assemblyReplacementMap` rewrites references to the design-time assembly's copy of `Row` into the runtime assembly's copy. Without this, the spliced expression would point at a type the consumer cannot see.

---

## Writing values into the erased base

The constructor's job in an erased provider is to produce the runtime value. The return type of the quotation **must match** the erased base type:

```fsharp
let ctor =
    ProvidedConstructor(
        [ ProvidedParameter("name", typeof<string>) ],
        invokeCode = fun args ->
            // args.[0] is "name" parameter (erased ctor — no this)
            <@@ MyProvider.Helpers.Row(%%(args.[0]) : string) :> obj @@>)
```

The `:> obj` upcast is required when the erased base type is `obj`.

---

## Splicing methods that take generic arguments

This is where the most common subtle bug lives. **Never** call `Type.MakeGenericType` or `MethodInfo.MakeGenericMethod` inside a quotation. Use:

```fsharp
ProvidedTypeBuilder.MakeGenericType(typeDef, [|typeArg1; typeArg2|])
ProvidedTypeBuilder.MakeGenericMethod(methodInfoDef, [|typeArg|])
```

Why: when the compiler later loads the generated assembly metadata, plain reflection's generic-instantiated types don't have valid metadata tokens — they were created in the design-time process, not the compilation. The SDK's `ProvidedTypeBuilder` produces metadata-correct surrogates that round-trip through assembly serialization.

Symptom of the bug: at consumer compile time, an `InvalidOperationException` mentioning "metadata token" or a `Type mismatch` error pointing at a generic instantiation that "should" be valid.

---

## Building expressions other than literals

Most TP code uses `<@@ ... @@>` literals. When you need to construct an expression programmatically (e.g. you don't know at quotation-write time which field to access), use the `Expr` factory methods:

```fsharp
open FSharp.Quotations

// Call: build Expr.Call(receiver, methodInfo, args)
Expr.Call(receiverExpr, mi, [argExpr])

// Property get
Expr.PropertyGet(receiverExpr, propInfo)

// Field get
Expr.FieldGet(receiverExpr, fieldInfo)

// Cast
Expr.Coerce(expr, targetType)
```

For generic method calls, use `Expr.CallUnchecked` from `UncheckedQuotations`:

```fsharp
open ProviderImplementation.ProvidedTypes.UncheckedQuotations
let mi  = ProvidedTypeBuilder.MakeGenericMethod(genericMethodInfo, [|typeof<string>|])
let exp = Expr.CallUnchecked(receiver, mi, [arg])
```

`CallUnchecked` skips the F# compiler's regular type-checking on the constructed expression — required because the constructed `MethodInfo` is a `ProvidedTypeBuilder` surrogate that the standard `Expr.Call` would reject.

---

## Quoting void / unit

`<@@ () @@>` is the unit quotation — used as the body of constructors and void-returning methods that have no useful behaviour.

For methods that need to ignore a captured argument (e.g. an event adder/remover stub):

```fsharp
adderCode = fun args -> <@@ ignore (%%(args.[1]) : System.EventHandler) @@>
```

The `ignore` ensures the splice is well-typed even though we don't use the value.

---

## Quoting access to runtime helpers safely

If your provider needs to reference a runtime helper class, **the type must be reachable from both the design-time and runtime assemblies, and `assemblyReplacementMap` must rewrite the design-time reference to the runtime one**. The standard idiom: include the runtime source file in the design-time project as a `<Compile Include="..\Runtime\Helpers.fs" />` so both assemblies have an identical copy, then declare:

```fsharp
inherit TypeProviderForNamespaces(
    config,
    assemblyReplacementMap = [("MyProvider.DesignTime", "MyProvider.Runtime")])
```

Without the replacement map, the quotation will reference `MyProvider.DesignTime.Helpers.Row` at the consumer's call site — a type the consumer's project doesn't see, producing a "type not found" error at consumer compile time.

---

## Debugging splice errors

When a quotation fails at consumer compile time, the error usually mentions:

- a parameter name or index — recheck `args.[i]` semantics
- a type that "could not be found" — likely missing `assemblyReplacementMap` entry or unbundled design-time dep
- "metadata token" / "InvalidOperationException" — switch from `Type.MakeGenericType` to `ProvidedTypeBuilder.MakeGenericType`

The fastest reproducer is a tiny consumer `.fsx` script that uses the provider; running `dotnet fsi consumer.fsx` gives faster feedback than rebuilding a full project.
