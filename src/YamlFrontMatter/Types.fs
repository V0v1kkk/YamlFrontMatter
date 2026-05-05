module YamlFrontMatter.Types

open System

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

