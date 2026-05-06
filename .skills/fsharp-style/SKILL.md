---
name: fsharp-style
description: >
  Personal F# coding style guide for Vladimir's AI assistant. Use this skill
  whenever writing or reviewing F# code — for new files, refactors, or when
  the user asks "how should I write this in F#". Covers functional thinking,
  single-case DU discipline, active patterns, computation expressions / custom
  DSLs, task-based async, file/module organisation, and the "make illegal
  states unrepresentable" implementation patterns (the *theory* lives in the
  domain-modeling-functional-ddd skill — this one captures the concrete code
  shapes Vladimir prefers). Trigger this skill on any .fs/.fsi/.fsx authoring
  task, when reviewing pull requests in F# repos, or when an agent is choosing
  between OOP-style and FP-style implementations of a feature in F#.
metadata:
  author: Vladimir Rogozhin (personal)
  version: '1.0'
  sources: >
    https://fsprojects.github.io/fsharp-cheatsheet/
    https://learn.microsoft.com/en-us/dotnet/fsharp/
    Scott Wlaschin — Domain Modeling Made Functional
dependsOnSkills: [ "fsharp-type-provider" ]
requiresDependencies:
  - "F# 8 or later (the dotnet 8+ SDK ships it)"
  - ".NET 8 or later runtime"
  - "FsToolkit.ErrorHandling (optional but recommended for `result { }` / `taskResult { }` CEs)"
  - "FSharp.UMX (optional; alternative to single-case DU for unit-tagged primitives)"
requiresEnvironmentVariables: []
---

# F# Style — Vladimir's Edition

## When to use

Load whenever the agent is:
- Writing new F# code (`.fs`, `.fsi`, `.fsx`).
- Reviewing or refactoring existing F# code.
- Choosing between OOP-style and FP-style for a feature in an F# codebase.
- Designing the public API of a module or library.
- Deciding whether some logic deserves an active pattern, a computation expression, or a Type Provider.

## Foundation: dependent skills

This skill **complements**, does not replace, two others:

- `domain-modeling-functional-ddd` *(personal Vladimir skill — not bundled in this repo)* — the *theory*: how to discover bounded contexts, model workflows as typed pipelines, decide when a value object becomes an aggregate. Load when the question is **what to model**. The book this skill is built on is [Scott Wlaschin — *Domain Modeling Made Functional*](https://pragprog.com/titles/swdddf/domain-modeling-made-functional/) and is a fine substitute if the skill itself is unavailable.
- [`fsharp-type-provider`](../fsharp-type-provider/SKILL.md) *(sibling skill in this same `.skills/` folder)* — load whenever authoring or debugging an F# Type Provider. Specifically: project layout (`*.Runtime` / `*.DesignTime`), erased vs generative, packaging, debugging. This skill stays out of TP-internals — defer to the TP skill there.

This skill is the **how to write the code** layer that sits between them.

---

## Part 1 — Functional thinking (the mindset)

The biggest gap I see in AI-written F# code: the author writes **C# with pipes** instead of thinking in F#. Symptoms: classes with mutable state holding everything, methods that return `Task` and mutate fields, exceptions for ordinary failure paths, nested `if`s instead of pattern matches, `for` loops where `List.map` would do.

The reframing that fixes this:

### A program is a pipeline of total functions over immutable data

Not "objects sending messages". Not "a graph of services calling each other". A pipeline:

```
input → validate → transform → emit
        (Result)   (Result)   (Result)
```

Each arrow is a function. Each input/output is an immutable value. Failure is **a value** (`Error`), not a thrown exception. The whole thing is composed via `>>`, `|>`, `Result.bind`, or `result { }` CE.

### Make illegal states unrepresentable, not just unlikely

If a `User` requires `Email` to be valid, `Email` should be a single-case DU with a `private` constructor. Then no codepath in the program can construct an invalid `User` — the type system says so. See [Part 3](#part-3--single-case-du-discipline) for the exact shape.

### The "verbs are functions, nouns are types" lens

When stuck on whether to model something as a method or a function: it's **almost always a function**. F# has no real reason to put `Process` as a method on `Order` — `processOrder : Order -> ...` reads better, composes better, and stays out of inheritance hierarchy debates.

**Members make sense for:**
- C# interop (consumers expect `.Property` access).
- Extension over types you don't own (extension methods).
- Cases where you want fluent chains (`x.WithFoo().WithBar()`) — but in F# this is usually a type-class/operator pattern instead.

**Member-on-DU exception** — for single-case DUs, having `member this.Value` *next to* the module's `value` function is fine and pragmatic. C# consumers (and IDE autocompletion) like `.Value`; F# call sites prefer `MyType.value`. Provide both — see [Part 3](#part-3--single-case-du-discipline).

### Total over partial

A `total` function returns a value for every input. A `partial` function throws / hangs / fails for some inputs. Idiomatic F# bias: **total**.

If the answer might not exist, return `Option<T>`. If it might fail, return `Result<T, Error>`. If it definitely returns something but the something is "we made progress", return a record with an explicit "what changed" field. Don't reach for exceptions to express "this didn't work for you".

Reserve exceptions for **truly exceptional** conditions: programmer errors (`invalidArg`), unrecoverable I/O failures crossing a process boundary, places where the type system literally can't express a constraint. The boundary between "expected failure" and "exception" is the boundary between "value" and "panic".

---

## Part 2 — File and module organisation

### Project file ordering (matters in F#)

F# compiles top-to-bottom. A type or function can only reference things declared **above** it in the same project. This forces a dependency-shaped order:

```
Types.fs              ← single-case DUs, records, ADTs (no logic)
DomainEvents.fs       ← if events are a thing
SchemaInference.fs    ← pure functions over Types
SkillScanner.fs       ← effectful code (file IO, channels)
Program.fs            ← composition root + CLI
```

Rules:
1. **`Types.fs` first** — pure data only, no I/O, no dependencies on anything else in the project.
2. **Pure before impure** — schema inference, validators, pure transforms come before scanners, parsers, network calls.
3. **Composition root last** — `Program.fs` (or `Library.fs` for a library's "wire-everything-up" entry).

This isn't dogma — it's the only thing the compiler accepts.

### Module vs namespace

| Use a **module** when | Use a **namespace** when |
|---|---|
| You want grouped functions, types, and values together | You want only types, no top-level `let` values |
| You want `open Foo` to bring functions and constants into scope | You're shipping a library and want a clean external surface |
| Single-file boundaries inside a project | Cross-file grouping inside a library |
| Most of the time | Rarely, in libraries |

For internal projects: **modules everywhere**. Namespaces add ceremony without buying anything for application code.

`[<RequireQualifiedAccess>]` on a module **forces** call sites to write `MyModule.foo` instead of `foo`. Use this when:
- The function names are generic (`create`, `value`, `tryParse`) and would clash if `open`-ed.
- You want call sites to read clearly even without IDE help.

`[<AutoOpen>]` on a module makes its contents visible automatically when its parent namespace is opened. Use **sparingly** — only for "DSL prelude" modules that the consumer is supposed to take wholesale.

---

## Part 3 — Single-case DU discipline

This is the single most important pattern. **Wrap every primitive that has domain meaning in a single-case DU.** Not just IDs — every email, every URL, every "minutes" vs "seconds" int, every "quantity" vs "price" decimal.

### The canonical shape (Vladimir's preference)

```fsharp
[<Struct>]
type SkillName = private SkillName of string with
    member this.Value = let (SkillName v) = this in v

module SkillName =
    let value (SkillName v) = v

    let create (s: string) =
        if String.IsNullOrWhiteSpace s then Error "SkillName must not be empty"
        else Ok (SkillName (s.Trim()))

    let createUnsafe (s: string) = SkillName s
```

Five elements, every time:

1. **`[<Struct>]`** — the wrapper has zero allocation; an unboxed `string` underneath. No heap pressure for ubiquitous types like IDs.
2. **`private SkillName of string`** — the constructor is private to the file. External code can only get a `SkillName` via `create` or `createUnsafe` from the module. This is what makes invalid states unrepresentable.
3. **`with member this.Value = ...`** — instance accessor. Lets `name.Value` work in C# interop and reads well in IDE intelligence (`.` then tab).
4. **`module SkillName`** — same name as the type. Houses `value`, `create`, `createUnsafe`, plus any domain operations.
5. **`value` / `create` / `createUnsafe` triplet** —
   - `value` — projection back to the underlying primitive. Pattern-match destructure inline.
   - `create` — validating constructor. Returns `Result<T, string>` (or a richer `Error` type if the project has one). **All external users go through this.**
   - `createUnsafe` — fast path for trusted internal callers (deserialisation, tests, hot loops). Bypasses validation. Name says "unsafe" loud and clear.

### Variant: no validation needed

If the wrapper exists purely for **type identity** (no validation possible), drop `private` and `create`/`createUnsafe`:

```fsharp
[<Struct>]
type YamlKey = YamlKey of string with
    member this.Value = let (YamlKey v) = this in v

module YamlKey =
    let value (YamlKey v) = v
    let create (s: string) = YamlKey s
```

Reasoning: every string is a valid YAML key; nothing to validate. Public constructor is fine. But the type identity still stops you from passing a `SkillName` where a `YamlKey` is expected.

### Why not just `string`?

```fsharp
let promote (skillName: string) (description: string) (origin: string) = ...
```

The compiler will gladly accept `promote origin skillName description` — a bug that only surfaces in production with weird strings in weird places. With single-case DUs:

```fsharp
let promote (name: SkillName) (desc: SkillDescription) (origin: Origin) = ...
```

…the wrong order doesn't compile. **A type error at 10am beats a production incident at 3pm.**

### Alternative: FSharp.UMX

`FSharp.UMX` is a lighter-weight alternative for cases where you want type safety with **zero overhead** and don't need `member`s or modules:

```fsharp
open FSharp.UMX

[<Measure>] type skillNameTag
type SkillName = string<skillNameTag>

let make (s: string) : SkillName = UMX.tag s
let unwrap (s: SkillName) : string = UMX.untag s
```

Trade-off: less ceremony, but no smart-constructor / private-constructor enforcement. Use UMX for high-frequency identifier types where validation isn't needed; use single-case DU for everything that has domain rules attached.

For a deeper comparison and bounded-context modelling rationale, see the `domain-modeling-functional-ddd` skill (or [Scott Wlaschin's book](https://pragprog.com/titles/swdddf/domain-modeling-made-functional/)).

---

## Part 4 — Pattern matching: prefer over `if`/`else`

If a function branches on **shape** of data (DU case, record presence, list head/tail), use `match`. If it branches on **boolean** condition, `if`/`else` is fine.

```fsharp
// shape branch — match
let describe = function
    | TString       -> "string"
    | TBool         -> "bool"
    | TInt          -> "int"
    | TList inner   -> sprintf "%s list" (describe inner)
    | TMapping _    -> "record"

// boolean condition — if/else is fine
let isLargeFile path =
    if FileInfo(path).Length > 10_000_000L then "large" else "small"
```

The `function` shorthand (no explicit parameter, no `match x with`) is idiomatic when the **only** thing the function does is dispatch on the input — saves a line.

Prefer **exhaustive** matches. F# warns when a match is non-exhaustive; treat that warning as an error. If you genuinely don't care about other cases, document it explicitly:

```fsharp
| _ -> failwithf "unexpected: %A" other     // dev assertion
| _ -> ()                                   // intentional ignore
```

The first form crashes at runtime if the assumption breaks (good for invariants); the second silently ignores (only OK when truly safe).

---

## Part 5 — Active patterns

Active patterns are **F#-specific** and one of the most underused features. They let you treat plain values as if they had constructor cases — useful for parsing, validation, and library APIs that take plain types.

### Single-case (conversion)

When you want to view a value through a different lens before matching on it.

```fsharp
let (|Lower|) (s: string) = s.ToLowerInvariant()

let action input =
    match input with
    | Lower "yes" -> "confirmed"
    | Lower "no"  -> "denied"
    | _           -> "unknown"
```

Reads as if `Lower` were a wrapper, even though it's a free conversion. Excellent for **case-insensitive** parsing.

### Complete (multi-case)

When the input naturally falls into a fixed set of categories.

```fsharp
let (|Even|Odd|) i = if i % 2 = 0 then Even else Odd

let summarize n =
    match n with
    | Even -> "even"
    | Odd  -> "odd"
```

Use when:
- The classification is exhaustive (no leftover case).
- You want callers to pattern-match instead of calling a "kind" function.

### Partial (`Option`-returning)

For "does this match, and if so, here's the data". The trailing `|_|` is required.

```fsharp
let (|Int|_|) (s: string) =
    match Int32.TryParse s with
    | true, n -> Some n
    | _       -> None

let (|Url|_|) (s: string) =
    match Uri.TryCreate(s, UriKind.Absolute) with
    | true, uri -> Some uri
    | _         -> None

let route input =
    match input with
    | Int n   -> sprintf "got integer %d" n
    | Url uri -> sprintf "got URL %O"     uri
    | _       -> "got plain string"
```

This is **the pattern** for replacing `if (TryParse...) then ... elif ... else ...` chains. It reads top-down, declaratively.

### Parameterised partial

Active patterns can take parameters:

```fsharp
let (|DivisibleBy|_|) divisor n =
    if n % divisor = 0 then Some () else None

let classify n =
    match n with
    | DivisibleBy 15 -> "fizzbuzz"
    | DivisibleBy 3  -> "fizz"
    | DivisibleBy 5  -> "buzz"
    | _              -> string n
```

Use when the same predicate shape needs different parameter values at different call sites.

### When NOT to use active patterns

- The branching is purely on a known DU you defined — just match on the DU.
- The conversion is one-shot and only used in one place — inline it.
- The pattern would shadow a common name (`Int`, `Url` are fine; `Result`, `Some`, `None` would be confusing).

---

## Part 6 — Computation expressions and custom DSLs

CEs are F#'s answer to "I want a domain-specific language without parsing anything". Standard ones:

| CE | Returns | Use for |
|---|---|---|
| `seq { }` | `seq<'T>` | Lazy enumerations |
| `task { }` | `Task<'T>` | Async work, default for new code |
| `async { }` | `Async<'T>` | F#-native async; prefer `task` for new code unless you need cancellation propagation |
| `option { }` (FsToolkit) | `Option<'T>` | Short-circuit on `None` |
| `result { }` (FsToolkit) | `Result<'T, 'E>` | Validation pipelines |
| `taskResult { }` (FsToolkit) | `Task<Result<'T, 'E>>` | Async + Result composition |

### `result { }` is the validation idiom

```fsharp
let createSkill rawName rawDesc rawOrigin = result {
    let! name   = SkillName.create rawName
    let! desc   = SkillDescription.create rawDesc
    let! origin = Origin.create rawOrigin
    return { Name = name; Description = desc; Origin = origin }
}
```

Each `let!` either binds the `Ok` value or short-circuits with `Error`. Equivalent without CE would be a chain of `Result.bind`s — same semantics, much louder syntax.

### Define your own CE for repeated patterns

If a project pattern repeats — "always log, then do, then log result" or "always try-with on this exception type, then default" — wrap it in a CE.

Skeleton:

```fsharp
type LoggedBuilder(prefix: string) =
    member _.Bind(x: 'a, f: 'a -> 'b) =
        printfn "[%s] step input: %A" prefix x
        let result = f x
        printfn "[%s] step output: %A" prefix result
        result
    member _.Return x = x

let logged = LoggedBuilder("audit")

let work = logged {
    let! name = SkillName.create "test" |> Result.toOption |> Option.get
    let! upper = name.Value.ToUpperInvariant() |> Some
    return upper
}
```

This is a basic shape; real CEs implement more members (`Zero`, `Combine`, `Delay`, `Run`, `For`, `While`, `TryWith`, `TryFinally`, `Using`) depending on what the DSL needs to express. F# Foundation has a [Computation Expressions guide](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/computation-expressions) for the full member surface.

**When to build a custom CE:**
- The same control-flow scaffolding repeats in 5+ places.
- The repetition is structural, not parametric (a function with parameters won't fix it).
- The CE makes call sites shorter *and* clearer (both, not just one).

**When NOT:**
- You can wrap it in a function (`tryWithLog`, `withDefault`).
- It's used in one or two places.
- The CE machinery is more code than the duplication it removes.

---

## Part 7 — `task` over `async` (modern default)

For new F# code targeting .NET 6+:

| Use `task { }` for | Use `async { }` for |
|---|---|
| Most new code | Legacy F# codebases that already use `async` consistently |
| C# interop (returning `Task<T>` to a C# caller) | F#-only libraries with cancellation conventions |
| ASP.NET, EF Core, anything `Task`-based | Long-running cooperative pipelines where `Async.Parallel` / `Async.Sequential` ergonomics matter |

The tipping reason: every modern .NET API takes/returns `Task<T>`. `async { }` requires `|> Async.AwaitTask` boilerplate at every boundary. `task { }` is native interop.

```fsharp
let downloadAndParse (url: Uri) : Task<Result<Skill, string>> = task {
    use client = new HttpClient()
    let! body = client.GetStringAsync(url)
    return SkillFrontMatter.parse body
}
```

Cancellation is **explicit** in `task { }` (you pass a `CancellationToken`); in `async { }` it's implicit (propagated by `Async.RunSynchronously`). For new code, explicit is clearer.

---

## Part 8 — Records and updates

Records are immutable by default. The "update" is a copy with overrides:

```fsharp
type Skill = { Name: SkillName; Origin: Origin option; Tags: string list }

let withTag tag skill = { skill with Tags = tag :: skill.Tags }
```

`{ x with Field = newValue }` is the only sane way to "update" a record. Don't add `mutable` to record fields except in performance-critical hot paths with strong justification.

For deeply nested updates, write a helper function or a small lens library — never resort to manual nested copies in business code.

---

## Part 9 — Errors: `Result`, `Option`, exceptions

Quick decision tree:

| Situation | Use |
|---|---|
| The function might not produce a value, no error info needed | `Option<T>` |
| The function might fail, error info matters to caller | `Result<T, Error>` |
| The function fails on programmer error or invariant break | exception (panic) |
| The function fails at I/O boundary, caller can recover | `Result` (catch at the boundary, wrap into `Error`) |
| The function fails at I/O boundary, caller can't recover | let exception propagate |

`Error` should be a project-local DU, not raw `string`:

```fsharp
type AppError =
    | ValidationFailed of field: string * reason: string
    | NotFound         of entityType: string * id: string
    | TransientIO      of operation: string * inner: exn

let lookup id : Result<Skill, AppError> = ...
```

This lets calling code *match* on the error and decide what to do. Strings as error values throw away that information.

`FsToolkit.ErrorHandling` provides `result { }` and `Result` combinators (`Result.map`, `Result.bind`, `Result.mapError`, etc). Strongly recommended dependency.

---

## Part 10 — Naming conventions

| Element | Convention | Example |
|---|---|---|
| Types (records, DUs, classes) | `PascalCase` | `SkillName`, `InferredType` |
| Modules | `PascalCase` | `SchemaInference` |
| Module functions | `camelCase` | `inferNodeType`, `mergeTypes` |
| Module values (constants) | `camelCase` | `defaultParallelism` |
| DU cases | `PascalCase` | `TString`, `TList`, `MissingClosingDelimiter` |
| Type parameters | `'lowercase` | `'a`, `'state`, `'event` |
| Type abbreviations | `PascalCase` | `type Schema = Map<YamlKey, FieldSchema>` |
| Test names (xUnit) | backtick natural language | `` `GetAll returns exactly 3 skills` `` |

DU case prefix conventions vary. The codebase here uses `T` prefix for type-domain cases (`TString`, `TBool`) — that disambiguates from the F# / .NET `String`, `Bool`. Adopt this when the domain naturally has a "type" axis.

---

## Part 11 — Anti-patterns to avoid

These come up repeatedly in AI-written F# code:

| Anti-pattern | Replacement |
|---|---|
| `try ... with _ -> defaultValue` for control flow | `Option.defaultValue` / `Result` |
| `mutable` field in a class because "it's easier" | Pure function returning new value |
| `obj` as a parameter type | A DU or a type parameter |
| `List.iter (fun x -> mutableState <- x)` | `List.fold` with accumulator |
| Long `if`/`elif` chains | `match` or active patterns |
| `failwith "TODO"` left in committed code | Either implement or `notImplemented` typed marker |
| `Async.RunSynchronously` inside a function (not just at entry) | Make the function `Async<>` / `Task<>` |
| Custom `Compare`/`Equals` on a record | Almost always means the record should be a class with explicit identity |
| `List.head xs` without checking `List.isEmpty` | `List.tryHead`, then match |

### Specifically: don't reach for OO inheritance

F# has classes and inheritance, but in domain code the answer is **almost never** "create an abstract class with virtual methods". It's "make the type a DU and pattern-match on it". Inheritance is for C# interop and framework hooks (e.g., implementing `TextReader` for a specific consumer); not for domain modelling.

---

## Part 12 — Type Provider authoring

When the user wants to author a Type Provider, **delegate to** [`fsharp-type-provider`](../fsharp-type-provider/SKILL.md). That skill covers:

- Two-component architecture (Runtime / DesignTime)
- Erased vs generative
- Static parameters, members, quotations, `args.[i]` semantics
- Packaging into NuGet (`IsFSharpDesignTimeProvider`)
- Debugging inside `fsc.exe` / Rider / VS Code
- The 6-most-common pitfalls (missing `provAsm.AddTypes`, etc.)

Don't try to fold TP authoring into this skill — it's deep enough to deserve its own.

The only TP-related thing that lives **here**: the **call-site** style. Code that *consumes* a TP should still follow this skill's discipline — wrap provided strings in domain DUs, use `Result` for validation, etc.

---

## Cheat-sheet pointer

For one-page syntax reference (string interpolation, list comprehensions, computation-expression literals, etc.), the [F# cheatsheet](https://fsprojects.github.io/fsharp-cheatsheet/) is the most condensed source. Use it as a refresher, not as a style guide — style decisions live in this skill.

---

## Response style for the agent

When applying this skill:

1. **Show types first, then implementation.** If the user asks "how do I X", lead with the signature. The signature usually answers half the question.
2. **Quote specific anti-patterns from the user's code** when refactoring. Don't be vague — point to the line and propose the rewrite.
3. **Use Russian or English to match the user's language**, but keep code identifiers in English.
4. **For non-trivial choices, state the trade-off**: "I'm picking `Result<_, AppError>` over `Option<_>` here because callers will want to know *why* validation failed." No silent decisions.
5. **Prefer one well-shaped example over three half-baked ones.** A complete `Types.fs` with the canonical single-case-DU pattern is worth more than scattered fragments.
6. **Don't reach for libraries unprompted** beyond the standard set (FsToolkit.ErrorHandling, FSharp.UMX). Adding `Fable`, `Saturn`, `Suave`, `Bolero`, etc. is a project-shape decision the user makes — not something to assume.
