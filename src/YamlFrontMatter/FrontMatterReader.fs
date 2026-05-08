module YamlFrontMatter.FrontMatterReader

open System.IO
open System.Text
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference
open YamlFrontMatter.FrontMatterTextReader
open YamlFrontMatter.Schemas

// ---------------------------------------------------------------------------
// Read problems for the single-file API
//
// `tryRead` rolls every category of failure — IO, parse, schema — into one
// DU so callers don't have to chain Result-of-Result-of-Result. Callers that
// care about a specific category just pattern-match on the case.
// ---------------------------------------------------------------------------

type ReadProblem =
    /// File has no `---`-delimited YAML front matter region. The caller's
    /// reaction to this is usually "this file isn't a front-matter document
    /// at all" (e.g. a regular Markdown file with no metadata).
    | NoFrontMatter

    /// The opening `---` was found but the closing `---` is missing before
    /// EOF. The front matter region is therefore truncated and untrustworthy.
    | UnclosedFrontMatter

    /// Front matter region is delimited correctly but YAML inside it is
    /// malformed. The string carries the parser's diagnostic message.
    | YamlMalformed of detail: string

    /// File system error reading the file (not found, permission, encoding).
    | IoFailure of message: string

    /// Front matter parsed cleanly but failed the supplied schema. The list
    /// is the *full set* of failures — every missing/wrong/empty field — so
    /// callers can show all problems for one file in one pass.
    | ValidationFailed of failures: ValidationFailure list

// ---------------------------------------------------------------------------
// Single-file read with schema validation
//
// This is the schema-aware sibling of `Scanner.tryReadOne`. The two share the
// same low-level parsing path; this one additionally runs `Schemas.validate`
// and folds every outcome into the unified `ReadProblem` DU.
//
// `Scanner.tryReadOne` stays for compatibility with the existing TP runtime
// helpers and for low-level callers that only want raw parse output.
// ---------------------------------------------------------------------------

let private openFileSync (path: string) =
    new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite ||| FileShare.Delete, 16 * 1024)

/// Read a single file, parse its YAML front matter, and validate the result
/// against the supplied schema.
///
/// Returns `Ok raw` when the file has well-formed front matter satisfying the
/// schema; otherwise `Error problem` describing precisely why.
let tryRead (schema: FrontMatterSchema) (filePath: AbsoluteFilePath) : Result<RawFrontMatter, ReadProblem> =
    let pathStr = AbsoluteFilePath.value filePath
    try
        use stream = openFileSync pathStr
        use reader = new StreamReader(stream, Encoding.UTF8,
                                       detectEncodingFromByteOrderMarks = true,
                                       bufferSize = 16 * 1024,
                                       leaveOpen = false)
        match FrontMatterTextReader.TryCreate(reader) with
        | None ->
            Error NoFrontMatter
        | Some yamlReader ->
            use yamlReader = yamlReader
            try
                let yamlText = yamlReader.ReadToEnd()
                match yamlReader.EndReason with
                | Some ClosedByDelimiter ->
                    let rawMap = parseYamlText yamlText
                    let fields =
                        rawMap
                        |> Map.toSeq
                        |> Seq.map (fun (k, v) -> k, objToValue v)
                        |> Map.ofSeq
                    let raw = { Path = filePath; Fields = fields }
                    match validate schema raw with
                    | Ok _ -> Ok raw
                    | Error failures -> Error (ValidationFailed failures)
                | _ ->
                    Error UnclosedFrontMatter
            with ex ->
                match yamlReader.EndReason with
                | Some ClosedByDelimiter -> Error (YamlMalformed ex.Message)
                | _                      -> Error UnclosedFrontMatter
    with ex ->
        Error (IoFailure ex.Message)
