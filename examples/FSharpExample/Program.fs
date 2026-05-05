open System
open System.IO
open System.Threading
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Scanner

let skillsDir =
    Path.Combine(AppContext.BaseDirectory, "Skills")
    |> Path.GetFullPath

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

let rec formatValue (value: YamlValue) =
    match value with
    | YString s -> s
    | YBool b   -> string b
    | YInt i    -> string i
    | YFloat f  -> sprintf "%g" f
    | YList items -> items |> List.map formatValue |> String.concat ", " |> sprintf "[%s]"
    | YMap entries ->
        entries
        |> Map.toSeq
        |> Seq.map (fun (YamlKey k, v) -> sprintf "%s: %s" k (formatValue v))
        |> String.concat ", "
        |> sprintf "{%s}"

// ─────────────────────────────────────────────────────────────────────────────
// 1. Read a single file
// ─────────────────────────────────────────────────────────────────────────────

printfn "=== YamlFrontMatter — F# Example ==="
printfn ""
printfn "── 1. Read a single file ──"
printfn ""

let complexPath = Path.Combine(skillsDir, "complex", "SKILL.md") |> AbsoluteFilePath.createUnsafe

match tryReadOne complexPath with
| Ok (Some skill) ->
    printfn "  Path: %s" (AbsoluteFilePath.value skill.Path)
    printfn "  Fields:"
    for (YamlKey k, v) in Map.toSeq skill.Fields do
        printfn "    %s: %s" k (formatValue v)
| Ok None ->
    printfn "  (no front matter found)"
| Error err ->
    match err with
    | MissingClosingDelimiter p -> printfn "  ERROR: missing delimiter in %s" p.Value
    | YamlParseFailed (p, ex)  -> printfn "  ERROR: parse failed in %s: %s" p.Value ex.Message
    | FileReadFailed (p, ex)   -> printfn "  ERROR: cannot read %s: %s" p.Value ex.Message

printfn ""

// ─────────────────────────────────────────────────────────────────────────────
// 2. Schema inference
// ─────────────────────────────────────────────────────────────────────────────

printfn "── 2. Schema Inference ──"
printfn ""

let report = discoverSchemaWithStats skillsDir "SKILL.md"

printfn "  Files scanned: %d" report.FilesScanned
printfn "  Inferred F# record type:"
printfn ""
for line in (formatSchema report).Split('\n') do
    printfn "    %s" line
printfn ""

// ─────────────────────────────────────────────────────────────────────────────
// 3. Channel-based streaming scanner
// ─────────────────────────────────────────────────────────────────────────────

printfn "── 3. Channel-based streaming scanner ──"
printfn ""

let opts =
    { RootDirectory       = AbsoluteFilePath.createUnsafe skillsDir
      Pattern             = "SKILL.md"
      Parallelism         = 4
      PathQueueCapacity   = 64
      ResultQueueCapacity = 64 }

let reader = scan opts CancellationToken.None

let mutable keepGoing = true
while keepGoing do
    let canRead = reader.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult()
    if not canRead then
        keepGoing <- false
    else
        let mutable item = Unchecked.defaultof<_>
        while reader.TryRead(&item) do
            match item with
            | Ok (Some skill) ->
                let name =
                    skill.Fields
                    |> Map.tryFind (YamlKey "name")
                    |> Option.map formatValue
                    |> Option.defaultValue "(unnamed)"
                printfn "  [OK] %s — %s" name (AbsoluteFilePath.value skill.Path)
            | Ok None -> ()
            | Error err ->
                printfn "  [ERR] %A" err

printfn ""

// ─────────────────────────────────────────────────────────────────────────────
// 4. Pattern matching on YamlValue — the idiomatic F# way
// ─────────────────────────────────────────────────────────────────────────────

printfn "── 4. Pattern matching on YamlValue ──"
printfn ""

match tryReadOne complexPath with
| Ok (Some skill) ->
    let fields = skill.Fields
    let tryGet key = Map.tryFind (YamlKey key) fields

    match tryGet "active" with
    | Some (YBool b) -> printfn "  active = %b" b
    | _ -> ()

    match tryGet "priority" with
    | Some (YInt i) -> printfn "  priority = %d" i
    | _ -> ()

    match tryGet "tags" with
    | Some (YList items) ->
        let tags = items |> List.choose (function YString s -> Some s | _ -> None)
        printfn "  tags = [%s]" (String.concat ", " tags)
    | _ -> ()

    match tryGet "metadata" with
    | Some (YMap entries) ->
        printfn "  metadata:"
        for (YamlKey k, v) in Map.toSeq entries do
            printfn "    %s = %s" k (formatValue v)
    | _ -> ()
| _ -> ()

printfn ""
printfn "── Done! ──"
