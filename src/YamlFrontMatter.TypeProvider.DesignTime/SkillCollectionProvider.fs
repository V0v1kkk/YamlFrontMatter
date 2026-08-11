module YamlFrontMatter.TypeProvider.DesignTime.Provider

open System
open System.IO
open System.Reflection
open FSharp.Core.CompilerServices
open ProviderImplementation.ProvidedTypes
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.Schemas
open YamlFrontMatter.Scanner
open YamlFrontMatter

// ---------------------------------------------------------------------------
// Helpers for generating typed property accessors over a discovered schema
// ---------------------------------------------------------------------------

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

// ---------------------------------------------------------------------------
// Build the FrontMatterDefinition provided type
//
// In `skill` and `agent-skill` modes, `Name : SkillName` and `Description : SkillDescription`
// are emitted as typed *required* properties (validation guarantees their
// presence). In `agent-skill` mode with `EmbeddedMetadataKey` configured,
// `ExtensionMetadata : ExtensionMetadataData option` is also generated.
// In `general` mode, they're not special-cased — they show up as regular
// `string option` fields if the discovery saw them.
// ---------------------------------------------------------------------------

let private buildFrontMatterDefinition
    (modeToken: string)
    (embeddedKey: string)
    (schema: DiscoveredSchema)
    (extensionSchema: DiscoveredSchema option)
    : ProvidedTypeDefinition =

    let defType = ProvidedTypeDefinition("FrontMatterDefinition", Some typeof<RawFrontMatter>, isErased = true)
    let isSkillMode = modeToken = "skill"
    let isAgentSkillMode = modeToken = "agent-skill"

    // Path is always present (synthesised by the scanner).
    defType.AddMember(
        ProvidedProperty("Path", typeof<AbsoluteFilePath>,
            getterCode = fun args ->
                let raw = args.[0]
                <@@ (%%raw : RawFrontMatter).Path @@>))

    // Name / Description are typed required properties in skill and agent-skill mode.
    if isSkillMode || isAgentSkillMode then
        defType.AddMember(
            ProvidedProperty("Name", typeof<SkillName>,
                getterCode = fun args ->
                    let raw = args.[0]
                    <@@ let k = YamlKey "name"
                        match RuntimeHelpers.TryGetString(k, (%%raw : RawFrontMatter).Fields) with
                        | Some s -> SkillName.createUnsafe s
                        | None   -> failwith "Required field 'name' is missing — bug: scanner should have rejected this file" @@>))

        defType.AddMember(
            ProvidedProperty("Description", typeof<SkillDescription>,
                getterCode = fun args ->
                    let raw = args.[0]
                    <@@ let k = YamlKey "description"
                        match RuntimeHelpers.TryGetString(k, (%%raw : RawFrontMatter).Fields) with
                        | Some s -> SkillDescription.createUnsafe s
                        | None   -> failwith "Required field 'description' is missing — bug: scanner should have rejected this file" @@>))

    // Field loop. Skip name/description only in skill and agent-skill modes.
    let isSpecialName (rawKey: string) =
        (isSkillMode || isAgentSkillMode) && (rawKey = "name" || rawKey = "description")

    for kv in schema do
        let yamlKey = kv.Key
        let fieldSchema = kv.Value
        let (YamlKey rawKey) = yamlKey
        if not (isSpecialName rawKey) then
            let propName = toPascalCase rawKey

            let prop =
                match fieldSchema.Type with
                | TBool ->
                    ProvidedProperty(propName, typeof<bool option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetBool(k, (%%raw : RawFrontMatter).Fields) @@>)

                | TInt ->
                    ProvidedProperty(propName, typeof<int option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetInt(k, (%%raw : RawFrontMatter).Fields) @@>)

                | TFloat ->
                    ProvidedProperty(propName, typeof<float option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetFloat(k, (%%raw : RawFrontMatter).Fields) @@>)

                | TList TString ->
                    ProvidedProperty(propName, typeof<string list option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetStringList(k, (%%raw : RawFrontMatter).Fields) @@>)

                | TMapping nestedFields ->
                    let nestedTypeName = toPascalCase rawKey + "Data"
                    let nestedType = generateMappingType nestedTypeName nestedFields
                    defType.AddMember nestedType
                    ProvidedProperty(propName, typedefof<option<_>>.MakeGenericType(nestedType),
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                (RuntimeHelpers.TryGetSubMap(k, (%%raw : RawFrontMatter).Fields) : Map<YamlKey, YamlValue> option) @@>)

                | _ ->
                    ProvidedProperty(propName, typeof<string option>,
                        getterCode = fun args ->
                            let raw = args.[0]
                            let keyStr = rawKey
                            <@@ let k = YamlKey keyStr
                                RuntimeHelpers.TryGetString(k, (%%raw : RawFrontMatter).Fields) @@>)

            defType.AddMember prop

    // ExtensionMetadata property when embeddedKey is configured
    if isAgentSkillMode && not (String.IsNullOrWhiteSpace embeddedKey) then
        let extSchema = extensionSchema |> Option.defaultValue Map.empty
        let extTypeName = "ExtensionMetadataData"
        let extType = generateMappingType extTypeName extSchema
        defType.AddMember extType
        let extProp =
            ProvidedProperty("ExtensionMetadata", typedefof<option<_>>.MakeGenericType(extType),
                getterCode = fun args ->
                    let raw = args.[0]
                    let keyStr = embeddedKey
                    <@@ let k = YamlKey keyStr
                        (RuntimeHelpers.TryGetExtensionMetadata(k, (%%raw : RawFrontMatter).Fields) : Map<YamlKey, YamlValue> option) @@>)
        defType.AddMember extProp

    defType

