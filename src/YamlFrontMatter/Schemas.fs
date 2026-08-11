module YamlFrontMatter.Schemas

open System
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference

// ---------------------------------------------------------------------------
// Schema definition
// ---------------------------------------------------------------------------

/// One field requirement: which key must be present, what its type must be,
/// and whether (for string-typed values) it must be non-empty after Trim.
type FieldRequirement =
    { Key:      YamlKey
      /// Reuse `InferredType` from SchemaInference — already handles scalars,
      /// lists, and nested mappings, so a future Required-of-Mapping requirement
      /// (e.g. `metadata: { author: required-string }`) is expressible without
      /// a parallel type system.
      Type:     InferredType
      /// Applies to TString only. Values that are present but whitespace-only
      /// fail validation as `EmptyString`. Ignored for non-string types.
      NonEmpty: bool }

/// What "valid" means for a given collection. Each constructor describes a
/// different validation policy. Designed so Skill is just one constructor —
/// no special-case code paths in downstream consumers.
type FrontMatterSchema =
    /// No mandatory fields. Every key is optional. Used for arbitrary
    /// front-matter collections (blog posts, recipes, ADRs, ...).
    | General

    /// SKILL.md convention: `name` and `description` are required non-empty
    /// strings. Mathematically equivalent to:
    ///   Required [
    ///     { Key = YamlKey "name";        Type = TString; NonEmpty = true }
    ///     { Key = YamlKey "description"; Type = TString; NonEmpty = true } ]
    /// but kept as its own constructor so consumers can pattern-match on the
    /// *intent*, and so the typed accessors `.Name : SkillName` /
    /// `.Description : SkillDescription` are only synthesised by the type
    /// provider in this case.
    | Skill

    /// Strict Agent Skills specification validation.
    /// Only `name`, `description`, `license`, `compatibility`, `metadata`, `allowed-tools` permitted.
    /// Enforces name length (1-64), regex (`^[a-z0-9]+(-[a-z0-9]+)*$`), parent directory equality.
    /// Enforces description length (1-1024), compatibility length (1-500), string-only metadata map values.
    /// Optionally parses and validates embedded YAML under `embeddedMetadataKey`.
    | AgentSkill of embeddedMetadataKey: string option

    /// Arbitrary list of required fields. Forward-compatible with future
    /// JSON-Schema validation: every constraint expressible via JSON Schema's
    /// `required` plus a primitive `type` keyword reduces to this case.
    | Required of FieldRequirement list

// FUTURE — once we adopt a JSON Schema validator (JsonSchema.Net or similar):
//
//    /// Full JSON-Schema-driven validation. The path is loaded lazily into
//    /// a compiled validator the first time it's used.
//    | JsonSchemaFile of path: AbsoluteFilePath

// ---------------------------------------------------------------------------
// Validation outcomes
// ---------------------------------------------------------------------------

/// Per-field reason a front-matter file failed schema validation.
type ValidationFailure =
    /// A required field is absent from the file's front-matter map.
    | MissingField of YamlKey

    /// A required field is present but with the wrong YAML type (e.g. schema
    /// expected `TString`, file has `TBool`). The actual value is preserved
    /// for diagnostics — usually shown to the user as "expected string, got
    /// `true`".
    | WrongType of key: YamlKey * expected: InferredType * actual: YamlValue

    /// A required string field is present but empty / whitespace-only. Only
    /// raised when `FieldRequirement.NonEmpty = true`.
    | EmptyString of YamlKey

    /// An unexpected/unrecognised field is present (in strict modes such as agent-skill).
    | UnknownField of YamlKey

    /// A field value fails format, character set, length, or parent-directory matching constraints.
    | InvalidFormat of key: YamlKey * detail: string

    /// An embedded YAML metadata entry fails validation (empty, malformed YAML, non-mapping root).
    | InvalidEmbeddedMetadata of key: YamlKey * detail: string

// ---------------------------------------------------------------------------
// Built-in schema constants
// ---------------------------------------------------------------------------

