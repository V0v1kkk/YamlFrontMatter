namespace YamlFrontMatter

open System.Threading
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Scanner

[<AbstractClass; Sealed>]
type RuntimeHelpers private () =

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

    static member GetAll(rootDir: string, pattern: string) : RawSkillData seq =
        seq {
            let ct = CancellationToken.None
            let opts =
                { RootDirectory       = AbsoluteFilePath.createUnsafe rootDir
                  Pattern             = pattern
                  Parallelism         = 8
                  PathQueueCapacity   = 256
                  ResultQueueCapacity = 256 }
            let reader = scan opts ct
            let mutable keepGoing = true
            while keepGoing do
                let canRead = reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()
                if not canRead then
                    keepGoing <- false
                else
                    let mutable more = true
                    while more do
                        let mutable item = Unchecked.defaultof<Result<RawSkillData option, ScanError>>
                        if reader.TryRead(&item) then
                            match item with
                            | Ok (Some skill) -> yield skill
                            | _ -> ()
                        else
                            more <- false
        }
