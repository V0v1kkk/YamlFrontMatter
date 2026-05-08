module YamlFrontMatter.Tests.SchemaTests

open System.IO
open Xunit
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Schemas
open YamlFrontMatter.FrontMatterReader
open YamlFrontMatter.Skill

// ---------------------------------------------------------------------------
// Fixture helpers
//
// Each test that needs an on-disk SKILL.md writes its own temp file so we
// don't pollute Fixtures/ (which is shared with the TP-instantiation tests
// and changing it would silently change the TP-discovered schema).
// ---------------------------------------------------------------------------

let private writeTemp (content: string) : AbsoluteFilePath =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".md")
    File.WriteAllText(path, content)
    AbsoluteFilePath.createUnsafe path

let private cleanup (path: AbsoluteFilePath) =
    try File.Delete(AbsoluteFilePath.value path) with _ -> ()

let private validSkill =
    "---\n\
     name: my-skill\n\
     description: Does something useful.\n\
     ---\n\
     # Body\n"

let private nameOnly =
    "---\n\
     name: my-skill\n\
     ---\n"

let private descriptionOnly =
    "---\n\
     description: Just a description.\n\
     ---\n"

let private nameEmpty =
    "---\n\
     name: '   '\n\
     description: A description.\n\
     ---\n"

let private nameAsInt =
    "---\n\
     name: 42\n\
     description: A description.\n\
     ---\n"

let private noFrontMatter =
    "# Just a regular markdown file\n\
     No YAML metadata here.\n"

let private unclosedFrontMatter =
    "---\n\
     name: my-skill\n\
     description: Never closed\n"

let private malformedYaml =
    "---\n\
     name: my-skill\n\
     description: Broken\n\
     [unbalanced bracket: oops\n\
     ---\n"

// ===========================================================================
// validate — unit tests over RawFrontMatter values built directly
// ===========================================================================

let private rawWith (kvs: (string * YamlValue) list) : RawFrontMatter =
    { Path = AbsoluteFilePath.createUnsafe "/tmp/synthetic.md"
      Fields = kvs |> List.map (fun (k, v) -> YamlKey k, v) |> Map.ofList }

[<Fact>]
let ``General accepts every input`` () =
    let raw = rawWith []
    match validate General raw with
    | Ok _    -> ()
    | Error e -> failwithf "Expected Ok, got Error %A" e

[<Fact>]
let ``Skill accepts when name and description are non-empty strings`` () =
    let raw =
        rawWith [
            "name",        YString "my-skill"
            "description", YString "Does something useful."
        ]
    match validate Skill raw with
    | Ok _    -> ()
    | Error e -> failwithf "Expected Ok, got Error %A" e

[<Fact>]
let ``Skill rejects when name is missing`` () =
    let raw = rawWith [ "description", YString "ok" ]
    match validate Skill raw with
    | Error failures ->
        Assert.Contains(MissingField (YamlKey "name"), failures)
    | Ok _ ->
        Assert.Fail "Expected Error, got Ok"

[<Fact>]
let ``Skill rejects when name is whitespace-only`` () =
    let raw =
        rawWith [
            "name",        YString "   "
            "description", YString "ok"
        ]
    match validate Skill raw with
    | Error failures ->
        Assert.Contains(EmptyString (YamlKey "name"), failures)
    | Ok _ ->
        Assert.Fail "Expected Error, got Ok"

[<Fact>]
let ``Skill rejects when name is not a string`` () =
    let raw =
        rawWith [
            "name",        YInt 42
            "description", YString "ok"
        ]
    match validate Skill raw with
    | Error failures ->
        Assert.True(
            failures |> List.exists (function
                | WrongType (k, TString, YInt 42) when k = YamlKey "name" -> true
                | _ -> false))
    | Ok _ ->
        Assert.Fail "Expected Error, got Ok"

[<Fact>]
let ``Skill collects ALL failures, not just the first`` () =
    let raw = rawWith []   // missing both name and description
    match validate Skill raw with
    | Error failures ->
        // Both name and description should be reported missing in one pass.
        Assert.Equal(2, List.length failures)
        Assert.Contains(MissingField (YamlKey "name"),        failures)
        Assert.Contains(MissingField (YamlKey "description"), failures)
    | Ok _ ->
        Assert.Fail "Expected Error, got Ok"

