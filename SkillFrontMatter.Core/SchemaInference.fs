module SkillFrontMatter.Core.SchemaInference

open System
open System.IO
open System.Text
open System.Collections
open System.Collections.Generic
open VYaml.Serialization
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
// VYaml deserializes YAML into a tree of native CLR objects:
//   scalar  → bool / int / int64 / double / string / null
//   mapping → Dictionary<object, object?>  (implements IDictionary)
//   sequence → List<object?>               (implements IList)
// We pattern-match on those CLR types directly.
// ---------------------------------------------------------------------------

let private toYamlKey (k: obj) : YamlKey =
    match k with
    | :? string as s -> YamlKey s
    | other          -> YamlKey (string other)

let rec inferNodeType (node: obj) : InferredType =
    match node with
    | null                     -> TString
    | :? bool                  -> TBool
    | :? int | :? int64        -> TInt
    | :? double | :? single    -> TFloat
    | :? string                -> TString
    | :? IDictionary as dict ->
        let entries =
            [ for entry in dict ->
                let entry = entry :?> DictionaryEntry
                let key    = toYamlKey entry.Key
                let schema = { Type = inferNodeType entry.Value; PresentInAll = true }
                key, schema ]
            |> Map.ofList
        TMapping entries
    | :? IList as list ->
        if list.Count = 0 then
            TList TString
        else
            let unified =
                [ for x in list -> x ]
                |> List.map inferNodeType
                |> List.reduce mergeTypes
            TList unified
    | _ -> TString

// ---------------------------------------------------------------------------
// Cross-file schema inference
// ---------------------------------------------------------------------------

/// Given a list of per-file raw field maps, derive the final DiscoveredSchema.
/// A field is PresentInAll=true only if it appears in every file.
let inferSchema (fileMaps: Map<YamlKey, obj> list) : DiscoveredSchema =
    let totalFiles = List.length fileMaps
    if totalFiles = 0 then Map.empty
    else
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

/// Parse a YAML text fragment into the raw map. The fragment is expected to be
/// a top-level mapping; non-mappings or empty input yield Map.empty.
let parseYamlText (yamlText: string) : Map<YamlKey, obj> =
    if String.IsNullOrWhiteSpace yamlText then Map.empty
    else
        let bytes = Encoding.UTF8.GetBytes yamlText
        let memory = ReadOnlyMemory<byte>(bytes)
        match YamlSerializer.Deserialize<obj>(memory) with
        | :? IDictionary as dict ->
            [ for entry in dict ->
                let entry = entry :?> DictionaryEntry
                toYamlKey entry.Key, entry.Value ]
            |> Map.ofList
        | _ -> Map.empty

/// Try to parse one file's front matter into a raw Map<YamlKey, obj>.
/// Returns None if there is no front matter, or Error on parse/IO failure.
let tryParseRawFrontMatter (path: string) : Result<Map<YamlKey, obj> option, string> =
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
                let yamlText = yamlReader.ReadToEnd()
                Ok(Some(parseYamlText yamlText))
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

let rec objToValue (node: obj) : YamlValue =
    match node with
    | null                  -> YString ""
    | :? bool   as b        -> YBool b
    | :? int    as i        -> YInt i
    | :? int64  as i        -> YInt (int i)
    | :? double as d        -> YFloat d
    | :? single as f        -> YFloat (float f)
    | :? string as s        -> YString s
    | :? IDictionary as dict ->
        let entries =
            [ for entry in dict ->
                let entry = entry :?> DictionaryEntry
                toYamlKey entry.Key, objToValue entry.Value ]
            |> Map.ofList
        YMap entries
    | :? IList as list ->
        [ for x in list -> objToValue x ]
        |> YList
    | other -> YString (string other)
