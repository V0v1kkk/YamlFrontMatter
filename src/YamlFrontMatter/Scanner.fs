module YamlFrontMatter.Scanner

open System.IO
open System.Threading
open System.Threading.Channels
open YamlFrontMatter.Types
open YamlFrontMatter.Schemas
open YamlFrontMatter.FrontMatterReader

// ---------------------------------------------------------------------------
// Per-file outcomes
//
// `ScanItem` is what the `ChannelReader<>` yields. Three categories, kept
// distinct so consumers can act on them differently:
//
//   • ItemValid    — file has well-formed front matter that satisfies the
//                    schema. The TP exposes only these via `GetAll()`.
//   • ItemRejected — file *has* front matter but it failed schema validation
//                    (missing required field, wrong type, empty string).
//                    Surfaced by the TP via `GetRejected()` for audit views.
//   • ItemSkipped  — file isn't a front-matter document at all (no `---`
//                    block, broken yaml, IO error). Distinct from Rejected
//                    because the file isn't *trying* to be valid.
// ---------------------------------------------------------------------------

type SkipReason =
    /// File has no `---`-delimited YAML front matter region.
    | NoFrontMatter
    /// File starts with `---` but the closing `---` is missing before EOF.
    | UnclosedFrontMatter
    /// Front matter region is delimited but the YAML inside is malformed.
    | YamlMalformed of detail: string
    /// File system error (not found, permission denied, encoding).
    | IoFailure of message: string

type ScanItem =
    | ItemValid    of RawFrontMatter
    | ItemRejected of path: AbsoluteFilePath * failures: ValidationFailure list
    | ItemSkipped  of path: AbsoluteFilePath * reason: SkipReason

type ScanOptions =
    { RootDirectory:       AbsoluteFilePath
      Pattern:             string
      Parallelism:         int
      PathQueueCapacity:   int
      ResultQueueCapacity: int }

// ---------------------------------------------------------------------------
// Adapter: FrontMatterReader.tryRead's Result → ScanItem
// ---------------------------------------------------------------------------

let private toScanItem (path: AbsoluteFilePath) (result: Result<RawFrontMatter, ReadProblem>) : ScanItem =
    match result with
    | Ok raw -> ItemValid raw
    | Error (ValidationFailed failures) ->
        ItemRejected (path, failures)
    | Error ReadProblem.NoFrontMatter ->
        ItemSkipped (path, SkipReason.NoFrontMatter)
    | Error ReadProblem.UnclosedFrontMatter ->
        ItemSkipped (path, SkipReason.UnclosedFrontMatter)
    | Error (ReadProblem.YamlMalformed d) ->
        ItemSkipped (path, SkipReason.YamlMalformed d)
    | Error (ReadProblem.IoFailure m) ->
        ItemSkipped (path, SkipReason.IoFailure m)

// ---------------------------------------------------------------------------
// Parallel Channel-based scanner
//
// One bounded producer enumerates the directory; N bounded workers each
// invoke `FrontMatterReader.tryRead` per path and push the resulting
// `ScanItem` into the result channel. Lazy/streaming: caller can stop
// reading at any point and the producer/workers stop on cancellation.
// ---------------------------------------------------------------------------

let scan (schema: FrontMatterSchema) (options: ScanOptions) (ct: CancellationToken) :
        ChannelReader<ScanItem> =

    let rootPath = AbsoluteFilePath.value options.RootDirectory

    let pathChannel   = Channel.CreateBounded<AbsoluteFilePath>(options.PathQueueCapacity)
    let resultChannel = Channel.CreateBounded<ScanItem>(options.ResultQueueCapacity)

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
                    let result = tryRead schema path
                    let item   = toScanItem path result
                    resultChannel.Writer.WriteAsync(item, ct).AsTask().GetAwaiter().GetResult())

    let workers = Array.init options.Parallelism (fun _ -> worker ())

    System.Threading.Tasks.Task
        .WhenAll(workers)
        .ContinueWith(fun _ -> resultChannel.Writer.Complete())
    |> ignore

    resultChannel.Reader

// ---------------------------------------------------------------------------
// Convenience: drain a channel reader into a lazy seq
//
// The seq is lazy — items are pulled from the channel on demand, so the
// scanner's parallel workers and the consumer's loop overlap naturally.
// Iteration stops when the channel is completed (producer + all workers done).
// ---------------------------------------------------------------------------

/// Wrap a `ChannelReader<ScanItem>` as a lazy `seq<ScanItem>`. Use this when
/// you want to consume scan results with idiomatic F# `Seq.*` combinators
/// without writing the WaitToReadAsync / TryRead loop yourself.
let toSeq (reader: ChannelReader<ScanItem>) (ct: CancellationToken) : seq<ScanItem> =
    seq {
        let mutable keepGoing = true
        while keepGoing do
            let canRead = reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()
            if not canRead then
                keepGoing <- false
            else
                let mutable more = true
                while more do
                    let mutable item = Unchecked.defaultof<ScanItem>
                    if reader.TryRead(&item) then
                        yield item
                    else
                        more <- false
    }

/// One-shot helper: scan a directory and return the results as a lazy seq.
/// Combines `scan` and `toSeq` for callers that don't need direct access to
/// the underlying `ChannelReader`. Streaming + parallel parsing still happen
/// under the hood — the seq pulls items as they become available.
let scanAll (schema: FrontMatterSchema) (options: ScanOptions) (ct: CancellationToken) : seq<ScanItem> =
    let reader = scan schema options ct
    toSeq reader ct
