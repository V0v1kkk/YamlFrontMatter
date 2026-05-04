module SkillTypeProvider.Tests.Tests

open System.IO
open Xunit
open SkillFrontMatter.Core.Types
open SkillTypeProvider.SkillCollectionProvider

// ---------------------------------------------------------------------------
// Instantiate the TP against our fixture directory.
// The path is resolved relative to the compiled output, so we use __SOURCE_DIRECTORY__.
// ---------------------------------------------------------------------------

[<Literal>]
let FixturesDir = __SOURCE_DIRECTORY__ + "/Fixtures"

type Fixtures = SkillTypeProvider.SkillCollectionProvider<FixturesDir>

// ---------------------------------------------------------------------------
// Compile-time shape tests (type annotations = compile errors if schema is wrong)
// ---------------------------------------------------------------------------

let _nameIsSkillName        (s: Fixtures.SkillDefinition) : SkillName        = s.Name
let _descIsSkillDescription (s: Fixtures.SkillDefinition) : SkillDescription  = s.Description
let _pathIsAbsoluteFilePath (s: Fixtures.SkillDefinition) : AbsoluteFilePath  = s.Path
let _versionIsStringOpt     (s: Fixtures.SkillDefinition) : string option     = s.Version
let _triggerIsStringOpt     (s: Fixtures.SkillDefinition) : string option     = s.Trigger
let _originIsStringOpt      (s: Fixtures.SkillDefinition) : string option     = s.Origin
let _activeIsBoolOpt        (s: Fixtures.SkillDefinition) : bool option       = s.Active
let _priorityIsIntOpt       (s: Fixtures.SkillDefinition) : int option        = s.Priority
let _tagsIsStringListOpt    (s: Fixtures.SkillDefinition) : string list option = s.Tags
let _metaIsNestedTypeOpt    (s: Fixtures.SkillDefinition) : Fixtures.SkillDefinition.MetadataData option = s.Metadata
let _metaAuthorIsStringOpt  (m: Fixtures.SkillDefinition.MetadataData) : string option = m.Author
let _metaRevisionIsIntOpt   (m: Fixtures.SkillDefinition.MetadataData) : int option    = m.Revision

// ---------------------------------------------------------------------------
// Runtime tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``GetAll returns exactly 3 skill definitions`` () =
    let skills = Fixtures.GetAll() |> Seq.toList
    Assert.Equal(3, skills.Length)

[<Fact>]
let ``all skills have absolute file paths`` () =
    for skill in Fixtures.GetAll() do
        let p = AbsoluteFilePath.value skill.Path
        Assert.True(Path.IsPathRooted p, $"Expected rooted path, got: {p}")

[<Fact>]
let ``minimal skill has required fields and no optional fields`` () =
    let s =
        Fixtures.GetAll()
        |> Seq.find (fun s -> s.Name = SkillName.createUnsafe "minimal-skill")
    Assert.Equal(SkillName.createUnsafe "minimal-skill", s.Name)
    Assert.Equal(SkillDescription.createUnsafe "A minimal skill with only the required fields.", s.Description)
    Assert.Equal(None, s.Version)
    Assert.Equal(None, s.Trigger)
    Assert.Equal(None, s.Origin)
    Assert.Equal(None, s.Active)
    Assert.Equal(None, s.Priority)
    Assert.Equal(None, s.Tags)
    Assert.Equal(None, s.Metadata)

[<Fact>]
let ``rich skill has string optional fields`` () =
    let s =
        Fixtures.GetAll()
        |> Seq.find (fun s -> s.Name = SkillName.createUnsafe "rich-skill")
    Assert.Equal(Some "2.0",                  s.Version)
    Assert.Equal(Some "explicit",             s.Trigger)
    Assert.Equal(Some "https://example.com",  s.Origin)
    Assert.Equal(None, s.Active)
    Assert.Equal(None, s.Priority)

[<Fact>]
let ``complex skill deserializes bool, int, list, and nested mapping`` () =
    let s =
        Fixtures.GetAll()
        |> Seq.find (fun s -> s.Name = SkillName.createUnsafe "complex-skill")
    Assert.Equal(Some true, s.Active)
    Assert.Equal(Some 42,   s.Priority)
    Assert.Equal(Some ["fsharp"; "dotnet"; "type-providers"], s.Tags)

    let meta = s.Metadata |> Option.defaultWith (fun () -> failwith "Expected Some metadata")
    Assert.Equal(Some "Vladimir", meta.Author)
    Assert.Equal(Some 3,           meta.Revision)

[<Fact>]
let ``Name is a SkillName not a string`` () =
    // If this line compiles, the type is correct.
    // If Name were string, the assignment to SkillName would fail.
    let _: SkillName =
        Fixtures.GetAll() |> Seq.head |> fun s -> s.Name
    Assert.True(true)

[<Fact>]
let ``Description is a SkillDescription not a string`` () =
    let _: SkillDescription =
        Fixtures.GetAll() |> Seq.head |> fun s -> s.Description
    Assert.True(true)