// ---------------------------------------------------------------------------
// Mode parsing — the static parameter is a string; defaults to "general".
// ---------------------------------------------------------------------------

let private parseMode (raw: string) : FrontMatterSchema * string =
    let normalised =
        if isNull raw then "general"
        else raw.Trim().ToLowerInvariant()
    match normalised with
    | "agent-skill" | "agentskill" -> AgentSkill None, "agent-skill"
    | "skill"                      -> Skill,           "skill"
    | "general"                    -> General,         "general"
    | _                            -> General,         "general"

// ---------------------------------------------------------------------------
// The TypeProvider
// ---------------------------------------------------------------------------

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
          ProvidedStaticParameter("Pattern",       typeof<string>, "SKILL.md")
          ProvidedStaticParameter("Mode",          typeof<string>, "general")
          ProvidedStaticParameter("EmbeddedMetadataKey", typeof<string>, "") ]

    do
        rootType.DefineStaticParameters(staticParams, fun typeName args ->
            let rootDir = args.[0] :?> string
            let pattern = args.[1] :?> string
            let modeRaw = args.[2] :?> string
            let embeddedKeyRaw = if args.Length > 3 then args.[3] :?> string else ""
            let embeddedKey = if isNull embeddedKeyRaw then "" else embeddedKeyRaw.Trim()

            let _, modeToken = parseMode modeRaw
            let embeddedKeyOpt = if String.IsNullOrWhiteSpace embeddedKey then None else Some embeddedKey
            let schema =
                match modeToken with
                | "agent-skill" -> Schemas.AgentSkill embeddedKeyOpt
                | "skill" -> Schemas.Skill
                | _ -> Schemas.General

            let report, extensionReport =
                if Directory.Exists rootDir then
                    Schemas.discoverValidatedSchemaWithStatsAndExtension schema rootDir pattern
                else
                    { Schema = Map.empty; FilesScanned = 0; FieldOccurrences = Map.empty }, None

            let root = ProvidedTypeDefinition(thisAsm, ns, typeName, Some typeof<obj>, isErased = true)

            let extensionSchema = extensionReport |> Option.map (fun r -> r.Schema)
            let fmDef = buildFrontMatterDefinition modeToken embeddedKey report.Schema extensionSchema
            root.AddMember fmDef

            // GetAll : unit -> seq<FrontMatterDefinition> — only items that
            // pass schema validation.
            let seqOfDef = ProvidedTypeBuilder.MakeGenericType(typedefof<seq<_>>, [fmDef])
            let rootDir' = rootDir
            let pattern' = pattern
            let modeToken' = modeToken
            let embeddedKey' = embeddedKey

            let getAll =
                ProvidedMethod("GetAll", [], seqOfDef,
                    isStatic = true,
                    invokeCode = fun _args ->
                        <@@ RuntimeHelpers.GetAll(rootDir', pattern', modeToken', embeddedKey') @@>)
            getAll.AddXmlDoc
                "Returns every file whose front matter passed schema validation. \
                 Files that failed validation are exposed via GetRejected(); files without front matter via GetSkipped()."
            root.AddMember getAll

            // GetRejected : unit -> seq<FrontMatterRejection>
            let seqOfRejection =
                ProvidedTypeBuilder.MakeGenericType(typedefof<seq<_>>, [typeof<FrontMatterRejection>])
            let getRejected =
                ProvidedMethod("GetRejected", [], seqOfRejection,
                    isStatic = true,
                    invokeCode = fun _args ->
                        <@@ RuntimeHelpers.GetRejected(rootDir', pattern', modeToken', embeddedKey') @@>)
            getRejected.AddXmlDoc
                "Returns files that have front matter but failed schema validation, \
                 along with the per-field validation failures."
            root.AddMember getRejected

            // GetSkipped : unit -> seq<FrontMatterSkip>
            let seqOfSkip =
                ProvidedTypeBuilder.MakeGenericType(typedefof<seq<_>>, [typeof<FrontMatterSkip>])
            let getSkipped =
                ProvidedMethod("GetSkipped", [], seqOfSkip,
                    isStatic = true,
                    invokeCode = fun _args ->
                        <@@ RuntimeHelpers.GetSkipped(rootDir', pattern', modeToken', embeddedKey') @@>)
            getSkipped.AddXmlDoc
                "Returns files that don't have parseable front matter (no `---` block, \
                 yaml malformed, IO error). Distinct from GetRejected — these files aren't \
                 attempting to be valid front-matter documents."
            root.AddMember getSkipped

            // Describe() — schema visualisation as F# record source.
            // Use the mode-aware formatter so the printed record matches what
            // this TP actually generated.
            let schemaText = formatSchemaForModeWithExtension modeToken embeddedKeyOpt report extensionReport
            let modeNote =
                match modeToken with
                | "agent-skill" ->
                    "// Mode: agent-skill — strict Agent Skills validation with typed Name/Description.\n"
                | "skill" ->
                    "// Mode: skill — Name and Description are required and typed.\n"
                | _ ->
                    "// Mode: general — every field is optional.\n"
            let fullText = modeNote + schemaText
            let describeMethod =
                ProvidedMethod("Describe", [], typeof<string>,
                    isStatic = true,
                    invokeCode = fun _args -> <@@ fullText @@>)
            describeMethod.AddXmlDoc
                "Returns the inferred FrontMatterDefinition schema as F# record source, \
                 annotated with how often each top-level field occurs across the scanned files. \
                 The first comment line states which Mode the type was generated for."
            root.AddMember describeMethod

            root)

        this.AddNamespace(ns, [rootType])
