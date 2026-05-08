namespace YamlFrontMatter

open System.Threading
open YamlFrontMatter.Types
open YamlFrontMatter.Schemas
open YamlFrontMatter.Scanner

// ---------------------------------------------------------------------------
// Lightweight rejection / skip records exposed to TP-generated code via
// quotations. Plain records (not the Scanner DUs) so the `seq<_>` returned
// to consumers contains stable, reflective types.
// ---------------------------------------------------------------------------

type FrontMatterRejection =
    { Path:        AbsoluteFilePath
      Failures:    ValidationFailure list }

type FrontMatterSkip =
    { Path:        AbsoluteFilePath
      Reason:      SkipReason }

[<AbstractClass; Sealed>]
type RuntimeHelpers private () =

    // -----------------------------------------------------------------------
    // Field accessors — used by the spliced quotation bodies of TP-generated
    // properties. Total over Map<YamlKey, YamlValue>: never throw; callers
    // always get back an Option.
    // -----------------------------------------------------------------------

    static member TryGetString(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YString s) -> Some s
        | Some (YBool b)   -> Some (string b)
        | Some (YInt i)    -> Some (string i)
        | Some (YFloat f)  -> Some (string f)
        | _                -> None

    static member TryGetBool(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YBool b) -> Some b
        | _              -> None

    static member TryGetInt(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YInt i) -> Some i
        | _             -> None

    static member TryGetFloat(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YFloat f) -> Some f
        | _               -> None

    static member TryGetStringList(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YList items) ->
            let strings = items |> List.choose (function YString s -> Some s | _ -> None)
            if strings.Length = items.Length then Some strings else None
        | _ -> None

    static member TryGetSubMap(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YMap m) -> Some m
        | _             -> None

    // -----------------------------------------------------------------------
    // Mode → schema reconstruction
    //
    // The TP encodes its `Mode` static parameter as a string ("skill" or
    // "general") because TP static params can only be primitive types. The
    // runtime-side `RuntimeHelpers` reconstructs the corresponding
    // FrontMatterSchema before each scan.
    //
    // Custom `Required [...]` schemas can't currently be expressed via TP
    // static params; users wanting that should call `FrontMatterReader.tryRead`
    // / `Scanner.scan` directly.
    // -----------------------------------------------------------------------

    static member private SchemaForMode(mode: string) : FrontMatterSchema =
        match (if isNull mode then "general" else mode.Trim().ToLowerInvariant()) with
        | "skill"   -> Skill
        | "general" -> General
        | _         -> General   // forward-compat: unknown mode → permissive

    static member private DefaultScanOptions(rootDir: string, pattern: string) : ScanOptions =
        { RootDirectory       = AbsoluteFilePath.createUnsafe rootDir
          Pattern             = pattern
          Parallelism         = 8
          PathQueueCapacity   = 256
          ResultQueueCapacity = 256 }

    // -----------------------------------------------------------------------
    // Public seq-yielding helpers — called from spliced quotations in
    // `GetAll()` / `GetRejected()` / `GetSkipped()` provided methods.
    //
    // Each runs its own scan (small/medium collections; if perf matters we
    // can add a cache later). The seqs are lazy — caller can break early.
    // -----------------------------------------------------------------------

    static member GetAll(rootDir: string, pattern: string, mode: string) : RawFrontMatter seq =
        seq {
            let ct     = CancellationToken.None
            let schema = RuntimeHelpers.SchemaForMode mode
            let opts   = RuntimeHelpers.DefaultScanOptions(rootDir, pattern)
            for item in scanAll schema opts ct do
                match item with
                | ItemValid raw -> yield raw
                | _ -> ()
        }

    static member GetRejected(rootDir: string, pattern: string, mode: string) : FrontMatterRejection seq =
        seq {
            let ct     = CancellationToken.None
            let schema = RuntimeHelpers.SchemaForMode mode
            let opts   = RuntimeHelpers.DefaultScanOptions(rootDir, pattern)
            for item in scanAll schema opts ct do
                match item with
                | ItemRejected (path, failures) ->
                    yield { Path = path; Failures = failures }
                | _ -> ()
        }

    static member GetSkipped(rootDir: string, pattern: string, mode: string) : FrontMatterSkip seq =
        seq {
            let ct     = CancellationToken.None
            let schema = RuntimeHelpers.SchemaForMode mode
            let opts   = RuntimeHelpers.DefaultScanOptions(rootDir, pattern)
            for item in scanAll schema opts ct do
                match item with
                | ItemSkipped (path, reason) ->
                    yield { Path = path; Reason = reason }
                | _ -> ()
        }
