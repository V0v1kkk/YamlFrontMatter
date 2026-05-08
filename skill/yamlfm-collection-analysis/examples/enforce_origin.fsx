// enforce_origin.fsx — declare "every file must have a non-empty `origin`"
// up-front, let the library enforce it, and let `GetRejected()` directly
// surface the broken files.
//
// Compare with `find_outliers.fsx`, which does the equivalent post-hoc by
// walking `GetAll()` and counting presence ratios. This script is the
// schema-driven version of the same idea — fewer lines, declarative intent,
// and the rejection list is the diagnostic.
//
// The TP's `Mode` static parameter only knows "skill" / "general"; for custom
// requirements like this one, drop into the core API (`Scanner.scanAll`) with
// a pipe-composed schema.

#r "nuget: YamlFrontMatter"

open System.Threading
open YamlFrontMatter.Types
open YamlFrontMatter.Schemas
open YamlFrontMatter.Scanner

[<Literal>]
let Root = "/absolute/path/to/your/collection"

// "Skill semantics + every file must declare an origin". Reads top-down as
// the requirements list grows.
let mySchema =
    Skill
    |> requireString "origin"

let opts =
    { RootDirectory       = AbsoluteFilePath.createUnsafe Root
      Pattern             = "SKILL.md"
      Parallelism         = 8
      PathQueueCapacity   = 256
      ResultQueueCapacity = 256 }

// `scanAll` returns a lazy seq over the channel-based parallel scanner —
// no manual WaitToReadAsync / TryRead loop needed.
let mutable valid    = 0
let mutable rejected = 0
let mutable skipped  = 0

scanAll mySchema opts CancellationToken.None
|> Seq.iter (function
    | ItemValid _ ->
        valid <- valid + 1

    | ItemRejected (path, failures) ->
        rejected <- rejected + 1
        printfn "REJECTED: %s" (AbsoluteFilePath.value path)
        for f in failures do
            match f with
            | MissingField (YamlKey k)        -> printfn "    • missing required field: %s" k
            | EmptyString  (YamlKey k)        -> printfn "    • empty required string: %s" k
            | WrongType    (YamlKey k, exp, _) -> printfn "    • '%s' wrong type (expected %A)" k exp

    | ItemSkipped (path, reason) ->
        skipped <- skipped + 1
        printfn "SKIPPED:  %s — %A" (AbsoluteFilePath.value path) reason)

printfn "\nTotals: valid=%d rejected=%d skipped=%d" valid rejected skipped
