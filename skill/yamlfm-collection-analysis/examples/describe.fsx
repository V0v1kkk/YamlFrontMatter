// describe.fsx — print the inferred schema for a collection of Markdown
// files with YAML front matter. Always run this *first* against an unfamiliar
// directory: it tells you what fields exist, how often each occurs, and which
// fields have nested structure.
//
// Substitute `Root` below with the absolute path to the collection.
// The path MUST be a `[<Literal>]` so the type provider can read it at
// compile time.

#r "nuget: YamlFrontMatter.TypeProvider"

open YamlFrontMatter

[<Literal>]
let Root = "/absolute/path/to/your/collection"

type Collection = FrontMatterProvider<Root, Mode = "skill">

printfn "%s" (Collection.Describe())