[<Fact>]
let ``Required schema enforces caller-provided fields`` () =
    let mySchema =
        Required [
            { Key = YamlKey "version"; Type = TString; NonEmpty = true }
        ]
    let withVersion = rawWith [ "version", YString "1.2.3" ]
    let withoutVersion = rawWith [ "name", YString "x" ]

    match validate mySchema withVersion with
    | Ok _    -> ()
    | Error e -> failwithf "Expected Ok, got Error %A" e

    match validate mySchema withoutVersion with
    | Error failures ->
        Assert.Contains(MissingField (YamlKey "version"), failures)
    | Ok _ ->
        Assert.Fail "Expected Error, got Ok"

[<Fact>]
let ``skillRequirements constant is what Skill expands to`` () =
    // Hand-roll the same requirements via Required and check it behaves identically
    // against a missing-name input.
    let raw = rawWith [ "description", YString "ok" ]
    let viaSkill    = validate Skill raw
    let viaRequired = validate (Required skillRequirements) raw
    Assert.Equal(viaSkill, viaRequired)

// ===========================================================================
// tryRead — file-level reads with schema validation
// ===========================================================================

[<Fact>]
let ``tryRead General succeeds on a SKILL.md`` () =
    let path = writeTemp validSkill
    try
        match tryRead General path with
        | Ok raw ->
            Assert.Equal(2, raw.Fields.Count)
        | Error p ->
            Assert.Fail($"Expected Ok, got Error %A{p}")
    finally cleanup path

[<Fact>]
let ``tryRead Skill succeeds on a valid SKILL.md`` () =
    let path = writeTemp validSkill
    try
        match tryRead Skill path with
        | Ok raw ->
            Assert.True(raw.Fields.ContainsKey(YamlKey "name"))
        | Error p ->
            Assert.Fail($"Expected Ok, got Error %A{p}")
    finally cleanup path

