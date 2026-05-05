module FSharpExample.Program

open YamlFrontMatter
open YamlFrontMatter.Types

// ─────────────────────────────────────────────────────────────────────────────
// Point the Type Provider at our Skills directory.
// Everything below gets full IntelliSense: .Name, .Active, .Tags, .Metadata…
// ─────────────────────────────────────────────────────────────────────────────

[<Literal>]
let SkillsDir = __SOURCE_DIRECTORY__ + "/../Skills"

type Skills = FrontMatterProvider<SkillsDir>

// ─────────────────────────────────────────────────────────────────────────────
// Active patterns for expressive filtering
// ─────────────────────────────────────────────────────────────────────────────

let (|Active|Inactive|) (skill: Skills.FrontMatterDefinition) =
    match skill.Active with
    | Some true -> Active
    | _         -> Inactive

let (|HighPriority|_|) threshold (skill: Skills.FrontMatterDefinition) =
    match skill.Priority with
    | Some p when p >= threshold -> Some p
    | _ -> None

let (|Tagged|_|) tag (skill: Skills.FrontMatterDefinition) =
    match skill.Tags with
    | Some tags when List.contains tag tags -> Some tags
    | _ -> None

// ─────────────────────────────────────────────────────────────────────────────
// Demo
// ─────────────────────────────────────────────────────────────────────────────

printfn "=== YamlFrontMatter — F# Type Provider Demo ==="
printfn ""

// 1. Describe — show the inferred schema
printfn "── Inferred schema ──"
printfn "%s" (Skills.Describe())

// 2. Iterate all skills with typed properties
printfn "── All skills ──"
printfn ""

for skill in Skills.GetAll() do
    printfn "  %s — %s" skill.Name.Value skill.Description.Value
    skill.Version  |> Option.iter (printfn "    version: %s")
    skill.Active   |> Option.iter (printfn "    active: %b")
    skill.Priority |> Option.iter (printfn "    priority: %d")
    skill.Tags     |> Option.iter (fun tags -> printfn "    tags: [%s]" (String.concat ", " tags))

    match skill.Metadata with
    | Some meta ->
        printfn "    metadata:"
        meta.Author   |> Option.iter (printfn "      author: %s")
        meta.Revision |> Option.iter (printfn "      revision: %d")
    | None -> ()

    printfn ""

// 3. Filter with active patterns
printfn "── Active skills with priority >= 40 ──"
printfn ""

Skills.GetAll()
|> Seq.choose (fun skill ->
    match skill with
    | Active & (HighPriority 40 priority) -> Some (skill, priority)
    | _ -> None)
|> Seq.iter (fun (skill, priority) ->
    printfn "  %s (priority=%d)" skill.Name.Value priority)

printfn ""
printfn "── Skills tagged 'fsharp' ──"
printfn ""

Skills.GetAll()
|> Seq.choose (fun skill ->
    match skill with
    | Tagged "fsharp" tags -> Some (skill, tags)
    | _ -> None)
|> Seq.iter (fun (skill, tags) ->
    printfn "  %s — tags: [%s]" skill.Name.Value (String.concat ", " tags))

printfn ""
printfn "── Done! ──"
