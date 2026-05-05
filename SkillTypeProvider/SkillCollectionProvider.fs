module SkillTypeProvider.SkillCollectionProvider

open System
open System.IO
open System.Reflection
open System.Threading
open FSharp.Core.CompilerServices
open ProviderImplementation.ProvidedTypes
open SkillFrontMatter.Core.Types
open SkillFrontMatter.Core.SchemaInference
open SkillFrontMatter.Core.SkillScanner

// ---------------------------------------------------------------------------
// Runtime helpers — called from generated property/method bodies
// ---------------------------------------------------------------------------

[<AbstractClass; Sealed>]
type RuntimeHelpers private () =

    static member TryGetString(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YString s) -> Some s
        | Some (YBool b)   -> Some (string b)
        | Some (YInt i)    -> Some (string i)
        | Some (YFloat f)  -> Some (string f)
        | _                -> None

    static member TryGetBool(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YBool b) -> Some b
        | _              -> None

    static member TryGetInt(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YInt i) -> Some i
        | _             -> None

    static member TryGetFloat(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YFloat f) -> Some f
        | _               -> None

    static member TryGetStringList(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YList items) ->
            let strings = items |> List.choose (function YString s -> Some s | _ -> None)
            if strings.Length = items.Length then Some strings else None
        | _ -> None

    static member TryGetSubMap(key: YamlKey, fields: Map<YamlKey, YamlValue>) =
        match Map.tryFind key fields with
        | Some (YMap m) -> Some m
        | _             -> None

    static member GetAll(rootDir: string, pattern: string) : RawSkillData seq =
        seq {
            let ct = CancellationToken.None
            let opts =
                { RootDirectory       = AbsoluteFilePath.createUnsafe rootDir
                  Pattern             = pattern
                  Parallelism         = 8
                  PathQueueCapacity   = 256
                  ResultQueueCapacity = 256 }
            let reader = scan opts ct
            let mutable keepGoing = true
            while keepGoing do
                let canRead = reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()
                if not canRead then
                    keepGoing <- false
                else
                    let mutable more = true
                    while more do
                        let mutable item = Unchecked.defaultof<Result<RawSkillData option, ScanError>>
                        if reader.TryRead(&item) then
                            match item with
                            | Ok (Some skill) -> yield skill
                            | _ -> ()
                        else
                            more <- false
        }

// ---------------------------------------------------------------------------
// Erased type generation helpers
// ---------------------------------------------------------------------------

// For erased TPs: nested types do NOT take assembly/ns arguments.
// Root types take the actual executing assembly and a namespace.

let rec private generateMappingType
    (typeName: string)
    (fields: Map<YamlKey, FieldSchema>)
    : ProvidedTypeDefinition =

    // Erased to Map<YamlKey, YamlValue> at runtime
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

let private buildSkillDefinition (schema: DiscoveredSchema) : ProvidedTypeDefinition =
    // Erased to RawSkillData at runtime
    let skillDef = ProvidedTypeDefinition("SkillDefinition", Some typeof<RawSkillData>, isErased = true)

    // Path
    skillDef.AddMember(
        ProvidedProperty("Path", typeof<AbsoluteFilePath>,
            getterCode = fun args ->
                let raw = args.[0]
                <@@ (%%raw : RawSkillData).Path @@>))

    // Name — required
    skillDef.AddMember(
        ProvidedProperty("Name", typeof<SkillName>,
            getterCode = fun args ->
                let raw = args.[0]
                <@@ let k = YamlKey "name"
                    match RuntimeHelpers.TryGetString(k, (%%raw : RawSkillData).Fields) with
                    | Some s -> SkillName.createUnsafe s
                    | None   -> failwith "Required field 'name' is missing" @@>))

    // Description — required
    skillDef.AddMember(
        ProvidedProperty("Description", typeof<SkillDescription>,
            getterCode = fun args ->
                let raw = args.[0]
                <@@ let k = YamlKey "description"
                    match RuntimeHelpers.TryGetString(k, (%%raw : RawSkillData).Fields) with
                    | Some s -> SkillDescription.createUnsafe s
                    | None   -> failwith "Required field 'description' is missing" @@>))

    // Discovered optional fields
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
                    skillDef.AddMember nestedType
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

            skillDef.AddMember prop

    skillDef

// ---------------------------------------------------------------------------
// The Type Provider
// ---------------------------------------------------------------------------

[<TypeProvider>]
type SkillCollectionTypeProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(
        config,
        addDefaultProbingLocation = true)

    let ns       = "SkillTypeProvider"
    let thisAsm  = Assembly.GetExecutingAssembly()

    // The un-parameterised root type (placeholder for static param application)
    let rootType =
        ProvidedTypeDefinition(thisAsm, ns, "SkillCollectionProvider", Some typeof<obj>, isErased = true)

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

            // The instantiated root type — erased to obj
            let root = ProvidedTypeDefinition(thisAsm, ns, typeName, Some typeof<obj>, isErased = true)

            // Nested SkillDefinition
            let skillDef = buildSkillDefinition report.Schema
            root.AddMember skillDef

            // GetAll() returns seq<SkillDefinition> (provided) — erases to seq<RawSkillData>
            let seqOfSkill = ProvidedTypeBuilder.MakeGenericType(typedefof<seq<_>>, [skillDef])
            let rootDir' = rootDir
            let pattern' = pattern
            root.AddMember(
                ProvidedMethod("GetAll", [], seqOfSkill,
                    isStatic = true,
                    invokeCode = fun _args ->
                        <@@ RuntimeHelpers.GetAll(rootDir', pattern') @@>))

            // Describe() returns the inferred schema as F# record source — useful
            // for agents/humans planning typed filter code over the collection.
            // Computed at design time and embedded as a string literal so the
            // visualization matches the *exact* schema the types were generated from.
            let schemaText = formatSchema report
            let describeMethod =
                ProvidedMethod("Describe", [], typeof<string>,
                    isStatic = true,
                    invokeCode = fun _args -> <@@ schemaText @@>)
            describeMethod.AddXmlDoc
                "Returns the inferred SkillDefinition schema as F# record source, \
                 annotated with how often each top-level field occurs across the scanned files."
            root.AddMember describeMethod

            root)

        this.AddNamespace(ns, [rootType])

[<assembly: TypeProviderAssembly>]
do ()
