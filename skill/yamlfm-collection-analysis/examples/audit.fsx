// audit.fsx — one-shot audit report for an arbitrary YAML-frontmatter
// collection. The first script to run against an unfamiliar directory: it
// surfaces structure, hot/cold fields, anomalies, and category breakdown —
// enough to plan further targeted scripts.
//
// Sections produced:
//   1. Inferred schema (via Describe())
//   2. Skills per top-level subfolder
//   3. Field-saturation chart with ASCII bars
//   4. "Common but not universal" anomalies
//   5. Versioning style consistency check
//   6. Authorship distribution (from metadata.author)
//   7. Origin / source URL host distribution

#r "nuget: YamlFrontMatter.TypeProvider"

open System
open YamlFrontMatter
open YamlFrontMatter.Types

[<Literal>]
let Root = "/absolute/path/to/your/collection"

type Collection = FrontMatterProvider<Root, Mode = "skill">

let all = Collection.GetAll() |> Seq.toList
let n   = List.length all

let categoryOf (s: Collection.FrontMatterDefinition) =
    let p = AbsoluteFilePath.value s.Path
    let rel = p.Substring(Root.Length).TrimStart('/')
    rel.Split('/').[0]

let nameOf (s: Collection.FrontMatterDefinition) = SkillName.value s.Name

// ============================================================================
printfn "================================================================"
printfn " YAML Front Matter Collection Audit"
printfn " Root: %s" Root
printfn " Files: %d" n
printfn "================================================================"

// --- 1. Schema -----------------------------------------------------------
printfn "\n## Inferred schema\n"
printfn "%s" (Collection.Describe())

// --- 2. Categories -------------------------------------------------------
printfn "\n## Categories\n"
all
|> List.groupBy categoryOf
|> List.sortBy fst
|> List.iter (fun (cat, items) ->
    printfn "  %-22s %2d items" cat (List.length items))

// --- 3. Field saturation -------------------------------------------------
let saturation label (predicate: Collection.FrontMatterDefinition -> bool) =
    let yes = all |> List.filter predicate
    let count = List.length yes
    let pct = float count / float n * 100.0
    let bar = String.replicate (int (pct / 5.0)) "█"
    printfn "  %-15s %2d/%d  %5.1f%%  %s" label count n pct bar
    yes

printfn "\n## Field saturation\n"
let _ = saturation "version"      (fun s -> s.Version.IsSome)
let originSkills = saturation "origin"       (fun s -> s.Origin.IsSome)
let sourceSkills = saturation "source"       (fun s -> s.Source.IsSome)
let _ = saturation "metadata"     (fun s -> s.Metadata.IsSome)

// --- 4. Anomalies --------------------------------------------------------
printfn "\n## Anomalies (fields common but not universal)\n"

let originCount = originSkills.Length
if originCount > n * 3 / 4 then
    let missing = all |> List.filter (fun s -> s.Origin.IsNone && s.Source.IsNone)
    printfn "  'origin' is in %d/%d (%d%%) — these are missing it:"
        originCount n (originCount * 100 / n)
    for s in missing do
        printfn "      • %s   (%s)" (nameOf s) (categoryOf s)

if originSkills.Length > 0 && sourceSkills.Length > 0 then
    printfn "\n  Both 'origin' (%d) and 'source' (%d) used — same concept under two names:"
        originSkills.Length sourceSkills.Length
    for s in sourceSkills do
        match s.Source with
        | Some src -> printfn "      • %s uses 'source: %s'" (nameOf s) src
        | None -> ()

// --- 5. Versioning style -------------------------------------------------
printfn "\n## Versioning style\n"
let versioned =
    all
    |> List.choose (fun s -> s.Version |> Option.map (fun v -> nameOf s, v))
if versioned.IsEmpty then
    printfn "  (no entries declare a version)"
else
    versioned
    |> List.sortBy fst
    |> List.iter (fun (name, v) -> printfn "  %-30s %s" name v)
    let semverLike =
        versioned
        |> List.filter (fun (_, v) -> v.Split('.').Length >= 2)
        |> List.length
    printfn "  → %d/%d versioned entries look semver-ish (X.Y or X.Y.Z)" semverLike versioned.Length

// --- 6. Authorship -------------------------------------------------------
printfn "\n## Authorship (from metadata.author)\n"
all
|> List.choose (fun s ->
    s.Metadata |> Option.bind (fun m -> m.Author |> Option.map (fun a -> nameOf s, a)))
|> List.groupBy snd
|> List.sortByDescending (fun (_, items) -> List.length items)
|> List.iter (fun (author, items) ->
    printfn "  %-25s %d entry(ies): %s"
        author
        (List.length items)
        (items |> List.map fst |> String.concat ", "))

// --- 7. Origin URL hosts -------------------------------------------------
printfn "\n## Origin / source URL hosts\n"
let hostOf (url: string) =
    try Uri(url).Host with _ -> "(non-URL)"
all
|> List.choose (fun s ->
    match s.Origin, s.Source with
    | Some o, _      -> Some (nameOf s, hostOf o)
    | None, Some src -> Some (nameOf s, hostOf src)
    | None, None     -> None)
|> List.groupBy snd
|> List.sortByDescending (fun (_, items) -> List.length items)
|> List.iter (fun (host, items) ->
    printfn "  %-35s %2d  (%s)"
        host
        (List.length items)
        (items |> List.map fst |> String.concat ", "))

printfn "\n================================================================"
