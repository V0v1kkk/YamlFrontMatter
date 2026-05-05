module YamlFrontMatter.TypeProvider.DesignTime.Provider

open System.IO
open System.Reflection
open FSharp.Core.CompilerServices
open ProviderImplementation.ProvidedTypes
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Scanner
open YamlFrontMatter

let rec private generateMappingType
    (typeName: string)
    (fields: Map<YamlKey, FieldSchema>)
    : ProvidedTypeDefinition =

    let provided = ProvidedTypeDefinition(typeName, Some typeof<Map<YamlKey, YamlValue>>, isErased = true)

    for kv in fields do
        let yamlKey = kv.Key
        let schema  = kv.Value
        let (YamlKey rawKey) = yamlKey
        let propName = toPascalCase rawKey

        let prop =
            match schema.Type with
            | TBool ->
                ProvidedProperty(propName, typeof<bool option>,
                    getterCode = fun args ->
                        let flds = args.[0]
                        let keyStr = rawKey
                        <@@ let k = YamlKey keyStr
                            RuntimeHelpers.TryGetBool(k, (%%flds : Map<YamlKey, YamlValue>)) @@>)

            | TInt ->
                ProvidedProperty(propName, typeof<int option>,
                    getterCode = fun args ->
                        let flds = args.[0]
                        let keyStr = rawKey
                        <@@ let k = YamlKey keyStr
                            RuntimeHelpers.TryGetInt(k, (%%flds : Map<YamlKey, YamlValue>)) @@>)

            | TFloat ->
                ProvidedProperty(propName, typeof<float option>,
                    getterCode = fun args ->
                        let flds = args.[0]
                        let keyStr = rawKey
                        <@@ let k = YamlKey keyStr
                            RuntimeHelpers.TryGetFloat(k, (%%flds : Map<YamlKey, YamlValue>)) @@>)

            | TList TString ->
                ProvidedProperty(propName, typeof<string list option>,
                    getterCode = fun args ->
                        let flds = args.[0]
                        let keyStr = rawKey
                        <@@ let k = YamlKey keyStr
                            RuntimeHelpers.TryGetStringList(k, (%%flds : Map<YamlKey, YamlValue>)) @@>)

            | TMapping nestedFields ->
                let nestedTypeName = toPascalCase rawKey + "Data"
                let nestedType = generateMappingType nestedTypeName nestedFields
                provided.AddMember nestedType
                ProvidedProperty(propName, typedefof<option<_>>.MakeGenericType(nestedType),
                    getterCode = fun args ->
                        let flds = args.[0]
                        let keyStr = rawKey
                        <@@ let k = YamlKey keyStr
                            (RuntimeHelpers.TryGetSubMap(k, (%%flds : Map<YamlKey, YamlValue>)) : Map<YamlKey, YamlValue> option) @@>)

            | _ ->
                ProvidedProperty(propName, typeof<string option>,
                    getterCode = fun args ->
                        let flds = args.[0]
                        let keyStr = rawKey
                        <@@ let k = YamlKey keyStr
                            RuntimeHelpers.TryGetString(k, (%%flds : Map<YamlKey, YamlValue>)) @@>)

        provided.AddMember prop

    provided