[<Fact>]
let ``tryRead Skill rejects file missing description`` () =
    let path = writeTemp nameOnly
    try
        match tryRead Skill path with
        | Error (ValidationFailed failures) ->
            Assert.Contains(MissingField (YamlKey "description"), failures)
        | other ->
            Assert.Fail($"Expected ValidationFailed, got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryRead General accepts file missing description`` () =
    // Same file the Skill schema rejects — General is permissive.
    let path = writeTemp nameOnly
    try
        match tryRead General path with
        | Ok _    -> ()
        | Error p -> Assert.Fail($"Expected Ok in General mode, got %A{p}")
    finally cleanup path

[<Fact>]
let ``tryRead reports NoFrontMatter for a plain Markdown file`` () =
    let path = writeTemp noFrontMatter
    try
        match tryRead General path with
        | Error ReadProblem.NoFrontMatter -> ()
        | other -> Assert.Fail($"Expected NoFrontMatter, got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryRead reports UnclosedFrontMatter`` () =
    let path = writeTemp unclosedFrontMatter
    try
        match tryRead General path with
        | Error ReadProblem.UnclosedFrontMatter -> ()
        | other -> Assert.Fail($"Expected UnclosedFrontMatter, got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryRead reports YamlMalformed for broken yaml`` () =
    let path = writeTemp malformedYaml
    try
        match tryRead General path with
        | Error (ReadProblem.YamlMalformed _) -> ()
        | other -> Assert.Fail($"Expected YamlMalformed, got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryRead reports IoFailure for nonexistent file`` () =
    let bogus = AbsoluteFilePath.createUnsafe "/nonexistent/path/that/does/not/exist.md"
    match tryRead General bogus with
    | Error (ReadProblem.IoFailure _) -> ()
    | other -> Assert.Fail($"Expected IoFailure, got %A{other}")

// ===========================================================================
// tryReadSkillIdentity — the Skill-extension API
// ===========================================================================

[<Fact>]
let ``tryReadSkillIdentity returns name and description from a valid skill`` () =
    let path = writeTemp validSkill
    try
        match tryReadSkillIdentity path with
        | Ok identity ->
            Assert.Equal("my-skill", SkillName.value identity.Name)
            Assert.Equal("Does something useful.", SkillDescription.value identity.Description)
        | Error p ->
            Assert.Fail($"Expected Ok, got Error %A{p}")
    finally cleanup path

[<Fact>]
let ``tryReadSkillIdentity returns NoFrontMatter for a plain Markdown file`` () =
    let path = writeTemp noFrontMatter
    try
        match tryReadSkillIdentity path with
        | Error SkillReadProblem.NoFrontMatter -> ()
        | other -> Assert.Fail($"Expected NoFrontMatter, got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryReadSkillIdentity returns NameMissing when only description present`` () =
    let path = writeTemp descriptionOnly
    try
        match tryReadSkillIdentity path with
        | Error NameMissing -> ()
        | other -> Assert.Fail($"Expected NameMissing, got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryReadSkillIdentity returns DescriptionMissing when only name present`` () =
    let path = writeTemp nameOnly
    try
        match tryReadSkillIdentity path with
        | Error DescriptionMissing -> ()
        | other -> Assert.Fail($"Expected DescriptionMissing, got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryReadSkillIdentity returns NameEmpty for whitespace-only name`` () =
    let path = writeTemp nameEmpty
    try
        match tryReadSkillIdentity path with
        | Error NameEmpty -> ()
        | other -> Assert.Fail($"Expected NameEmpty, got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryReadSkillIdentity returns NameNotString when name is a number`` () =
    let path = writeTemp nameAsInt
    try
        match tryReadSkillIdentity path with
        | Error (NameNotString (YInt 42)) -> ()
        | other -> Assert.Fail($"Expected NameNotString (YInt 42), got %A{other}")
    finally cleanup path

[<Fact>]
let ``tryReadSkillIdentity carries the source path`` () =
    let path = writeTemp validSkill
    try
        match tryReadSkillIdentity path with
        | Ok identity ->
            Assert.Equal(AbsoluteFilePath.value path, AbsoluteFilePath.value identity.Path)
        | Error p ->
            Assert.Fail($"Expected Ok, got %A{p}")
    finally cleanup path

[<Fact>]
let ``tryReadSkillIdentity composes via Result.map for "just the name"`` () =
    // The composition the docs in Skill.fs claim works:  Result.map (fun id -> id.Name)
    let path = writeTemp validSkill
    try
        let nameOnlyResult = tryReadSkillIdentity path |> Result.map (fun id -> id.Name)
        match nameOnlyResult with
        | Ok name -> Assert.Equal("my-skill", SkillName.value name)
        | Error p -> Assert.Fail($"Expected Ok, got %A{p}")
    finally cleanup path

// ===========================================================================
// Pipe-style schema builders — composing schemas via |>
// ===========================================================================

[<Fact>]
let ``requireString adds a non-empty string requirement`` () =
    let s = General |> requireString "title"
    let raw = rawWith [ "title", YString "ok" ]
    match validate s raw with
    | Ok _ -> ()
    | Error e -> Assert.Fail($"Expected Ok, got %A{e}")

[<Fact>]
let ``requireString rejects when missing`` () =
    let s = General |> requireString "title"
    let raw = rawWith []
    match validate s raw with
    | Error [ MissingField (YamlKey "title") ] -> ()
    | other -> Assert.Fail($"Expected MissingField title, got %A{other}")

[<Fact>]
let ``requireString rejects empty string by default`` () =
    let s = General |> requireString "title"
    let raw = rawWith [ "title", YString "   " ]
    match validate s raw with
    | Error [ EmptyString (YamlKey "title") ] -> ()
    | other -> Assert.Fail($"Expected EmptyString title, got %A{other}")

[<Fact>]
let ``Skill |> requireString origin extends Skill with extra field`` () =
    let s =
        Skill
        |> requireString "origin"
    let withAll =
        rawWith [
            "name",        YString "my-skill"
            "description", YString "ok"
            "origin",      YString "https://example.com"
        ]
    match validate s withAll with
    | Ok _ -> ()
    | Error e -> Assert.Fail($"Expected Ok, got %A{e}")

[<Fact>]
let ``Skill |> requireString origin still requires Skill's own fields`` () =
    let s = Skill |> requireString "origin"
    let withoutSkillFields = rawWith [ "origin", YString "https://example.com" ]
    match validate s withoutSkillFields with
    | Error failures ->
        Assert.Contains(MissingField (YamlKey "name"),        failures)
        Assert.Contains(MissingField (YamlKey "description"), failures)
    | Ok _ ->
        Assert.Fail "Expected Error, got Ok"

[<Fact>]
let ``Pipeline composes multiple typed requirements`` () =
    let s =
        General
        |> requireString     "title"
        |> requireStringList "tags"
        |> requireInt        "priority"
        |> requireBool       "active"
    let raw =
        rawWith [
            "title",    YString "T"
            "tags",     YList [ YString "a"; YString "b" ]
            "priority", YInt 3
            "active",   YBool true
        ]
    match validate s raw with
    | Ok _ -> ()
    | Error e -> Assert.Fail($"Expected Ok, got %A{e}")

[<Fact>]
let ``allowEmpty downgrades a previously non-empty string requirement`` () =
    let strict   = General |> requireString "notes"
    let relaxed  = strict  |> allowEmpty    "notes"
    let raw      = rawWith [ "notes", YString "" ]

    // Strict rejects the empty string
    match validate strict raw with
    | Error [ EmptyString (YamlKey "notes") ] -> ()
    | other -> Assert.Fail($"Expected EmptyString rejection, got %A{other}")

    // Relaxed accepts it (still requires the field to be present, just allows empty)
    match validate relaxed raw with
    | Ok _ -> ()
    | Error e -> Assert.Fail($"Expected Ok after allowEmpty, got %A{e}")

[<Fact>]
let ``require with explicit FieldRequirement composes too`` () =
    let custom : FieldRequirement =
        { Key = YamlKey "level"; Type = TInt; NonEmpty = false }
    let s = General |> require custom
    let raw = rawWith [ "level", YInt 7 ]
    match validate s raw with
    | Ok _ -> ()
    | Error e -> Assert.Fail($"Expected Ok, got %A{e}")

// ===========================================================================
// formatSchemaForMode — text differs in skill vs general mode
// ===========================================================================

[<Fact>]
let ``formatSchemaForMode skill emits typed Name and Description`` () =
    let report =
        { Schema =
            Map.ofList [
                YamlKey "name",        { Type = TString; PresentInAll = true }
                YamlKey "description", { Type = TString; PresentInAll = true }
            ]
          FilesScanned = 3
          FieldOccurrences =
            Map.ofList [
                YamlKey "name",        3
                YamlKey "description", 3
            ] }
    let txt = formatSchemaForMode true report
    Assert.Contains("Name",        txt)
    Assert.Contains("SkillName",   txt)
    Assert.Contains("required (SKILL.md convention)", txt)

[<Fact>]
let ``formatSchemaForMode general does not emit SkillName specials`` () =
    let report =
        { Schema =
            Map.ofList [
                YamlKey "name",        { Type = TString; PresentInAll = true }
                YamlKey "description", { Type = TString; PresentInAll = true }
            ]
          FilesScanned = 3
          FieldOccurrences =
            Map.ofList [
                YamlKey "name",        3
                YamlKey "description", 3
            ] }
    let txt = formatSchemaForMode false report
    // In general mode, name/description appear as ordinary `string option`.
    Assert.DoesNotContain("SkillName",         txt)
    Assert.DoesNotContain("SkillDescription",  txt)
    Assert.DoesNotContain("required (SKILL.md convention)", txt)
    Assert.Contains("Name",        txt)   // they still appear as fields
    Assert.Contains("Description", txt)
    Assert.Contains("string option", txt)

[<Fact>]
let ``formatSchema field padding scales with longest name`` () =
    let report =
        { Schema =
            Map.ofList [
                YamlKey "version",                       { Type = TString; PresentInAll = false }
                YamlKey "requires-environment-variables", { Type = TList TString; PresentInAll = false }
            ]
          FilesScanned = 5
          FieldOccurrences =
            Map.ofList [
                YamlKey "version",                       3
                YamlKey "requires-environment-variables", 1
            ] }
    let txt = formatSchemaForMode false report
    // Long name should NOT have its colon glued directly to the name; there
    // must be at least one space before the colon. Catches the regression
    // where PadRight(16) didn't expand for longer names.
    let lines = txt.Split('\n')
    let longLine =
        lines
        |> Array.tryFind (fun l -> l.Contains "RequiresEnvironmentVariables")
    match longLine with
    | None -> Assert.Fail "No line with RequiresEnvironmentVariables in formatted output"
    | Some line ->
        Assert.Contains("RequiresEnvironmentVariables :", line)   // space before colon

