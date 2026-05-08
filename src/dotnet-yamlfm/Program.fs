module YamlFrontMatter.Cli.Program

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Schemas
open YamlFrontMatter.Scanner

let private printSkip (path: AbsoluteFilePath) (reason: SkipReason) =
    let p = AbsoluteFilePath.value path
    match reason with
    | SkipReason.NoFrontMatter ->
        eprintfn "SKIP (no front matter): %s" p
    | SkipReason.UnclosedFrontMatter ->
        eprintfn "SKIP (unclosed): %s" p
    | SkipReason.YamlMalformed detail ->
        eprintfn "SKIP (yaml malformed): %s" p
        eprintfn "  %s" detail
    | SkipReason.IoFailure msg ->
        eprintfn "SKIP (IO error): %s" p
        eprintfn "  %s" msg

let private printRejection (path: AbsoluteFilePath) (failures: ValidationFailure list) =
    eprintfn "REJECTED: %s" (AbsoluteFilePath.value path)
    for f in failures do
        match f with
        | MissingField (YamlKey k) ->
            eprintfn "  • missing required field: %s" k
        | EmptyString (YamlKey k) ->
            eprintfn "  • empty required string: %s" k
        | WrongType (YamlKey k, expected, _) ->
            eprintfn "  • field '%s' has wrong type (expected %A)" k expected

let rec private printValue (indent: int) (value: YamlValue) =
    let pad = String(' ', indent)
    match value with
    | YString s -> printfn "%s%s" pad s
    | YBool b   -> printfn "%s%b" pad b
    | YInt i    -> printfn "%s%d" pad i
    | YFloat f  -> printfn "%s%f" pad f
    | YList items ->
        for v in items do
            printf "%s- " pad
            printValue 0 v
    | YMap entries ->
        for kv in entries do
            let (YamlKey k) = kv.Key
            printfn "%s%s:" pad k
            printValue (indent + 2) kv.Value

let private parseMode (s: string) : FrontMatterSchema =
    match s.Trim().ToLowerInvariant() with
    | "skill"   -> Skill
    | "general" -> General
    | other     ->
        eprintfn "Unknown --mode '%s' (expected 'skill' or 'general'); using 'general'" other
        General

/// Default mode: stream item-by-item through the lazy scanner and dump every
/// YAML field. Validated entries print under a `===` heading; rejected ones
/// print with a precise list of validation failures; skipped ones print why
/// they were skipped (no front matter / yaml broken / IO error).
let private dumpMetadata (rootDir: string) (schema: FrontMatterSchema) (pattern: string) : Task<int> =
    task {
        use cts = new CancellationTokenSource()
        Console.CancelKeyPress.Add(fun args ->
            args.Cancel <- true
            cts.Cancel())

        let opts =
            { RootDirectory       = AbsoluteFilePath.createUnsafe rootDir
              Pattern             = pattern
              Parallelism         = 16
              PathQueueCapacity   = 512
              ResultQueueCapacity = 512 }

        let reader = scan schema opts cts.Token

        let mutable valid     = 0
        let mutable rejected  = 0
        let mutable skipped   = 0
        let mutable keepGoing = true

        while keepGoing do
            let! canRead = reader.WaitToReadAsync(cts.Token).AsTask()
            if not canRead then
                keepGoing <- false
            else
                let mutable item = Unchecked.defaultof<ScanItem>
                while reader.TryRead(&item) do
                    match item with
                    | ItemValid raw ->
                        valid <- valid + 1
                        printfn ""
                        printfn "=== %s ===" (AbsoluteFilePath.value raw.Path)
                        for kv in raw.Fields do
                            let (YamlKey k) = kv.Key
                            printfn "  %s:" k
                            printValue 4 kv.Value
                    | ItemRejected (path, failures) ->
                        rejected <- rejected + 1
                        printRejection path failures
                    | ItemSkipped (path, reason) ->
                        skipped <- skipped + 1
                        printSkip path reason

        printfn ""
        printfn "Done. Valid=%d Rejected=%d Skipped=%d" valid rejected skipped
        // Exit code: non-zero when *rejected* count is non-zero (rejected files
        // are documents that *tried* to be valid but failed validation, which
        // is a CI-meaningful issue). Skipped files are not errors.
        return if rejected = 0 then 0 else 2
    }

/// Schema-discovery mode: synchronously walk the directory, infer the union
/// schema across all files, and print it as F# record source.
let private dumpSchema (rootDir: string) (pattern: string) : int =
    let report = discoverSchemaWithStats rootDir pattern
    printfn "%s" (formatSchema report)
    0

let private printUsage () =
    eprintfn "Usage:"
    eprintfn "  yamlfm <directory> [--mode skill|general] [--pattern <glob>]"
    eprintfn "  yamlfm <directory> --schema [--pattern <glob>]"
    eprintfn ""
    eprintfn "Defaults: --mode general   --pattern SKILL.md"
    1

[<EntryPoint>]
let main argv =
    // Tiny argv parser. Order-independent flags; positional arg = directory.
    let mutable rootDir = ""
    let mutable mode    = General
    let mutable pattern = "SKILL.md"
    let mutable schemaMode = false
    let mutable bad        = false

    let mutable i = 0
    while i < argv.Length && not bad do
        match argv.[i] with
        | "--schema" ->
            schemaMode <- true
            i <- i + 1
        | "--mode" when i + 1 < argv.Length ->
            mode <- parseMode argv.[i + 1]
            i <- i + 2
        | "--pattern" when i + 1 < argv.Length ->
            pattern <- argv.[i + 1]
            i <- i + 2
        | s when not (s.StartsWith "--") && rootDir = "" ->
            rootDir <- s
            i <- i + 1
        | _ ->
            bad <- true

    if bad || rootDir = "" then
        printUsage ()
    elif not (Directory.Exists rootDir) then
        eprintfn "Directory does not exist: %s" rootDir
        1
    elif schemaMode then
        dumpSchema rootDir pattern
    else
        (dumpMetadata rootDir mode pattern).GetAwaiter().GetResult()
