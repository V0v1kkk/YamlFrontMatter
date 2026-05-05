module YamlFrontMatter.Scanner

open System.IO
open System.Text
open System.Threading
open System.Threading.Channels
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.FrontMatterTextReader

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

type ScanError =
    | MissingClosingDelimiter of AbsoluteFilePath
    | YamlParseFailed         of AbsoluteFilePath * exn
    | FileReadFailed          of AbsoluteFilePath * exn

type RawSkillData =
    { Path:   AbsoluteFilePath
      Fields: Map<YamlKey, YamlValue> }

type ScanOptions =
    { RootDirectory:       AbsoluteFilePath
      Pattern:             string
      Parallelism:         int
      PathQueueCapacity:   int
      ResultQueueCapacity: int }

// ---------------------------------------------------------------------------
// File reading
// ---------------------------------------------------------------------------

let private openFile (path: AbsoluteFilePath) =
    let p = AbsoluteFilePath.value path
    new FileStream(p, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite ||| FileShare.Delete, 16 * 1024)

let tryReadOne (filePath: AbsoluteFilePath) : Result<RawSkillData option, ScanError> =
    try
        use stream = openFile filePath
        use reader = new StreamReader(stream, Encoding.UTF8,
                                       detectEncodingFromByteOrderMarks = true,
                                       bufferSize = 16 * 1024,
                                       leaveOpen = false)
        match FrontMatterTextReader.TryCreate(reader) with
        | None -> Ok None
        | Some yamlReader ->
            use yamlReader = yamlReader
            try
                let yamlText  = yamlReader.ReadToEnd()
                let endReason = yamlReader.EndReason

                match endReason with
                | Some ClosedByDelimiter ->
                    let rawMap = parseYamlText yamlText
                    let fields =
                        rawMap
                        |> Map.toSeq
                        |> Seq.map (fun (k, v) -> k, objToValue v)
                        |> Map.ofSeq
                    Ok(Some { Path = filePath; Fields = fields })
                | _ -> Error(MissingClosingDelimiter filePath)

            with ex ->
                match yamlReader.EndReason with
                | Some ClosedByDelimiter -> Error(YamlParseFailed(filePath, ex))
                | _                      -> Error(MissingClosingDelimiter filePath)

    with ex ->
        Error(FileReadFailed(filePath, ex))

// ---------------------------------------------------------------------------
// Parallel Channel-based scanner
// ---------------------------------------------------------------------------

let scan (options: ScanOptions) (ct: CancellationToken) :
        ChannelReader<Result<RawSkillData option, ScanError>> =

    let rootPath = AbsoluteFilePath.value options.RootDirectory

    let pathChannel   = Channel.CreateBounded<AbsoluteFilePath>(options.PathQueueCapacity)
    let resultChannel = Channel.CreateBounded<Result<RawSkillData option, ScanError>>(options.ResultQueueCapacity)

    let producer = System.Threading.Tasks.Task.Run(fun () ->
        try
            Directory.EnumerateFiles(rootPath, options.Pattern, SearchOption.AllDirectories)
            |> Seq.iter (fun path ->
                let fp = AbsoluteFilePath.createUnsafe path
                pathChannel.Writer.WriteAsync(fp, ct).AsTask().GetAwaiter().GetResult())
        with ex ->
            pathChannel.Writer.Complete(ex)
            ()
        pathChannel.Writer.Complete())

    ignore producer

    let worker () = System.Threading.Tasks.Task.Run(fun () ->
        let pathReader = pathChannel.Reader
        let mutable keepReading = true
        while keepReading do
            let canRead = pathReader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()
            if not canRead then
                keepReading <- false
            else
                let mutable path = Unchecked.defaultof<AbsoluteFilePath>
                while pathReader.TryRead(&path) do
                    let result = tryReadOne path
                    resultChannel.Writer.WriteAsync(result, ct).AsTask().GetAwaiter().GetResult())

    let workers = Array.init options.Parallelism (fun _ -> worker ())

    System.Threading.Tasks.Task
        .WhenAll(workers)
        .ContinueWith(fun _ -> resultChannel.Writer.Complete())
    |> ignore

    resultChannel.Reader

