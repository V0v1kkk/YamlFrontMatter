module YamlFrontMatter.SchemaInference

open System
open System.IO
open System.Text
open System.Collections
open System.Collections.Generic
open VYaml.Serialization
open YamlFrontMatter.Types
open YamlFrontMatter.FrontMatterTextReader

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

/// Result of running schema discovery: the inferred schema plus the raw
/// statistics needed to render a frequency-annotated visualization.
type DiscoveryReport =
    { Schema:           DiscoveredSchema
      FilesScanned:     int
      /// Number of files containing each top-level field. Fields not present
      /// in any file simply do not appear in the schema (or this map).
      FieldOccurrences: Map<YamlKey, int> }

/// Given a list of per-file raw field maps, derive the final DiscoveredSchema
/// together with the per-field occurrence counts and the total file count.
let inferSchemaWithStats (fileMaps: Map<YamlKey, obj> list) : DiscoveryReport =
    let totalFiles = List.length fileMaps
    if totalFiles = 0 then
        { Schema = Map.empty; FilesScanned = 0; FieldOccurrences = Map.empty }
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

        let schema =
            acc
            |> Seq.map (fun kv ->
                let key = kv.Key
                let (itype, count) = kv.Value
                key, { Type = itype; PresentInAll = (count = totalFiles) })
            |> Map.ofSeq

        let occurrences =
            acc
            |> Seq.map (fun kv -> kv.Key, snd kv.Value)
            |> Map.ofSeq

        { Schema = schema; FilesScanned = totalFiles; FieldOccurrences = occurrences }

/// Given a list of per-file raw field maps, derive the final DiscoveredSchema.
/// A field is PresentInAll=true only if it appears in every file.
let inferSchema (fileMaps: Map<YamlKey, obj> list) : DiscoveredSchema =
    (inferSchemaWithStats fileMaps).Schema

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

/// Scan a directory synchronously, infer the schema and per-field statistics
/// from all matching files. Errors are silently skipped — design-time we want
/// best-effort schema even if a few files are broken.
let discoverSchemaWithStats (rootDir: string) (pattern: string) : DiscoveryReport =
    let fileMaps =
        Directory.EnumerateFiles(rootDir, pattern, SearchOption.AllDirectories)
        |> Seq.choose (fun path ->
            match tryParseRawFrontMatter path with
            | Ok(Some m) -> Some m
            | _          -> None)
        |> Seq.toList
    inferSchemaWithStats fileMaps

/// Scan a directory synchronously, infer the schema from all matching files.
/// Errors are silently skipped (design-time: we want schema even if a few files are broken).
let discoverSchema (rootDir: string) (pattern: string) : DiscoveredSchema =
    (discoverSchemaWithStats rootDir pattern).Schema

// ---------------------------------------------------------------------------
// Schema visualization
//
// Renders a DiscoveryReport as a valid F# record declaration. Designed so an
// agent (or human) can read the inferred shape, see how often each top-level
// field occurs, and write strongly-typed filter code on top of it.
// ---------------------------------------------------------------------------

