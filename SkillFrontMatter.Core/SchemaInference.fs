module SkillFrontMatter.Core.SchemaInference

open System
open System.IO
open System.Text
open System.Collections.Generic
open YamlDotNet.RepresentationModel
open SkillFrontMatter.Core.Types
open SkillFrontMatter.Core.FrontMatterTextReader

// ---------------------------------------------------------------------------
// Inferred YAML value types
// ---------------------------------------------------------------------------

type InferredType =
    | TString
    | TBool
    | TInt
    | TFloat
    | TList   of InferredType
    | TMapping of Map<YamlKey, FieldSchema>

and FieldSchema =
    { Type:         InferredType
      PresentInAll: bool }

type DiscoveredSchema = Map<YamlKey, FieldSchema>

// ---------------------------------------------------------------------------
// Type widening lattice
// ---------------------------------------------------------------------------

/// Merge two inferred types into the widest safe common type.
/// TBool < TInt < TFloat < TString
/// TList  widened element-wise
/// TMapping + TMapping → merge children (fields missing in one side → optional)
/// TMapping + non-TMapping (or vice-versa) → TString (safest fallback)
let rec mergeTypes (a: InferredType) (b: InferredType) : InferredType =
    match a, b with
    | x, y when x = y -> x

    // numeric widening
    | TBool,  TInt    -> TInt
    | TInt,   TBool   -> TInt
    | TBool,  TFloat  -> TFloat
    | TFloat, TBool   -> TFloat
    | TInt,   TFloat  -> TFloat
    | TFloat, TInt    -> TFloat

    // anything + TString → TString
    | _, TString | TString, _ -> TString

    // list element widening
    | TList ea, TList eb -> TList (mergeTypes ea eb)

    // list + scalar → TString
    | TList _, _ | _, TList _ -> TString

    // mapping + mapping → merge children
    | TMapping fa, TMapping fb ->
        let allKeys = Set.union (fa |> Map.keys |> Set.ofSeq) (fb |> Map.keys |> Set.ofSeq)
        let merged =
            allKeys
            |> Set.toSeq
            |> Seq.map (fun key ->
                match Map.tryFind key fa, Map.tryFind key fb with
                | Some sa, Some sb ->
                    key, { Type = mergeTypes sa.Type sb.Type; PresentInAll = sa.PresentInAll && sb.PresentInAll }
                | Some sa, None ->
                    key, { sa with PresentInAll = false }
                | None, Some sb ->
                    key, { sb with PresentInAll = false }
                | None, None ->
                    key, { Type = TString; PresentInAll = false })
            |> Map.ofSeq
        TMapping merged

    // mapping + non-mapping conflict → TString
    | TMapping _, _ | _, TMapping _ -> TString

    // any other combo not yet listed → TString
    | _ -> TString

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let toPascalCase (s: string) : string =
    s.Split([| '-'; '_'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun w ->
        if w.Length = 0 then w
        else string (Char.ToUpperInvariant w.[0]) + w.[1..])
    |> String.concat ""

// ---------------------------------------------------------------------------
// Node → InferredType
// ---------------------------------------------------------------------------

let rec inferNodeType (node: YamlNode) : InferredType =
    match node with
    | :? YamlScalarNode as s ->
        // Quoted scalars are explicitly strings regardless of content
        match s.Style with
        | YamlDotNet.Core.ScalarStyle.SingleQuoted
        | YamlDotNet.Core.ScalarStyle.DoubleQuoted
        | YamlDotNet.Core.ScalarStyle.Literal
        | YamlDotNet.Core.ScalarStyle.Folded -> TString
        | _ ->
            let v = s.Value
            let mutable boolVal  = false
            let mutable intVal   = 0L
            let mutable floatVal = 0.0
            if Boolean.TryParse(v, &boolVal) then TBool
            elif Int64.TryParse(v, &intVal)  then TInt
            elif Double.TryParse(v, Globalization.NumberStyles.Float,
                                    Globalization.CultureInfo.InvariantCulture, &floatVal) then TFloat
            else TString

    | :? YamlSequenceNode as seq ->
        let items = seq.Children
        if items.Count = 0 then
            TList TString   // empty → assume string elements
        else
            let unified =
                items
                |> Seq.map inferNodeType
                |> Seq.reduce mergeTypes
            TList unified

    | :? YamlMappingNode as m ->
        let fields =
            m.Children
            |> Seq.map (fun kv ->
                let key =
                    match kv.Key with
                    | :? YamlScalarNode as k -> YamlKey k.Value
                    | other -> YamlKey(other.ToString())
                let schema = { Type = inferNodeType kv.Value; PresentInAll = true }
                key, schema)
            |> Map.ofSeq
        TMapping fields

    | _ -> TString

// ---------------------------------------------------------------------------
// Cross-file schema inference
// ---------------------------------------------------------------------------

/// Given a list of per-file raw field maps, derive the final DiscoveredSchema.
/// A field is PresentInAll=true only if it appears in every file.
let inferSchema (fileMaps: Map<YamlKey, YamlNode> list) : DiscoveredSchema =
    let totalFiles = List.length fileMaps
    if totalFiles = 0 then Map.empty
    else
        // Fold over all files: accumulate (InferredType, count) per key
        let acc = Dictionary<YamlKey, InferredType * int>()

        for fileMap in fileMaps do
            for kv in fileMap do
                let key   = kv.Key
                let itype = inferNodeType kv.Value
                match acc.TryGetValue(key) with
                | true, (existing, count) ->
                    acc.[key] <- (mergeTypes existing itype, count + 1)
                | _ ->
                    acc.[key] <- (itype, 1)

        acc
        |> Seq.map (fun kv ->
            let key = kv.Key
            let (itype, count) = kv.Value
            key, { Type = itype; PresentInAll = (count = totalFiles) })
        |> Map.ofSeq

// ---------------------------------------------------------------------------
// Front matter parsing (sync, for design-time use)
// ---------------------------------------------------------------------------

let private openFileSync (path: string) =
    new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite ||| FileShare.Delete, 16 * 1024)