/// The two requirements that define a SKILL.md file. Exposed as a public
/// constant so callers writing custom Required-schemas can compose with it
/// (e.g. "skill plus my own extra fields").
let skillRequirements : FieldRequirement list =
    [ { Key = YamlKey "name";        Type = TString; NonEmpty = true }
      { Key = YamlKey "description"; Type = TString; NonEmpty = true } ]

let private allowedAgentSkillKeys =
    Set.ofList [
        YamlKey "name"
        YamlKey "description"
        YamlKey "license"
        YamlKey "compatibility"
        YamlKey "metadata"
        YamlKey "allowed-tools"
    ]

let private isValidAgentSkillNameChars (s: string) : bool =
    s.Length > 0
    && not (s.StartsWith "-")
    && not (s.EndsWith "-")
    && not (s.Contains "--")
    && (s |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-'))

let private validateAgentSkill (embeddedKeyOpt: string option) (raw: RawFrontMatter) : ValidationFailure list =
    let failures = ResizeArray<ValidationFailure>()

    // 1. Unknown top-level fields
    for kv in raw.Fields do
        if not (Set.contains kv.Key allowedAgentSkillKeys) then
            failures.Add(UnknownField kv.Key)

    // 2. Name validation
    match Map.tryFind (YamlKey "name") raw.Fields with
    | None ->
        failures.Add(MissingField (YamlKey "name"))
    | Some (YString s) ->
        if String.IsNullOrWhiteSpace s then
            failures.Add(EmptyString (YamlKey "name"))
        else
            if s.Length < 1 || s.Length > 64 then
                failures.Add(InvalidFormat (YamlKey "name", sprintf "Name length %d is outside allowed range 1-64" s.Length))
            if not (isValidAgentSkillNameChars s) then
                failures.Add(InvalidFormat (YamlKey "name", "Name must contain only lowercase alphanumeric characters and single hyphens, with no leading or trailing hyphen and no consecutive hyphens"))
            let filePath = AbsoluteFilePath.value raw.Path
            let dirName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(filePath))
            if not (String.IsNullOrEmpty dirName) && s <> dirName then
                failures.Add(InvalidFormat (YamlKey "name", sprintf "Name '%s' does not match parent directory name '%s'" s dirName))
    | Some other ->
        failures.Add(WrongType (YamlKey "name", TString, other))

    // 3. Description validation
    match Map.tryFind (YamlKey "description") raw.Fields with
    | None ->
        failures.Add(MissingField (YamlKey "description"))
    | Some (YString s) ->
        if String.IsNullOrWhiteSpace s then
            failures.Add(EmptyString (YamlKey "description"))
        elif s.Length < 1 || s.Length > 1024 then
            failures.Add(InvalidFormat (YamlKey "description", sprintf "Description length %d is outside allowed range 1-1024" s.Length))
    | Some other ->
        failures.Add(WrongType (YamlKey "description", TString, other))

    // 4. License validation (optional)
    match Map.tryFind (YamlKey "license") raw.Fields with
    | Some (YString _) | None -> ()
    | Some other ->
        failures.Add(WrongType (YamlKey "license", TString, other))

    // 5. Compatibility validation (optional)
    match Map.tryFind (YamlKey "compatibility") raw.Fields with
    | None -> ()
    | Some (YString s) ->
        if s.Length < 1 || s.Length > 500 then
            failures.Add(InvalidFormat (YamlKey "compatibility", sprintf "Compatibility length %d is outside allowed range 1-500" s.Length))
    | Some other ->
        failures.Add(WrongType (YamlKey "compatibility", TString, other))

    // 6. Allowed-tools validation (optional)
    match Map.tryFind (YamlKey "allowed-tools") raw.Fields with
    | Some (YString _) | None -> ()
    | Some other ->
        failures.Add(WrongType (YamlKey "allowed-tools", TString, other))

    // 7. Metadata validation (optional)
    match Map.tryFind (YamlKey "metadata") raw.Fields with
    | None -> ()
    | Some (YMap entries) ->
        for kv in entries do
            let (YamlKey k) = kv.Key
            match kv.Value with
            | YString _ -> ()
            | nonString ->
                failures.Add(WrongType (YamlKey (sprintf "metadata.%s" k), TString, nonString))

        // 8. Embedded metadata key validation (if configured)
        match embeddedKeyOpt with
        | Some keyStr when not (String.IsNullOrWhiteSpace keyStr) ->
            let yamlKey = YamlKey keyStr
            match Map.tryFind yamlKey entries with
            | Some (YString yamlText) ->
                match parseEmbeddedYaml (Some raw.Path) yamlKey yamlText with
                | Ok _ -> ()
                | Error err ->
                    failures.Add(InvalidEmbeddedMetadata (yamlKey, err))
            | _ -> ()
        | _ -> ()
    | Some other ->
        failures.Add(WrongType (YamlKey "metadata", TMapping Map.empty, other))

    Seq.toList failures