/// Render a DiscoveryReport as F# record types annotated with frequency
/// information. Top-level fields show "present in N/M files"; fields inside
/// nested mappings show "always" / "sometimes" within their parent record.
///
/// `isSkillMode` controls how `name` / `description` are rendered:
///   * true  — emitted as typed required properties (`Name : SkillName`,
///             `Description : SkillDescription`) and skipped in the regular
///             field loop. Matches what the type provider exposes when
///             `Mode = "skill"`.
///   * false — treated as ordinary discovered fields (`Name : string option`)
///             that appear in the normal loop alongside everyone else.
///             Matches `Mode = "general"` and any custom Required schema.
///
/// Padding is computed dynamically from the longest property name in the
/// record so long names like `RequiresEnvironmentVariables` don't break
/// alignment.
let private formatSchemaCore (isSkillMode: bool) (report: DiscoveryReport) : string =
    let sb = StringBuilder()

    // Nested TMappings get their own `and`-record. We collect them as we walk
    // and emit them after the root record. `seen` deduplicates by name.
    let pending = ResizeArray<string * Map<YamlKey, FieldSchema>>()
    let seen    = HashSet<string>()

    let rec renderType (parentName: string) (t: InferredType) : string =
        match t with
        | TString          -> "string"
        | TBool            -> "bool"
        | TInt             -> "int"
        | TFloat           -> "float"
        | TList inner      -> renderType parentName inner + " list"
        | TMapping fields  ->
            let typeName = parentName + "Data"
            if seen.Add typeName then
                pending.Add(typeName, fields)
            typeName

    let isSkillSpecialKey (YamlKey k) = isSkillMode && (k = "name" || k = "description")

    // Compute the padding once per record from the longest property name in
    // that record, so long names (e.g. `RequiresEnvironmentVariables`) don't
    // glue the colon to the name. Minimum 16 to keep short records readable.
    let computeNamePadding (recordHasSkillSpecials: bool) (fields: seq<YamlKey * 'a>) : int =
        let candidates =
            seq {
                yield "Path".Length
                if recordHasSkillSpecials then
                    yield "Name".Length
                    yield "Description".Length
                for (YamlKey rawKey, _) in fields do
                    yield (toPascalCase rawKey).Length
            }
        max 16 (Seq.max candidates)

    let appendField (namePad: int) (name: string) (typeStr: string) (annotation: string) =
        sb.Append("    ")
          .Append(name.PadRight(namePad))
          .Append(" : ")
          .Append(typeStr.PadRight(34))
          .Append(" // ")
          .AppendLine(annotation)
        |> ignore

    // --- Root record -------------------------------------------------------
    let topLevelEntries =
        report.Schema
        |> Map.toSeq
        |> Seq.filter (fun (k, _) -> not (isSkillSpecialKey k))
        |> Seq.sortBy (fun (YamlKey k, _) -> k)
        |> Seq.toList

    let rootPad = computeNamePadding isSkillMode topLevelEntries

    sb.AppendLine("type SkillDefinition = {") |> ignore
    appendField rootPad "Path" "AbsoluteFilePath" "always (synthesised by the scanner)"

    if isSkillMode then
        appendField rootPad "Name"        "SkillName"        "required (SKILL.md convention)"
        appendField rootPad "Description" "SkillDescription" "required (SKILL.md convention)"

    for (yamlKey, schema) in topLevelEntries do
        let (YamlKey rawKey) = yamlKey
        let propName = toPascalCase rawKey
        let inner    = renderType propName schema.Type
        let typeStr  = inner + " option"
        let count    = Map.tryFind yamlKey report.FieldOccurrences |> Option.defaultValue 0
        let annotation = sprintf "present in %d/%d files" count report.FilesScanned
        appendField rootPad propName typeStr annotation

    sb.AppendLine("}") |> ignore

    // --- Nested records ---------------------------------------------------
    let mutable i = 0
    while i < pending.Count do
        let (typeName, fields) = pending.[i]
        sb.AppendLine() |> ignore
        sb.Append("and ").Append(typeName).AppendLine(" = {") |> ignore

        let entries =
            fields
            |> Map.toSeq
            |> Seq.sortBy (fun (YamlKey k, _) -> k)
            |> Seq.toList

        let nestedPad = computeNamePadding false entries

        for (yamlKey, schema) in entries do
            let (YamlKey rawKey) = yamlKey
            let propName = toPascalCase rawKey
            let inner    = renderType propName schema.Type
            let typeStr  = inner + " option"
            let annotation =
                if schema.PresentInAll then "always present in this record"
                else "optional within this record"
            appendField nestedPad propName typeStr annotation

        sb.AppendLine("}") |> ignore
        i <- i + 1

    if report.FilesScanned = 0 then
        sb.AppendLine() |> ignore
        sb.AppendLine("// (no files matched — schema reflects only the mode-required fields)") |> ignore

    sb.ToString()

/// Render the discovered schema for a *general* / non-skill collection: every
/// field is optional, no special handling for `name` / `description`. Use
/// `formatSchemaForMode` if the rendering should match a specific TP Mode.
let formatSchema (report: DiscoveryReport) : string =
    formatSchemaCore false report

/// Render the discovered schema in the way that matches the supplied schema.
/// Used by the type provider's `Describe()` so the printed F# record matches
/// the actual properties the TP generated for that mode.
let formatSchemaForMode (isSkillMode: bool) (report: DiscoveryReport) : string =
    formatSchemaCore isSkillMode report