let private buildFrontMatterDefinition (schema: DiscoveredSchema) : ProvidedTypeDefinition =
    let defType = ProvidedTypeDefinition("FrontMatterDefinition", Some typeof<RawSkillData>, isErased = true)

    defType.AddMember(
        ProvidedProperty("Path", typeof<AbsoluteFilePath>,
            getterCode = fun args ->
                let raw = args.[0]
                <@@ (%%raw : RawSkillData).Path @@>))

    defType.AddMember(
        ProvidedProperty("Name", typeof<SkillName>,
            getterCode = fun args ->
                let raw = args.[0]
                <@@ let k = YamlKey "name"
                    match RuntimeHelpers.TryGetString(k, (%%raw : RawSkillData).Fields) with
                    | Some s -> SkillName.createUnsafe s
                    | None   -> failwith "Required field 'name' is missing" @@>))

    defType.AddMember(
        ProvidedProperty("Description", typeof<SkillDescription>,
            getterCode = fun args ->
                let raw = args.[0]
                <@@ let k = YamlKey "description"
                    match RuntimeHelpers.TryGetString(k, (%%raw : RawSkillData).Fields) with
                    | Some s -> SkillDescription.createUnsafe s
                    | None   -> failwith "Required field 'description' is missing" @@>))

    for kv in schema do
        let yamlKey = kv.Key
        let fieldSchema = kv.Value
        let (YamlKey rawKey) = yamlKey
        if rawKey <> "name" && rawKey <> "description" then
            let propName = toPascalCase rawKey

            let prop =
                match fieldSchema.Type with
                | TBool ->
                    ProvidedProperty(propName, typeof<bool option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetBool(k, (%%raw : RawSkillData).Fields) @@>)

                | TInt ->
                    ProvidedProperty(propName, typeof<int option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetInt(k, (%%raw : RawSkillData).Fields) @@>)

                | TFloat ->
                    ProvidedProperty(propName, typeof<float option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetFloat(k, (%%raw : RawSkillData).Fields) @@>)

                | TList TString ->
                    ProvidedProperty(propName, typeof<string list option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetStringList(k, (%%raw : RawSkillData).Fields) @@>)

                | TMapping nestedFields ->
                    let nestedTypeName = toPascalCase rawKey + "Data"
                    let nestedType = generateMappingType nestedTypeName nestedFields
                    defType.AddMember nestedType
                    ProvidedProperty(propName, typedefof<option<_>>.MakeGenericType(nestedType),
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                (RuntimeHelpers.TryGetSubMap(k, (%%raw : RawSkillData).Fields) : Map<YamlKey, YamlValue> option) @@>)

                | _ ->
                    ProvidedProperty(propName, typeof<string option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetString(k, (%%raw : RawSkillData).Fields) @@>)

            defType.AddMember prop

    defType

[<TypeProvider>]
type FrontMatterTypeProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(
        config,
        assemblyReplacementMap =
            [("YamlFrontMatter.TypeProvider.DesignTime", "YamlFrontMatter.TypeProvider")],
        addDefaultProbingLocation = true)

    let ns       = "YamlFrontMatter"
    let thisAsm  = Assembly.GetExecutingAssembly()

    let rootType =
        ProvidedTypeDefinition(thisAsm, ns, "FrontMatterProvider", Some typeof<obj>, isErased = true)

    let staticParams =
        [ ProvidedStaticParameter("RootDirectory", typeof<string>)
          ProvidedStaticParameter("Pattern",       typeof<string>, "SKILL.md") ]

    do
        rootType.DefineStaticParameters(staticParams, fun typeName args ->
            let rootDir = args.[0] :?> string
            let pattern = args.[1] :?> string

            let report =
                if Directory.Exists rootDir then
                    discoverSchemaWithStats rootDir pattern
                else
                    { Schema = Map.empty; FilesScanned = 0; FieldOccurrences = Map.empty }

            let root = ProvidedTypeDefinition(thisAsm, ns, typeName, Some typeof<obj>, isErased = true)

            let fmDef = buildFrontMatterDefinition report.Schema
            root.AddMember fmDef

            let seqOfDef = ProvidedTypeBuilder.MakeGenericType(typedefof<seq<_>>, [fmDef])
            let rootDir' = rootDir
            let pattern' = pattern
            root.AddMember(
                ProvidedMethod("GetAll", [], seqOfDef,
                    isStatic = true,
                    invokeCode = fun _args ->
                        <@@ RuntimeHelpers.GetAll(rootDir', pattern') @@>))

            let schemaText = formatSchema report
            let describeMethod =
                ProvidedMethod("Describe", [], typeof<string>,
                    isStatic = true,
                    invokeCode = fun _args -> <@@ schemaText @@>)
            describeMethod.AddXmlDoc
                "Returns the inferred FrontMatterDefinition schema as F# record source, \
                 annotated with how often each top-level field occurs across the scanned files."
            root.AddMember describeMethod

            root)

        this.AddNamespace(ns, [rootType])