// ---------------------------------------------------------------------------
// Type matching
// ---------------------------------------------------------------------------

/// Project a runtime YamlValue back into the InferredType it satisfies, so we
/// can compare against a FieldRequirement.Type. Mirrors SchemaInference's
/// inferNodeType but operates on already-typed YamlValue rather than raw obj.
let private classifyValue (v: YamlValue) : InferredType =
    let rec go v =
        match v with
        | YString _ -> TString
        | YBool   _ -> TBool
        | YInt    _ -> TInt
        | YFloat  _ -> TFloat
        | YList items ->
            if items.IsEmpty then TList TString
            else
                items
                |> List.map go
                |> List.reduce mergeTypes
                |> TList
        | YMap entries ->
            entries
            |> Map.toSeq
            |> Seq.map (fun (k, v) -> k, { Type = go v; PresentInAll = true })
            |> Map.ofSeq
            |> TMapping
    go v

/// Structural compatibility: does `actual` satisfy `expected`?
/// Strict equality for scalars; element-type compatibility for lists; field
/// subset + recursive compatibility for mappings.
let rec private isCompatible (expected: InferredType) (actual: InferredType) : bool =
    match expected, actual with
    | TString, TString | TBool, TBool | TInt, TInt | TFloat, TFloat -> true
    // Numeric widening on the actual side: schema says TFloat, file has TInt → OK.
    | TFloat, TInt -> true
    | TList ea, TList aa -> isCompatible ea aa
    | TMapping fe, TMapping fa ->
        fe |> Map.forall (fun k schema ->
            match Map.tryFind k fa with
            | Some actualSchema -> isCompatible schema.Type actualSchema.Type
            | None -> false)
    | _ -> false

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

/// Validate one parsed front-matter against a schema. Collects *all* failures
/// (not just the first) so a single audit run can surface every problem with
/// a file in one pass.
let validate (schema: FrontMatterSchema) (raw: RawFrontMatter) : Result<RawFrontMatter, ValidationFailure list> =
    match schema with
    | AgentSkill embeddedKeyOpt ->
        let failures = validateAgentSkill embeddedKeyOpt raw
        if failures.IsEmpty then Ok raw else Error failures
    | _ ->
        let requirements =
            match schema with
            | General      -> []
            | Skill        -> skillRequirements
            | AgentSkill _ -> []
            | Required rs  -> rs

        let failures =
            requirements
            |> List.collect (fun req ->
                match Map.tryFind req.Key raw.Fields with
                | None ->
                    [ MissingField req.Key ]
                | Some value ->
                    let actualType = classifyValue value
                    if not (isCompatible req.Type actualType) then
                        [ WrongType (req.Key, req.Type, value) ]
                    else
                        match req.Type, value, req.NonEmpty with
                        | TString, YString s, true when System.String.IsNullOrWhiteSpace s ->
                            [ EmptyString req.Key ]
                        | _ -> [])

        if failures.IsEmpty then Ok raw else Error failures

