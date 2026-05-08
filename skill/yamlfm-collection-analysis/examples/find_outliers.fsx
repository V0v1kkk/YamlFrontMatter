// find_outliers.fsx — surface data-quality issues across the collection.
//
// Heuristics:
//   1. Optional fields present in a tiny minority (≤2 files) are listed by
//      name — likely typos or one-off conventions worth review.
//   2. If two semantically-similar field names both exist (e.g. `origin` vs
//      `source`), the rarer is highlighted as a likely inconsistency.
//   3. Fields present in 75%+ of files but missing from a few are flagged as
//      "common but not universal" — those few are the outliers.

#r "nuget: YamlFrontMatter.TypeProvider"

open YamlFrontMatter
open YamlFrontMatter.Types

[<Literal>]
let Root = "/absolute/path/to/your/collection"

type Collection = FrontMatterProvider<Root, Mode = "skill">

let all = Collection.GetAll() |> Seq.toList
let n   = List.length all

printfn "Scanned %d files in %s\n" n Root

let withField label (filtered: Collection.FrontMatterDefinition list) =
    let count = List.length filtered
    let pct   = float count / float n * 100.0
    printfn "  %-15s %d/%d (%.0f%%)" label count n pct
    if count > 0 && count <= 2 then
        for s in filtered do
            printfn "      └─ %s" (SkillName.value s.Name)

// --- Edit the predicates below to match the field shape your `describe.fsx`
//     showed. The current set is appropriate for a SKILL.md collection. ---
printfn "Field presence (optionals only):"
withField "version"       (all |> List.filter (fun s -> s.Version.IsSome))
withField "origin"        (all |> List.filter (fun s -> s.Origin.IsSome))
withField "source"        (all |> List.filter (fun s -> s.Source.IsSome))
withField "metadata"      (all |> List.filter (fun s -> s.Metadata.IsSome))

// --- 'origin' vs 'source' duplicate concept ---
let originCount = all |> List.filter (fun s -> s.Origin.IsSome) |> List.length
let sourceCount = all |> List.filter (fun s -> s.Source.IsSome) |> List.length
if originCount > 0 && sourceCount > 0 then
    printfn "\nSuspicious near-duplicates:"
    printfn "  Both 'origin' (%d) and 'source' (%d) present — likely the same concept named two ways." originCount sourceCount
    for s in all do
        match s.Source with
        | Some src ->
            printfn "      • %s   →   source: %s" (SkillName.value s.Name) src
        | None -> ()

// --- Skills missing the common 'origin' field ---
if originCount > n * 3 / 4 then
    let missing = all |> List.filter (fun s -> s.Origin.IsNone && s.Source.IsNone)
    if not missing.IsEmpty then
        printfn "\nSkills missing 'origin' (it's present in %d/%d others):" originCount n
        for s in missing do
            printfn "      • %s" (SkillName.value s.Name)
