# Static Parameters

Static parameters let consumers specialize a provided type at compile time, e.g. `JsonProvider<"sample.json">` or `Schema<Count = 5, Prefix = "Col">`.

## Single required parameter

```fsharp
let entry = ProvidedTypeDefinition(asm, ns, "Schema", Some typeof<obj>)
entry.DefineStaticParameters(
    [ ProvidedStaticParameter("Count", typeof<int>) ],
    fun typeName args ->
        let count = args.[0] :?> int
        createType typeName count)
this.AddNamespace(ns, [entry])
```

The function `(string -> obj[] -> ProvidedTypeDefinition)` receives:
- `typeName` — the *mangled* name the compiler synthesises for this specialization (e.g. `"Schema,5"`); use it as the name of the type you create.
- `args` — boxed values, one per declared parameter, in declaration order. Cast each via `:?>`.

Return: the specialized `ProvidedTypeDefinition`. **Don't** add it to a namespace — the compiler does that automatically.

## Multiple parameters

```fsharp
entry.DefineStaticParameters(
    [ ProvidedStaticParameter("Count",  typeof<int>)
      ProvidedStaticParameter("Prefix", typeof<string>) ],
    fun typeName args ->
        let count  = args.[0] :?> int
        let prefix = args.[1] :?> string
        createType typeName count prefix)
```

## Optional parameters with defaults

```fsharp
entry.DefineStaticParameters(
    [ ProvidedStaticParameter("Count",  typeof<int>)
      ProvidedStaticParameter("Prefix", typeof<string>, parameterDefaultValue = "Col") ],
    callback)
```

If **all** parameters have defaults, the SDK warns at build: the unapplied container type and the all-defaults specialization are observationally indistinguishable, which can confuse IntelliSense. The warning is intentional — either accept it or make at least one parameter required.

## Supported parameter types

`typeof<int>`, `typeof<string>`, `typeof<bool>`, `typeof<float>`, `typeof<float32>`, and `typeof<System.Type>`. Other types are rejected by the compiler at the consumer's call site.

For complex inputs (config objects, large records), use a string parameter and parse it inside the callback — the same trick `JsonProvider` uses with file paths and inline JSON literals.

## Parameter values that read from disk

```fsharp
entry.DefineStaticParameters(
    [ ProvidedStaticParameter("Sample", typeof<string>) ],
    fun typeName args ->
        let sample = args.[0] :?> string
        let json =
            if System.IO.File.Exists sample then System.IO.File.ReadAllText sample
            else sample   // treat as inline literal
        createTypeFromJson typeName json)
```

Two important details when reading files at design time:
1. Resolve relative paths against `config.ResolutionFolder` (available on the `TypeProviderConfig` passed to your provider's primary constructor) — the consumer's project root, not the design-time DLL location.
2. Handle the file being missing or malformed gracefully: throw an exception with a clear message; the F# compiler displays it as the static parameter error.

## Caching

Static parameter callbacks may be invoked multiple times during an IDE session as the user re-edits. If schema discovery is expensive, cache by `(typeName, args)` in a mutable dictionary owned by the provider instance. But: the provider instance itself is short-lived (re-created per type-check), so the cache is most effective for repeated callbacks within one type-check, not across edits.

For caching across edits, use `config.SystemRuntimeAssemblyVersion` and the file modification time as a key, and store in a static field. Be careful — you are now sharing state across compilations and must keep it correct under concurrent design-time invocations.
