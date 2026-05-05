module YamlFrontMatter.Cli.Program

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Scanner

let private printError (err: ScanError) =
    match err with
    | MissingClosingDelimiter path ->
        eprintfn "BROKEN FRONT MATTER: %s" (AbsoluteFilePath.value path)
    | YamlParseFailed (path, ex) ->
        eprintfn "YAML ERROR: %s" (AbsoluteFilePath.value path)
        eprintfn "  %s" ex.Message
    | FileReadFailed (path, ex) ->
        eprintfn "FILE ERROR: %s" (AbsoluteFilePath.value path)
        eprintfn "  %s" ex.Message

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

/// Default mode: stream skill-by-skill through the lazy scanner and dump
/// every YAML field. The consumer side is fully async — `let! canRead = ...`
/// yields the thread back while the parallel parser workers fill the channel,
/// so the pipeline actually overlaps IO + parsing instead of blocking on each
/// element.
let private dumpMetadata (rootDir: string) : Task<int> =
    task {
        use cts = new CancellationTokenSource()
        Console.CancelKeyPress.Add(fun args ->
            args.Cancel <- true
            cts.Cancel())

        let opts =
            { RootDirectory       = AbsoluteFilePath.createUnsafe rootDir
              Pattern             = "SKILL.md"
              Parallelism         = 16
              PathQueueCapacity   = 512
              ResultQueueCapacity = 512 }

        let reader = scan opts cts.Token

        let mutable parsed   = 0
        let mutable skipped  = 0
        let mutable errors   = 0
        let mutable keepGoing = true

        while keepGoing do
            let! canRead = reader.WaitToReadAsync(cts.Token).AsTask()
            if not canRead then
                keepGoing <- false
            else
                let mutable item = Unchecked.defaultof<Result<RawSkillData option, ScanError>>
                while reader.TryRead(&item) do
                    match item with
                    | Ok (Some skill) ->
                        parsed <- parsed + 1
                        printfn ""
                        printfn "=== %s ===" (AbsoluteFilePath.value skill.Path)
                        for kv in skill.Fields do
                            let (YamlKey k) = kv.Key
                            printfn "  %s:" k
                            printValue 4 kv.Value
                    | Ok None ->
                        skipped <- skipped + 1
                    | Error err ->
                        errors <- errors + 1
                        printError err

        printfn ""
        printfn "Done. Parsed=%d Skipped=%d Errors=%d" parsed skipped errors
        return if errors = 0 then 0 else 2
    }

/// Schema mode: synchronously discover the inferred record type for the
/// directory and print it as F# code annotated with frequency stats.
/// Use case: an agent runs this once, reads the output, then writes typed
/// F# code on top of the SkillTypeProvider knowing the exact shape.
let private dumpSchema (rootDir: string) : int =
    let report = discoverSchemaWithStats rootDir "SKILL.md"
    printfn "%s" (formatSchema report)
    0

[<EntryPoint>]
let main argv =
    match argv with
    | [| rootDir |] when Directory.Exists rootDir ->
        // Block once at the very top — everything inside dumpMetadata is async.
        (dumpMetadata rootDir).GetAwaiter().GetResult()

    | [| rootDir; "--schema" |]
    | [| "--schema"; rootDir |] when Directory.Exists rootDir ->
        dumpSchema rootDir

    | [| rootDir |] | [| rootDir; _ |] | [| _; rootDir |] ->
        eprintfn "Directory does not exist: %s" rootDir
        1

    | _ ->
        eprintfn "Usage:"
        eprintfn "  dotnet run -- <skills-root-directory>           # dump every SKILL.md's metadata"
        eprintfn "  dotnet run -- <skills-root-directory> --schema  # print inferred F# record type"
        1
