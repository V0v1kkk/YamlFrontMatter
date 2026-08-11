module YamlFrontMatter.Types

open System
open System.Collections

[<Struct>]
type AbsoluteFilePath = private AbsoluteFilePath of string with
    member this.Value = let (AbsoluteFilePath v) = this in v

module AbsoluteFilePath =
    let value (AbsoluteFilePath v) = v

    let create (s: string) =
        if System.IO.Path.IsPathRooted s then Ok(AbsoluteFilePath s)
        else Error $"Path must be absolute: '%s{s}'"

    let createUnsafe (s: string) = AbsoluteFilePath s

[<Struct>]
type SkillName = private SkillName of string with
    member this.Value = let (SkillName v) = this in v

module SkillName =
    let value (SkillName v) = v

    let create (s: string) =
        if String.IsNullOrWhiteSpace s then Error "SkillName must not be empty"
        else Ok(SkillName(s.Trim()))

    let createUnsafe (s: string) = SkillName s

[<Struct>]
type SkillDescription = private SkillDescription of string with
    member this.Value = let (SkillDescription v) = this in v

module SkillDescription =
    let value (SkillDescription v) = v

    let create (s: string) =
        if String.IsNullOrWhiteSpace s then Error "SkillDescription must not be empty"
        else Ok(SkillDescription(s.Trim()))

    let createUnsafe (s: string) = SkillDescription s

/// A YAML front matter key (raw string, case-preserved)
[<Struct>]
type YamlKey = YamlKey of string with
    member this.Value = let (YamlKey v) = this in v

module YamlKey =
    let value (YamlKey v) = v
    let create (s: string) = YamlKey s

// ---------------------------------------------------------------------------
// Runtime-typed YAML value
//
// What VYaml's `Deserialize<obj>` returns gets normalised into this DU once
// at parse time, so downstream code (TP runtime helpers, Skill projection,
// validation) pattern-matches on a clean F# shape instead of poking `obj`.
// Lives here in Types.fs because RawFrontMatter (right below) carries it,
// and several modules (SchemaInference, Scanner, Schemas, Skill) all need
// to pattern-match on it.
// ---------------------------------------------------------------------------

type YamlValue =
    | YString of string
    | YBool   of bool
    | YInt    of int
    | YFloat  of float
    | YList   of YamlValue list
    | YMap    of Map<YamlKey, YamlValue>

let private toYamlKey (k: obj) : YamlKey =
    match k with
    | :? string as s -> YamlKey s
    | other          -> YamlKey (string other)

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
                match entry with
                | :? DictionaryEntry as de -> toYamlKey de.Key, objToValue de.Value
                | :? System.Collections.Generic.KeyValuePair<obj, obj> as kvp -> toYamlKey kvp.Key, objToValue kvp.Value
                | other -> toYamlKey other, YString "" ]
            |> Map.ofList
        YMap entries
    | :? IList as list ->
        [ for x in list -> objToValue x ]
        |> YList
    | other -> YString (string other)

// ---------------------------------------------------------------------------
// Parsed front-matter for a single file
//
// The raw, schema-agnostic result of reading one file's YAML front matter.
// Validation, projection to typed identities (SkillIdentity), and TP property
// access all build on top of this.
// ---------------------------------------------------------------------------

type RawFrontMatter =
    { Path:   AbsoluteFilePath
      Fields: Map<YamlKey, YamlValue> }

