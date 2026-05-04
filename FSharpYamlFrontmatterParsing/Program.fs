// For more information see https://aka.ms/fsharp-console-apps
open System
open System.IO
open System.Threading
open System.Threading.Channels
open FSharpYamlFrontmatterParsing.SkillFrontMatter
open FSharpYamlFrontmatterParsing.FrontMatterTextReader
open YamlDotNet.Serialization

let printError error =
    match error with
    | MissingClosingDelimiter path ->
        eprintfn "BROKEN FRONT MATTER: %s" path

    | YamlParseFailed(path, ex) ->
        eprintfn "YAML ERROR: %s" path
        eprintfn "  %s" ex.Message

    | FileReadFailed(path, ex) ->
        eprintfn "FILE ERROR: %s" path
        eprintfn "  %s" ex.Message

let run rootDirectory =
    task {
        use cts = new CancellationTokenSource()

        Console.CancelKeyPress.Add(fun args ->
            args.Cancel <- true
            cts.Cancel())

        let reader =
            SkillFrontMatter.scan
                rootDirectory = rootDirectory
                pattern = "SKILL.md"
                parallelism = 16
                pathQueueCapacity = 512
                resultQueueCapacity = 512
                ct = cts.Token

        let mutable parsedCount = 0
        let mutable skippedCount = 0
        let mutable errorCount = 0

        let mutable keepReading = true

        while keepReading do
            let! canRead = reader.WaitToReadAsync(cts.Token).AsTask()

            if not canRead then
                keepReading <- false
            else
                let mutable item =
                    Unchecked.defaultof<Result<SkillFrontMatter option, SkillReadError>>

                while reader.TryRead(&item) do
                    match item with
                    | Ok(Some skill) ->
                        parsedCount <- parsedCount + 1

                        printfn ""
                        printfn "=== %s ===" skill.Path

                        for pair in skill.Metadata do
                            printfn "%s: %O" pair.Key pair.Value

                    | Ok None ->
                        skippedCount <- skippedCount + 1

                    | Error error ->
                        errorCount <- errorCount + 1
                        printError error

        printfn ""
        printfn "Done."
        printfn "Parsed:  %d" parsedCount
        printfn "Skipped: %d" skippedCount
        printfn "Errors:  %d" errorCount

        return if errorCount = 0 then 0 else 2
    }

[<EntryPoint>]
let main argv =
    match argv with
    | [| rootDirectory |] when IO.Directory.Exists rootDirectory ->
        run rootDirectory
            .GetAwaiter()
            .GetResult()

    | [| rootDirectory |] ->
        eprintfn "Directory does not exist: %s" rootDirectory
        1

    | _ ->
        eprintfn "Usage:"
        eprintfn "  dotnet run -- <skills-root-directory>"
        1