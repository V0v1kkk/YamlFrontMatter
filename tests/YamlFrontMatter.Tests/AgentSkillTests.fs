module YamlFrontMatter.Tests.AgentSkillTests

open System.IO
open Xunit
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Schemas
open YamlFrontMatter.Scanner
open YamlFrontMatter

// ---------------------------------------------------------------------------
// Static Type Provider instantiation for Agent Skills
// ---------------------------------------------------------------------------

[<Literal>]
let AgentSkillFixturesDir = __SOURCE_DIRECTORY__ + "/AgentSkillFixtures"

type AgentSkillsProvider =
    FrontMatterProvider<
        RootDirectory = AgentSkillFixturesDir,
        Pattern = "SKILL.md",
        Mode = "agent-skill",
        EmbeddedMetadataKey = "dev.v-san.skills">

// Compile-time shape assertions
let private _nameType (s: AgentSkillsProvider.FrontMatterDefinition) : SkillName = s.Name
let private _descType (s: AgentSkillsProvider.FrontMatterDefinition) : SkillDescription = s.Description
let private _licenseType (s: AgentSkillsProvider.FrontMatterDefinition) : string option = s.License
let private _compatType (s: AgentSkillsProvider.FrontMatterDefinition) : string option = s.Compatibility
let private _allowedToolsType (s: AgentSkillsProvider.FrontMatterDefinition) : string option = s.AllowedTools
let private _metaType (s: AgentSkillsProvider.FrontMatterDefinition) : AgentSkillsProvider.FrontMatterDefinition.MetadataData option = s.Metadata
let private _extMetaType (s: AgentSkillsProvider.FrontMatterDefinition) : AgentSkillsProvider.FrontMatterDefinition.ExtensionMetadataData option = s.ExtensionMetadata

// ---------------------------------------------------------------------------
// Type Provider tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``AgentSkill TP GetAll returns all valid skills`` () =
    let all = AgentSkillsProvider.GetAll() |> Seq.toList
    Assert.Equal(4, all.Length)

[<Fact>]
let ``AgentSkill TP GetRejected returns 0 on valid fixtures`` () =
    let rejections = AgentSkillsProvider.GetRejected() |> Seq.toList
    Assert.Empty(rejections)

[<Fact>]
let ``AgentSkill TP GetSkipped returns 0 on valid fixtures`` () =
    let skipped = AgentSkillsProvider.GetSkipped() |> Seq.toList
    Assert.Empty(skipped)

[<Fact>]
let ``AgentSkill TP parses complex embedded metadata correctly`` () =
    let all = AgentSkillsProvider.GetAll() |> Seq.toList
    let validSkill = all |> List.find (fun s -> s.Name.Value = "valid-skill")
    
    Assert.Equal("valid-skill", validSkill.Name.Value)
    Assert.Equal("A fully valid skill adhering to agent-skills specification.", validSkill.Description.Value)
    Assert.Equal(Some "MIT", validSkill.License)
    Assert.Equal(Some "Requires Python 3.10+ and Linux", validSkill.Compatibility)
    Assert.Equal(Some "Read Bash(python:*)", validSkill.AllowedTools)

    // Outer metadata view
    Assert.True(validSkill.Metadata.IsSome)
    Assert.Equal(Some "Vladimir Rogozhin", validSkill.Metadata.Value.Author)

    // Embedded ExtensionMetadata view
    Assert.True(validSkill.ExtensionMetadata.IsSome)
    let ext = validSkill.ExtensionMetadata.Value
    Assert.Equal(Some "original (personal skill)", ext.Origin)
    Assert.Equal(Some "1.0", ext.Version)
    Assert.Equal(Some "2026-08-11", ext.ChangeDate)
    Assert.Equal(Some ["Vladimir Rogozhin"], ext.Authors)
    Assert.Equal(Some false, ext.PrivateSkill)
    Assert.Equal(Some ["fsharp"; "fsharp-core"], ext.SkillGroups)
    Assert.Equal(Some ["another-skill"], ext.DependsOnSkills)

    // Nested requires
    Assert.True(ext.Requires.IsSome)
    let reqs = ext.Requires.Value
    Assert.Equal(Some ["Python 3.10+"; "jq"], reqs.Dependencies)
    Assert.Equal(Some ["REQUIRED_TOKEN"], reqs.EnvironmentVariables)
    Assert.Equal(Some ["OPTIONAL_TOKEN"], reqs.OptionalEnvironmentVariables)
    Assert.Equal(Some ["linux"; "macos"], reqs.Platforms)

    // Nested book
    Assert.True(ext.Book.IsSome)
    let book = ext.Book.Value
    Assert.Equal(Some "Extensible Pattern Matching", book.Title)
    Assert.Equal(Some ["Don Syme"; "Gregory Neverov"], book.Authors)
    Assert.Equal(Some 2007, book.Year)
    Assert.Equal(Some "eng", book.Language)
    Assert.Equal(Some "9781595938152", book.Isbn)

    // Nested hermes
    Assert.True(ext.Hermes.IsSome)
    let hermes = ext.Hermes.Value
    Assert.Equal(Some "content", hermes.Category)
    Assert.Equal(Some ["x"; "posts"; "threads"], hermes.Tags)

    // Nested upstream
    Assert.True(ext.Upstream.IsSome)
    let upstream = ext.Upstream.Value
    Assert.Equal(Some "https://github.com/example/upstream", upstream.Repository)
    Assert.Equal(Some "skills/valid-skill", upstream.Path)
    Assert.Equal(Some "0123456789abcdef0123456789abcdef01234567", upstream.Commit)
    Assert.Equal(Some "1.2.3", upstream.Version)
    Assert.Equal(Some "2026-08-01", upstream.ChangeDate)

