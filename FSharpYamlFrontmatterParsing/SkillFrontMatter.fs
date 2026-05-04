module FSharpYamlFrontmatterParsing.SkillFrontMatter

open FSharpYamlFrontmatterParsing.FrontMatterTextReader

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open YamlDotNet.Serialization

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

    let private toReadOnlyDictionary (value: Dictionary<string, obj> | null) =
        if isNull (box value) then
            Dictionary<string, obj>() :> IReadOnlyDictionary<string, obj>
        else
            value :> IReadOnlyDictionary<string, obj>

    let tryReadOne
        (deserializer: IDeserializer)
        (path: string)
        : Result<SkillFrontMatter option, SkillReadError> =

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
            | None ->
                Ok None

            | Some yamlReader ->
                use yamlReader = yamlReader

                try
                    let metadata =
                        deserializer.Deserialize<Dictionary<string, obj>>(yamlReader)
                        |> toReadOnlyDictionary

                    let endReason = yamlReader.DrainToEnd()

                    match endReason with
                    | Some ClosedByDelimiter ->
                        Ok
                            (Some
                                { Path = path
                                  Metadata = metadata })

                    | Some PhysicalEndOfFile
                    | None ->
                        Error(MissingClosingDelimiter path)

                with ex ->
                    // Даже если YAML-парсер упал, можно дочитать адаптер,
                    // чтобы понять: это реально YAML-ошибка или просто нет closing ---.
                    let endReason = yamlReader.DrainToEnd()

                    match endReason with
                    | Some PhysicalEndOfFile
                    | None ->
                        Error(MissingClosingDelimiter path)

                    | Some ClosedByDelimiter ->
                        Error(YamlParseFailed(path, ex))

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

        ignore producer // fire-and-forget; channel completion signals consumers

        // Consumer worker: read paths, parse front matter, write results
        let worker () = task {
            let deserializer = DeserializerBuilder().Build()
            let pathReader = pathChannel.Reader

            let mutable keepReading = true

            while keepReading do
                let! canRead = pathReader.WaitToReadAsync(ct).AsTask()

                if not canRead then
                    keepReading <- false
                else
                    let mutable path = ""

                    while pathReader.TryRead(&path) do
                        let result = tryReadOne deserializer path
                        do! resultChannel.Writer.WriteAsync(result, ct)
        }

        let workers = Array.init options.Parallelism (fun _ -> worker () :> Task)

        Task
            .WhenAll(workers)
            .ContinueWith(fun _ -> resultChannel.Writer.Complete())
        |> ignore

        resultChannel.Reader
