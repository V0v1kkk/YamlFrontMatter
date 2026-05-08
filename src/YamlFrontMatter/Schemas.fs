module YamlFrontMatter.Schemas

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

// ---------------------------------------------------------------------------
// Built-in schema constants
// ---------------------------------------------------------------------------

/// The two requirements that define a SKILL.md file. Exposed as a public
/// constant so callers writing custom Required-schemas can compose with it
/// (e.g. "skill plus my own extra fields").
let skillRequirements : FieldRequirement list =
    [ { Key = YamlKey "name";        Type = TString; NonEmpty = true }
      { Key = YamlKey "description"; Type = TString; NonEmpty = true } ]

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
    let requirements =
        match schema with
        | General      -> []
        | Skill        -> skillRequirements
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
