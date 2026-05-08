module YamlFrontMatter.TypeProvider.DesignTime.Provider

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
// In `skill` mode, `Name : SkillName` and `Description : SkillDescription`
// are emitted as typed *required* properties (validation guarantees their
// presence). In `general` mode, they're not special-cased — they show up as
// regular `string option` fields if the discovery saw them.
// ---------------------------------------------------------------------------

let private buildFrontMatterDefinition (isSkillMode: bool) (schema: DiscoveredSchema) : ProvidedTypeDefinition =
    let defType = ProvidedTypeDefinition("FrontMatterDefinition", Some typeof<RawFrontMatter>, isErased = true)

    // Path is always present (synthesised by the scanner).
    defType.AddMember(
        ProvidedProperty("Path", typeof<AbsoluteFilePath>,
            getterCode = fun args ->
                let raw = args.[0]
                <@@ (%%raw : RawFrontMatter).Path @@>))

    // Name / Description are typed required properties *only* in skill mode.
    if isSkillMode then
        defType.AddMember(
            ProvidedProperty("Name", typeof<SkillName>,
                getterCode = fun args ->
                    let raw = args.[0]
                    <@@ let k = YamlKey "name"
                        match RuntimeHelpers.TryGetString(k, (%%raw : RawFrontMatter).Fields) with
                        | Some s -> SkillName.createUnsafe s
                        | None   -> failwith "Required field 'name' is missing — bug: scanner should have rejected this file in skill mode" @@>))

        defType.AddMember(
            ProvidedProperty("Description", typeof<SkillDescription>,
                getterCode = fun args ->
                    let raw = args.[0]
                    <@@ let k = YamlKey "description"
                        match RuntimeHelpers.TryGetString(k, (%%raw : RawFrontMatter).Fields) with
                        | Some s -> SkillDescription.createUnsafe s
                        | None   -> failwith "Required field 'description' is missing — bug: scanner should have rejected this file in skill mode" @@>))

    // Field loop. Skip name/description only in skill mode; in general mode
    // they're ordinary discovered fields and follow the same path as everyone else.
    let isSpecialName (rawKey: string) =
        isSkillMode && (rawKey = "name" || rawKey = "description")

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

    defType

// ---------------------------------------------------------------------------
// Mode parsing — the static parameter is a string; defaults to "general".
// ---------------------------------------------------------------------------

let private parseMode (raw: string) : FrontMatterSchema * bool =
    let normalised =
        if isNull raw then "general"
        else raw.Trim().ToLowerInvariant()
    match normalised with
    | "skill"   -> Skill,   true
    | "general" -> General, false
    | _         -> General, false   // forward-compat: unknown mode → permissive

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
          ProvidedStaticParameter("Mode",          typeof<string>, "general") ]

    do
        rootType.DefineStaticParameters(staticParams, fun typeName args ->
            let rootDir = args.[0] :?> string
            let pattern = args.[1] :?> string
            let modeRaw = args.[2] :?> string

            let _schema, isSkillMode = parseMode modeRaw
            let modeToken = if isSkillMode then "skill" else "general"

            // Schema-discovery is independent of validation: we union the field
            // shape across *all* files in the directory, regardless of whether
            // each one will eventually pass schema validation. The TP exposes
            // every discovered field as `option`, so missing-from-some-files is
            // safe at the property level.
            let report =
                if Directory.Exists rootDir then
                    discoverSchemaWithStats rootDir pattern
                else
                    { Schema = Map.empty; FilesScanned = 0; FieldOccurrences = Map.empty }

            let root = ProvidedTypeDefinition(thisAsm, ns, typeName, Some typeof<obj>, isErased = true)

            let fmDef = buildFrontMatterDefinition isSkillMode report.Schema
            root.AddMember fmDef

            // GetAll : unit -> seq<FrontMatterDefinition> — only items that
            // pass schema validation.
            let seqOfDef = ProvidedTypeBuilder.MakeGenericType(typedefof<seq<_>>, [fmDef])
            let rootDir' = rootDir
            let pattern' = pattern
            let modeToken' = modeToken
            let getAll =
                ProvidedMethod("GetAll", [], seqOfDef,
                    isStatic = true,
                    invokeCode = fun _args ->
                        <@@ RuntimeHelpers.GetAll(rootDir', pattern', modeToken') @@>)
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
                        <@@ RuntimeHelpers.GetRejected(rootDir', pattern', modeToken') @@>)
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
                        <@@ RuntimeHelpers.GetSkipped(rootDir', pattern', modeToken') @@>)
            getSkipped.AddXmlDoc
                "Returns files that don't have parseable front matter (no `---` block, \
                 yaml malformed, IO error). Distinct from GetRejected — these files aren't \
                 attempting to be valid front-matter documents."
            root.AddMember getSkipped

            // Describe() — schema visualisation as F# record source.
            // Use the mode-aware formatter so the printed record matches what
            // this TP actually generated (Name/Description typed in skill mode,
            // ordinary `string option` in general mode).
            let schemaText = formatSchemaForMode isSkillMode report
            let modeNote =
                if isSkillMode then
                    "// Mode: skill — Name and Description are required and typed.\n"
                else
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
