module FSharpYamlFrontmatterParsing.SkillFrontMatter

open FSharpYamlFrontmatterParsing.FrontMatterTextReader

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open YamlDotNet.RepresentationModel

type SkillFrontMatter =
    { Path: string
      Metadata: IReadOnlyDictionary<string, obj> }

type SkillReadError =
    | MissingClosingDelimiter of path: string
    | YamlParseFailed of path: string * error: exn
    | FileReadFailed of path: string * error: exn

type ScanOptions =
    { RootDirectory: string
      Pattern: string
      Parallelism: int
      PathQueueCapacity: int
      ResultQueueCapacity: int }

module SkillFrontMatter =

    let private openSkillFile path =
        let options = FileStreamOptions()
        options.Mode <- FileMode.Open
        options.Access <- FileAccess.Read
        options.Share <- FileShare.ReadWrite ||| FileShare.Delete
        options.BufferSize <- 16 * 1024
        options.Options <- FileOptions.None
        new FileStream(path, options)

    // Walk the YamlDotNet AST and convert to plain F# objects:
    //   YamlScalarNode   → string
    //   YamlMappingNode  → Dictionary<string, obj>
    //   YamlSequenceNode → ResizeArray<obj>
    let rec private nodeToObj (node: YamlNode) : obj =
        match node with
        | :? YamlScalarNode as s -> box s.Value
        | :? YamlMappingNode as m ->
            let dict = Dictionary<string, obj>(m.Children.Count)
            for kv in m.Children do
                let key =
                    match kv.Key with
                    | :? YamlScalarNode as k -> k.Value
                    | other -> other.ToString()
                dict.[key] <- nodeToObj kv.Value
            box dict
        | :? YamlSequenceNode as seq ->
            let list = ResizeArray<obj>(seq.Children.Count)
            for item in seq.Children do
                list.Add(nodeToObj item)
            box list
        | other -> box (other.ToString())

    let private parseYaml (reader: TextReader) : IReadOnlyDictionary<string, obj> =
        let stream = YamlStream()
        stream.Load(reader)

        if stream.Documents.Count = 0 then
            Dictionary<string, obj>() :> IReadOnlyDictionary<string, obj>
        else
            match stream.Documents.[0].RootNode with
            | :? YamlMappingNode as mapping ->
                let dict = Dictionary<string, obj>(mapping.Children.Count)
                for kv in mapping.Children do
                    let key =
                        match kv.Key with
                        | :? YamlScalarNode as k -> k.Value
                        | other -> other.ToString()
                    dict.[key] <- nodeToObj kv.Value
                dict :> IReadOnlyDictionary<string, obj>
            | _ ->
                Dictionary<string, obj>() :> IReadOnlyDictionary<string, obj>

    let tryReadOne (path: string) : Result<SkillFrontMatter option, SkillReadError> =
        try
            use stream = openSkillFile path

            use reader =
                new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks = true,
                    bufferSize = 16 * 1024,
                    leaveOpen = false)

            match FrontMatterTextReader.TryCreate(reader) with
            | None -> Ok None
            | Some yamlReader ->
                use yamlReader = yamlReader

                try
                    let metadata = parseYaml yamlReader
                    let endReason = yamlReader.DrainToEnd()

                    match endReason with
                    | Some ClosedByDelimiter ->
                        Ok(Some { Path = path; Metadata = metadata })
                    | Some PhysicalEndOfFile
                    | None ->
                        Error(MissingClosingDelimiter path)

                with ex ->
                    let endReason = yamlReader.DrainToEnd()

                    match endReason with
                    | Some PhysicalEndOfFile
                    | None -> Error(MissingClosingDelimiter path)
                    | Some ClosedByDelimiter -> Error(YamlParseFailed(path, ex))

        with ex ->
            Error(FileReadFailed(path, ex))

    let scan (options: ScanOptions) (ct: CancellationToken) : ChannelReader<Result<SkillFrontMatter option, SkillReadError>> =

        let pathChannel = Channel.CreateBounded<string>(options.PathQueueCapacity)
        let resultChannel = Channel.CreateBounded<Result<SkillFrontMatter option, SkillReadError>>(options.ResultQueueCapacity)

        // Producer: walk the directory tree and push file paths
        let producer = task {
            try
                let files =
                    Directory.EnumerateFiles(options.RootDirectory, options.Pattern, SearchOption.AllDirectories)
                for file in files do
                    do! pathChannel.Writer.WriteAsync(file, ct)
            with ex ->
                pathChannel.Writer.Complete(ex)
                return ()
            pathChannel.Writer.Complete()
        }

        ignore producer

        // Worker: no shared state — YamlStream is created per call inside tryReadOne
        let worker () = task {
            let pathReader = pathChannel.Reader
            let mutable keepReading = true

            while keepReading do
                let! canRead = pathReader.WaitToReadAsync(ct).AsTask()

                if not canRead then
                    keepReading <- false
                else
                    let mutable path = ""
                    while pathReader.TryRead(&path) do
                        let result = tryReadOne path
                        do! resultChannel.Writer.WriteAsync(result, ct)
        }

        let workers = Array.init options.Parallelism (fun _ -> worker () :> Task)

        Task
            .WhenAll(workers)
            .ContinueWith(fun _ -> resultChannel.Writer.Complete())
        |> ignore

        resultChannel.Reader