[<Fact>]
let ``AgentSkill TP parses diverse types and quotes in embedded metadata`` () =
    let all = AgentSkillsProvider.GetAll() |> Seq.toList
    let typesSkill = all |> List.find (fun s -> s.Name.Value = "types-agent-skill")

    Assert.True(typesSkill.ExtensionMetadata.IsSome)
    let ext = typesSkill.ExtensionMetadata.Value
    Assert.Equal(Some true, ext.BoolFlag)
    Assert.Equal(Some 42, ext.IntCount)
    Assert.Equal(Some 3.14, ext.FloatRate)
    Assert.Equal(Some "123", ext.QuotedInt)
    Assert.Equal(Some "true", ext.QuotedBool)
    Assert.Equal(Some "ФSharp 🚀", ext.UnicodeText)
    Assert.Equal(Some ["a"; "b"; "c"], ext.FlowList)
    Assert.Equal(Some ["alpha"; "beta"], ext.BlockList)

[<Fact>]
let ``AgentSkill TP Describe includes agent-skill mode and extension metadata type`` () =
    let desc = AgentSkillsProvider.Describe()
    Assert.Contains("// Mode: agent-skill", desc)
    Assert.Contains("type SkillDefinition = {", desc)
    Assert.Contains("and ExtensionMetadataData = {", desc)
    Assert.Contains("Name              : SkillName", desc)
    Assert.Contains("Description       : SkillDescription", desc)
    Assert.Contains("ExtensionMetadata : ExtensionMetadataData option", desc)

// ---------------------------------------------------------------------------
// Validation tests against invalid fixtures
// ---------------------------------------------------------------------------

let private scanInvalid () : FrontMatterRejection list =
    let rootDir = Path.Combine(__SOURCE_DIRECTORY__, "InvalidAgentSkillFixtures")
    RuntimeHelpers.GetRejected(rootDir, "SKILL.md", "agent-skill", "dev.v-san.skills")
    |> Seq.toList

[<Fact>]
let ``Invalid fixtures are all rejected with expected failure types`` () =
    let rejections = scanInvalid ()
    Assert.Equal(10, rejections.Length)

    let rejectionFor (name: string) =
        rejections
        |> List.find (fun r -> (AbsoluteFilePath.value r.Path).Contains(name))

    // 1. Mismatched name
    let r1 = rejectionFor "mismatched-name"
    Assert.Contains(r1.Failures, function InvalidFormat (YamlKey "name", d) when d.Contains("does not match parent directory") -> true | _ -> false)

    // 2. Uppercase in name
    let r2 = rejectionFor "bad-name-uppercase"
    Assert.Contains(r2.Failures, function InvalidFormat (YamlKey "name", _) -> true | _ -> false)

    // 3. Consecutive hyphens in name
    let r3 = rejectionFor "bad-name-consecutive-hyphen"
    Assert.Contains(r3.Failures, function InvalidFormat (YamlKey "name", _) -> true | _ -> false)

    // 4. Trailing hyphen in name
    let r4 = rejectionFor "bad-name-trailing-hyphen"
    Assert.Contains(r4.Failures, function InvalidFormat (YamlKey "name", _) -> true | _ -> false)

    // 5. Unknown top field
    let r5 = rejectionFor "unknown-top-field"
    Assert.Contains(r5.Failures, function UnknownField (YamlKey "custom-property") -> true | _ -> false)

    // 6. Non-string metadata value
    let r6 = rejectionFor "non-string-metadata-value"
    Assert.Contains(r6.Failures, function WrongType (YamlKey "metadata.invalid-int", TString, _) -> true | _ -> false)

    // 7. Malformed embedded YAML
    let r7 = rejectionFor "malformed-embedded-yaml"
    Assert.Contains(r7.Failures, function InvalidEmbeddedMetadata (YamlKey "dev.v-san.skills", _) -> true | _ -> false)

    // 8. Non-mapping embedded YAML
    let r8 = rejectionFor "non-mapping-embedded-yaml"
    Assert.Contains(r8.Failures, function InvalidEmbeddedMetadata (YamlKey "dev.v-san.skills", d) when d.Contains("must be a mapping") -> true | _ -> false)

    // 9. Empty embedded YAML
    let r9 = rejectionFor "empty-embedded-yaml"
    Assert.Contains(r9.Failures, function InvalidEmbeddedMetadata (YamlKey "dev.v-san.skills", d) when d.Contains("empty") -> true | _ -> false)

    // 10. Description too long
    let r10 = rejectionFor "desc-too-long"
    Assert.Contains(r10.Failures, function InvalidFormat (YamlKey "description", d) when d.Contains("outside allowed range 1-1024") -> true | _ -> false)