/// Try to parse one file's front matter into a raw Map<YamlKey, YamlNode>.
/// Returns None if there is no front matter, or Error on parse/IO failure.
let tryParseRawFrontMatter (path: string) : Result<Map<YamlKey, YamlNode> option, string> =
    try
        use stream = openFileSync path
        use reader = new StreamReader(stream, Encoding.UTF8,
                                       detectEncodingFromByteOrderMarks = true,
                                       bufferSize = 16 * 1024,
                                       leaveOpen = false)
        match FrontMatterTextReader.TryCreate(reader) with
        | None -> Ok None
        | Some yamlReader ->
            use yamlReader = yamlReader
            try
                let yamlStream = YamlStream()
                yamlStream.Load(reader :> System.IO.TextReader)
                let _drain = yamlReader.DrainToEnd()

                if yamlStream.Documents.Count = 0 then
                    Ok(Some Map.empty)
                else
                    match yamlStream.Documents.[0].RootNode with
                    | :? YamlMappingNode as mapping ->
                        let fields =
                            mapping.Children
                            |> Seq.map (fun kv ->
                                let key =
                                    match kv.Key with
                                    | :? YamlScalarNode as k -> YamlKey k.Value
                                    | other -> YamlKey(other.ToString())
                                key, kv.Value)
                            |> Map.ofSeq
                        Ok(Some fields)
                    | _ -> Ok(Some Map.empty)
            with ex ->
                Error $"YAML parse error in '%s{path}': %s{ex.Message}"
    with ex ->
        Error $"IO error reading '%s{path}': %s{ex.Message}"

/// Scan a directory synchronously, infer the schema from all matching files.
/// Errors are silently skipped (design-time: we want schema even if a few files are broken).
let discoverSchema (rootDir: string) (pattern: string) : DiscoveredSchema =
    let fileMaps =
        Directory.EnumerateFiles(rootDir, pattern, SearchOption.AllDirectories)
        |> Seq.choose (fun path ->
            match tryParseRawFrontMatter path with
            | Ok(Some m) -> Some m
            | _          -> None)
        |> Seq.toList
    inferSchema fileMaps

// ---------------------------------------------------------------------------
// Runtime typed value (used by generated property bodies)
// ---------------------------------------------------------------------------

type YamlValue =
    | YString of string
    | YBool   of bool
    | YInt    of int
    | YFloat  of float
    | YList   of YamlValue list
    | YMap    of Map<YamlKey, YamlValue>

let rec nodeToValue (node: YamlNode) : YamlValue =
    match node with
    | :? YamlScalarNode as s ->
        match s.Style with
        | YamlDotNet.Core.ScalarStyle.SingleQuoted
        | YamlDotNet.Core.ScalarStyle.DoubleQuoted
        | YamlDotNet.Core.ScalarStyle.Literal
        | YamlDotNet.Core.ScalarStyle.Folded -> YString s.Value
        | _ ->
            let v = s.Value
            let mutable boolVal  = false
            let mutable intVal   = 0L
            let mutable floatVal = 0.0
            if Boolean.TryParse(v, &boolVal)  then YBool boolVal
            elif Int64.TryParse(v, &intVal)   then YInt (int intVal)
            elif Double.TryParse(v, Globalization.NumberStyles.Float,
                                    Globalization.CultureInfo.InvariantCulture, &floatVal) then
                YFloat floatVal
            else YString v
    | :? YamlSequenceNode as seq ->
        seq.Children |> Seq.map nodeToValue |> Seq.toList |> YList
    | :? YamlMappingNode as m ->
        let entries =
            m.Children
            |> Seq.map (fun kv ->
                let key =
                    match kv.Key with
                    | :? YamlScalarNode as k -> YamlKey k.Value
                    | other -> YamlKey(other.ToString())
                key, nodeToValue kv.Value)
            |> Map.ofSeq
        YMap entries
    | _ -> YString(node.ToString())





