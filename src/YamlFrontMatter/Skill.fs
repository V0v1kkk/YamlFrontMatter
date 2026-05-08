module YamlFrontMatter.Skill

open YamlFrontMatter.Types
open YamlFrontMatter.Schemas
open YamlFrontMatter.FrontMatterReader

// ---------------------------------------------------------------------------
// Skill is an *extension* over the generic front-matter library, not a
// parallel implementation. Internally:
//
//     tryReadSkillIdentity = tryRead Skill
//                            |> Result.mapError translateProblem
//                            |> Result.map projectIdentity
//
// Everything below is just type-narrowing to make the call site ergonomic for
// the most common skill-flavoured workflow ("did I just open a SKILL.md, and
// if so, what's its name?").
// ---------------------------------------------------------------------------

/// What we extract from a valid SKILL.md file. Description rides along because
/// callers who care about Name almost always want it too, and parsing it is
/// free at this point — `validate Skill` already checked it's present.
type SkillIdentity =
    { Path:        AbsoluteFilePath
      Name:        SkillName
      Description: SkillDescription }

/// Why a file failed to be recognised as a skill. Cases are a *narrowing*
/// projection of `ReadProblem`: the generic `ValidationFailed` case explodes
/// into `NameMissing` / `NameEmpty` / `DescriptionMissing` / `DescriptionEmpty`
/// since for the Skill schema those are the only validation failures possible.
/// `WrongType` cases are also possible (e.g. `name: 42` would parse as TInt)
/// and surface as `NameNotString` / `DescriptionNotString`.
type SkillReadProblem =
    /// File has no `---` front-matter region.
    | NoFrontMatter

    /// File starts with `---` but the closing `---` is missing.
    | UnclosedFrontMatter

    /// YAML inside the front matter is malformed.
    | YamlMalformed of detail: string

    /// `name:` key absent.
    | NameMissing

    /// `name:` key present but empty / whitespace-only.
    | NameEmpty

    /// `name:` is present but not a string scalar (e.g. `name: 42` or
    /// `name: [a, b]`). `actual` is preserved for diagnostics.
    | NameNotString of actual: YamlValue

    /// `description:` key absent.
    | DescriptionMissing

    /// `description:` key present but empty / whitespace-only.
    | DescriptionEmpty

    /// `description:` is present but not a string scalar.
    | DescriptionNotString of actual: YamlValue

    /// File system error.
    | IoFailure of message: string

// ---------------------------------------------------------------------------
// Translation: ReadProblem (generic) → SkillReadProblem (typed for Skill)
// ---------------------------------------------------------------------------

let private nameKey        = YamlKey "name"
let private descriptionKey = YamlKey "description"

let private translateValidationFailure (failure: ValidationFailure) : SkillReadProblem =
    match failure with
    | MissingField k when k = nameKey        -> NameMissing
    | MissingField k when k = descriptionKey -> DescriptionMissing
    | EmptyString  k when k = nameKey        -> NameEmpty
    | EmptyString  k when k = descriptionKey -> DescriptionEmpty
    | WrongType (k, _, actual) when k = nameKey
        -> NameNotString actual
    | WrongType (k, _, actual) when k = descriptionKey
        -> DescriptionNotString actual
    // Unreachable for the Skill schema — its only requirements are name and
    // description. If a future change adds new requirements without updating
    // this match, surface as IoFailure rather than crashing so callers still
    // get a Result.
    | other ->
        IoFailure (sprintf "Unexpected Skill-schema validation failure: %A" other)

let private translateProblem (problem: ReadProblem) : SkillReadProblem =
    match problem with
    | ReadProblem.NoFrontMatter         -> NoFrontMatter
    | ReadProblem.UnclosedFrontMatter   -> UnclosedFrontMatter
    | ReadProblem.YamlMalformed d       -> YamlMalformed d
    | ReadProblem.IoFailure m           -> IoFailure m
    | ReadProblem.ValidationFailed []   ->
        // validate returns Error [] only on a buggy schema definition.
        IoFailure "Skill schema validation produced an empty failure list (bug)"
    | ReadProblem.ValidationFailed (head :: _) ->
        // For SkillReadProblem we report the first failure encountered,
        // matching the "did I just open a skill?" question this API answers.
        // Callers that want the full failure set should use
        // `FrontMatterReader.tryRead Skill` directly.
        translateValidationFailure head

// ---------------------------------------------------------------------------
// Identity projection: validated RawFrontMatter → SkillIdentity
// ---------------------------------------------------------------------------

let private projectIdentity (raw: RawFrontMatter) : SkillIdentity =
    // Total: validate Skill has just established that both fields exist as
    // non-empty strings. If they don't, that's a library bug, not a runtime
    // condition we need to handle gracefully.
    let lookupString key =
        match Map.tryFind key raw.Fields with
        | Some (YString s) -> s
        | _ ->
            failwithf
                "Skill schema accepted %s but field %A is not a string. \
                 This is a YamlFrontMatter library bug."
                (AbsoluteFilePath.value raw.Path) key

    { Path        = raw.Path
      Name        = SkillName.createUnsafe (lookupString nameKey)
      Description = SkillDescription.createUnsafe (lookupString descriptionKey) }

// ---------------------------------------------------------------------------
// Public single-file API
// ---------------------------------------------------------------------------

/// Read a single file and try to identify it as a SKILL.md.
///
/// Returns `Ok identity` when the file has well-formed front matter with
/// non-empty `name` and `description` strings. Otherwise returns `Error`
/// describing exactly why — distinguishing between "no front matter" (this
/// isn't a skill at all), "broken yaml" (this looks like a corrupt skill),
/// and "missing/empty/wrong-typed required field" (this is meant to be a
/// skill but is malformed).
///
/// Internally this is a thin specialisation of `FrontMatterReader.tryRead`
/// over the `FrontMatterSchema.Skill` schema; nothing in this module is
/// skill-aware below the type level.
let tryReadSkillIdentity (path: AbsoluteFilePath) : Result<SkillIdentity, SkillReadProblem> =
    tryRead Schemas.Skill path
    |> Result.mapError translateProblem
    |> Result.map projectIdentity
