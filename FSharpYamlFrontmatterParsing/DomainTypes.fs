module FSharpYamlFrontmatterParsing.DomainTypes

open System.IO
open FSharpYamlFrontmatterParsing.SkillFrontMatter

type FilePath = AbsolutePath of Path

type SkillDefinition =
    { Path: FilePath
      FrontMatter: SkillFrontMatter }
    
// provider should return seq of SkillDefinition
// SkillDefinition should be a generated type with any possible front matter keys as properties, and the provider should fill those properties based on the front matter content
// There will be required properties such as Name and Description. All other will be optional (in terms of F# types) and depend on the content of the front matter. The provider should be able to handle any front matter keys and values, and map them to the corresponding properties in the SkillDefinition type.