// ---------------------------------------------------------------------------
// Functional builders — pipe-style schema composition
//
// Each `requireXxx` is a curried `string -> FrontMatterSchema -> FrontMatterSchema`
// so it chains naturally through `|>`. The pipeline starts from any existing
// schema (`General`, `Skill`, or `Required _`) and accumulates additional
// requirements. The result is always a `Required` schema.
//
//     // Skill plus a custom required field:
//     let extendedSkill =
//         Skill |> requireString "origin"
//
//     // Full custom schema from scratch:
//     let recipe =
//         General
//         |> requireString     "title"
//         |> requireStringList "ingredients"
//         |> requireInt        "prepMinutes"
//         |> allowEmpty        "notes"            // override NonEmpty=true on a string
// ---------------------------------------------------------------------------

let private requirementsOf = function
    | General      -> []
    | Skill        -> skillRequirements
    | AgentSkill _ -> skillRequirements
    | Required rs  -> rs

let private addRequirement (req: FieldRequirement) (schema: FrontMatterSchema) : FrontMatterSchema =
    Required (requirementsOf schema @ [req])

/// Require a non-empty string field.
let requireString (key: string) : FrontMatterSchema -> FrontMatterSchema =
    addRequirement { Key = YamlKey key; Type = TString; NonEmpty = true }

/// Require an integer field.
let requireInt (key: string) : FrontMatterSchema -> FrontMatterSchema =
    addRequirement { Key = YamlKey key; Type = TInt; NonEmpty = false }

/// Require a float field.
let requireFloat (key: string) : FrontMatterSchema -> FrontMatterSchema =
    addRequirement { Key = YamlKey key; Type = TFloat; NonEmpty = false }

/// Require a boolean field.
let requireBool (key: string) : FrontMatterSchema -> FrontMatterSchema =
    addRequirement { Key = YamlKey key; Type = TBool; NonEmpty = false }

/// Require a list-of-strings field.
let requireStringList (key: string) : FrontMatterSchema -> FrontMatterSchema =
    addRequirement { Key = YamlKey key; Type = TList TString; NonEmpty = false }

/// Require a field with a fully-specified type and emptiness rule. Use this
/// for complex types (nested mappings, lists of lists) the typed `requireXxx`
/// helpers don't cover.
let require (req: FieldRequirement) : FrontMatterSchema -> FrontMatterSchema =
    addRequirement req

/// Override the `NonEmpty` flag on a previously-required string field. Useful
/// when a particular string is allowed to be empty even though `requireString`
/// defaults to non-empty.
let allowEmpty (key: string) (schema: FrontMatterSchema) : FrontMatterSchema =
    let yamlKey = YamlKey key
    Required (
        requirementsOf schema
        |> List.map (fun r ->
            if r.Key = yamlKey && r.Type = TString then { r with NonEmpty = false }
            else r))

// ---------------------------------------------------------------------------
// Validation-aware discovery
// ---------------------------------------------------------------------------

/// Synchronously discover the schema and optional extension schema across a directory,
/// filtering strictly for files that satisfy the given FrontMatterSchema validation rules.
/// Files that fail validation are excluded so rejected samples do not pollute Describe()
/// or the generated FrontMatterDefinition type.
let discoverValidatedSchemaWithStatsAndExtension
    (schema: FrontMatterSchema)
    (rootDir: string)
    (pattern: string)
    : DiscoveryReport * DiscoveryReport option =
    let embeddedKey =
        match schema with
        | AgentSkill (Some k) -> Some k
        | _ -> None

    let predicate (path: AbsoluteFilePath) (rawMap: Map<YamlKey, obj>) : bool =
        match schema with
        | General -> true
        | _ ->
            let typedFields = rawMap |> Map.map (fun _ v -> objToValue v)
            let raw = { Path = path; Fields = typedFields }
            match validate schema raw with
            | Ok _ -> true
            | Error _ -> false

    discoverSchemaWithFilterAndExtension rootDir pattern embeddedKey (Some predicate)

/// Synchronously discover the schema across a directory, filtering for files that satisfy
/// the given FrontMatterSchema validation rules.
let discoverValidatedSchema (schema: FrontMatterSchema) (rootDir: string) (pattern: string) : DiscoveredSchema =
    let report, _ = discoverValidatedSchemaWithStatsAndExtension schema rootDir pattern
    report.Schema
