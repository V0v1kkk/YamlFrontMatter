// count_by_category.fsx — group every entry in the collection by its
// first-level subfolder (e.g. development/, analysis/, ...) and print a
// roll-up. Useful for "how is my collection organised" overviews.

#r "nuget: YamlFrontMatter.TypeProvider"

open YamlFrontMatter
open YamlFrontMatter.Types

[<Literal>]
let Root = "/absolute/path/to/your/collection"

type Collection = FrontMatterProvider<Root, Mode = "skill">

let categoryOf (s: Collection.FrontMatterDefinition) =
    let path = AbsoluteFilePath.value s.Path
    let rel  = path.Substring(Root.Length).TrimStart('/')
    rel.Split('/').[0]

Collection.GetAll()
|> Seq.groupBy categoryOf
|> Seq.sortBy fst
|> Seq.iter (fun (category, items) ->
    printfn "%-22s %d items" category (Seq.length items)
    for s in items |> Seq.sortBy (fun s -> SkillName.value s.Name) do
        printfn "    - %s" (SkillName.value s.Name))
