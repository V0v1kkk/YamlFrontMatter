module YamlFrontMatter.Tests.SchemaInferenceTests

open System
open System.Collections.Generic
open System.Globalization
open Xunit
open YamlFrontMatter.Types
open YamlFrontMatter.SchemaInference

// ---------------------------------------------------------------------------
// Helpers
//
// `inferNodeType` now operates on raw CLR objects (the shape VYaml emits
// from `Deserialize<obj>`). The helpers below construct that shape directly.
// `scalar` mirrors YAML's plain-scalar coercion (try bool → int → float →
// otherwise string) so the existing test cases keep their original semantics:
// `scalar "1"` is treated as int, `scalar "hello"` as string, etc.
// ---------------------------------------------------------------------------

let private scalar (v: string) : obj =
    let mutable b = false
    let mutable i = 0L
    let mutable f = 0.0
    if Boolean.TryParse(v, &b) then box b
    elif Int64.TryParse(v, &i) then box (int i)
    elif Double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, &f) then box f
    else box v

let private seqNode (values: string list) : obj =
    let list = List<obj>()
    for v in values do list.Add(scalar v)
    box list

let private mapNode (pairs: (string * string) list) : obj =
    let dict = Dictionary<obj, obj>()
    for (k, v) in pairs do dict.[box k] <- scalar v
    box dict

let private key = YamlKey

// ---------------------------------------------------------------------------
// inferNodeType — scalar types
// ---------------------------------------------------------------------------

[<Fact>]
let ``scalar 'true' infers TBool`` () =
    Assert.Equal(TBool, inferNodeType (scalar "true"))

[<Fact>]
let ``scalar 'false' infers TBool`` () =
    Assert.Equal(TBool, inferNodeType (scalar "false"))

[<Fact>]
let ``scalar 'True' infers TBool (case insensitive)`` () =
    Assert.Equal(TBool, inferNodeType (scalar "True"))

[<Fact>]
let ``scalar '42' infers TInt`` () =
    Assert.Equal(TInt, inferNodeType (scalar "42"))

[<Fact>]
let ``scalar '-7' infers TInt`` () =
    Assert.Equal(TInt, inferNodeType (scalar "-7"))

[<Fact>]
let ``scalar '3.14' infers TFloat`` () =
    Assert.Equal(TFloat, inferNodeType (scalar "3.14"))

[<Fact>]
let ``scalar '1.0.0' infers TString (not float)`` () =
    Assert.Equal(TString, inferNodeType (scalar "1.0.0"))

[<Fact>]
let ``scalar 'hello' infers TString`` () =
    Assert.Equal(TString, inferNodeType (scalar "hello"))

[<Fact>]
let ``scalar 'https://example.com' infers TString`` () =
    Assert.Equal(TString, inferNodeType (scalar "https://example.com"))

// ---------------------------------------------------------------------------
// inferNodeType — sequence
// ---------------------------------------------------------------------------

[<Fact>]
let ``sequence of strings infers TList TString`` () =
    Assert.Equal(TList TString, inferNodeType (seqNode ["fsharp"; "dotnet"]))

[<Fact>]
let ``sequence of integers infers TList TInt`` () =
    Assert.Equal(TList TInt, inferNodeType (seqNode ["1"; "2"; "3"]))

[<Fact>]
let ``empty sequence infers TList TString`` () =
    let s = box (List<obj>())
    Assert.Equal(TList TString, inferNodeType s)

[<Fact>]
let ``sequence of mixed int and string infers TList TString`` () =
    Assert.Equal(TList TString, inferNodeType (seqNode ["1"; "hello"]))

// ---------------------------------------------------------------------------
// inferNodeType — mapping
// ---------------------------------------------------------------------------

[<Fact>]
let ``mapping infers TMapping with child schemas`` () =
    let node = mapNode ["author", "Vladimir"; "revision", "3"]
    match inferNodeType node with
    | TMapping fields ->
        Assert.Equal(TString, fields.[key "author"].Type)
        Assert.Equal(TInt,    fields.[key "revision"].Type)
    | other -> failwith $"Expected TMapping, got %A{other}"

// ---------------------------------------------------------------------------
// mergeTypes — same types
// ---------------------------------------------------------------------------

[<Fact>]
let ``merge TString + TString = TString`` () =
    Assert.Equal(TString, mergeTypes TString TString)

[<Fact>]
let ``merge TBool + TBool = TBool`` () =
    Assert.Equal(TBool, mergeTypes TBool TBool)

[<Fact>]
let ``merge TInt + TInt = TInt`` () =
    Assert.Equal(TInt, mergeTypes TInt TInt)

[<Fact>]
let ``merge TFloat + TFloat = TFloat`` () =
    Assert.Equal(TFloat, mergeTypes TFloat TFloat)

// ---------------------------------------------------------------------------
// mergeTypes — numeric widening
// ---------------------------------------------------------------------------

[<Fact>]
let ``merge TBool + TInt = TInt`` () =
    Assert.Equal(TInt, mergeTypes TBool TInt)

[<Fact>]
let ``merge TInt + TBool = TInt`` () =
    Assert.Equal(TInt, mergeTypes TInt TBool)

[<Fact>]
let ``merge TInt + TFloat = TFloat`` () =
    Assert.Equal(TFloat, mergeTypes TInt TFloat)

[<Fact>]
let ``merge TFloat + TInt = TFloat`` () =
    Assert.Equal(TFloat, mergeTypes TFloat TInt)

[<Fact>]
let ``merge TBool + TFloat = TFloat`` () =
    Assert.Equal(TFloat, mergeTypes TBool TFloat)

// ---------------------------------------------------------------------------
// mergeTypes — string as widest type
// ---------------------------------------------------------------------------

[<Fact>]
let ``merge TBool + TString = TString`` () =
    Assert.Equal(TString, mergeTypes TBool TString)

[<Fact>]
let ``merge TInt + TString = TString`` () =
    Assert.Equal(TString, mergeTypes TInt TString)

[<Fact>]
let ``merge TFloat + TString = TString`` () =
    Assert.Equal(TString, mergeTypes TFloat TString)

[<Fact>]
let ``merge TString + TBool = TString`` () =
    Assert.Equal(TString, mergeTypes TString TBool)

// ---------------------------------------------------------------------------
// mergeTypes — list
// ---------------------------------------------------------------------------

[<Fact>]
let ``merge TList TInt + TList TString = TList TString`` () =
    Assert.Equal(TList TString, mergeTypes (TList TInt) (TList TString))

[<Fact>]
let ``merge TList TBool + TList TInt = TList TInt`` () =
    Assert.Equal(TList TInt, mergeTypes (TList TBool) (TList TInt))

[<Fact>]
let ``merge TList + scalar = TString`` () =
    Assert.Equal(TString, mergeTypes (TList TString) TString)
    Assert.Equal(TString, mergeTypes TInt (TList TString))

// ---------------------------------------------------------------------------
// mergeTypes — mapping
// ---------------------------------------------------------------------------

[<Fact>]
let ``merge TMapping + TString (conflict) = TString`` () =
    let m = TMapping(Map.ofList [key "x", { Type = TString; PresentInAll = true }])
    Assert.Equal(TString, mergeTypes m TString)
    Assert.Equal(TString, mergeTypes TString m)

[<Fact>]
let ``merge two TMappings merges children with correct presence`` () =
    // File 1: metadata: { author: string }
    // File 2: metadata: { author: string, revision: int }
    // Result:  author(present in all), revision(not present in all)
    let m1 = TMapping(Map.ofList [key "author", { Type = TString; PresentInAll = true }])
    let m2 = TMapping(Map.ofList [key "author", { Type = TString; PresentInAll = true }
                                  key "revision", { Type = TInt; PresentInAll = true }])
    match mergeTypes m1 m2 with
    | TMapping merged ->
        Assert.True(merged.ContainsKey(key "author"))
        Assert.True(merged.ContainsKey(key "revision"))
        // revision was only in m2, so PresentInAll should be false after merge
        Assert.False(merged.[key "revision"].PresentInAll)
    | other -> failwith $"Expected TMapping, got %A{other}"

[<Fact>]
let ``merge TMappings with conflicting child types widens child`` () =
    let m1 = TMapping(Map.ofList [key "count", { Type = TBool; PresentInAll = true }])
    let m2 = TMapping(Map.ofList [key "count", { Type = TInt;  PresentInAll = true }])
    match mergeTypes m1 m2 with
    | TMapping merged -> Assert.Equal(TInt, merged.[key "count"].Type)
    | other -> failwith $"Expected TMapping, got %A{other}"

// ---------------------------------------------------------------------------
// inferSchema — cross-file
// ---------------------------------------------------------------------------

[<Fact>]
let ``field present in all files is PresentInAll`` () =
    let f1 = Map.ofList [key "name", scalar "a"; key "version", scalar "1"]
    let f2 = Map.ofList [key "name", scalar "b"; key "version", scalar "2"]
    let schema = inferSchema [f1; f2]
    Assert.True(schema.[key "name"].PresentInAll)
    Assert.True(schema.[key "version"].PresentInAll)

[<Fact>]
let ``field present in some files is not PresentInAll`` () =
    let f1 = Map.ofList [key "name", scalar "a"]
    let f2 = Map.ofList [key "name", scalar "b"; key "origin", scalar "https://example.com"]
    let schema = inferSchema [f1; f2]
    Assert.False(schema.[key "origin"].PresentInAll)

[<Fact>]
let ``schema merges types across files`` () =
    // File 1: priority: true (bool)  File 2: priority: 42 (int) → widens to TInt
    let f1 = Map.ofList [key "priority", scalar "true"]
    let f2 = Map.ofList [key "priority", scalar "42"]
    let schema = inferSchema [f1; f2]
    Assert.Equal(TInt, schema.[key "priority"].Type)

[<Fact>]
let ``schema merges bool+string to string`` () =
    let f1 = Map.ofList [key "value", scalar "true"]
    let f2 = Map.ofList [key "value", scalar "hello"]
    let schema = inferSchema [f1; f2]
    Assert.Equal(TString, schema.[key "value"].Type)

[<Fact>]
let ``empty file list returns empty schema`` () =
    let schema = inferSchema []
    Assert.Empty(schema)

// ---------------------------------------------------------------------------
// toPascalCase
// ---------------------------------------------------------------------------

[<Fact>]
let ``toPascalCase handles hyphen-separated`` () =
    Assert.Equal("AllowedTools", toPascalCase "allowed-tools")

[<Fact>]
let ``toPascalCase handles underscore_separated`` () =
    Assert.Equal("SomeName", toPascalCase "some_name")

[<Fact>]
let ``toPascalCase handles single word`` () =
    Assert.Equal("Name", toPascalCase "name")

[<Fact>]
let ``toPascalCase handles mixed separators`` () =
    Assert.Equal("SomeLongKey", toPascalCase "some-long_key")

[<Fact>]
let ``toPascalCase preserves already-pascal`` () =
    Assert.Equal("Description", toPascalCase "description")

// ---------------------------------------------------------------------------
// discoverSchema — integration with real fixture files
// ---------------------------------------------------------------------------

[<Fact>]
let ``discoverSchema finds keys from all fixture files`` () =
    let fixturesDir =
        System.IO.Path.GetFullPath(
            System.IO.Path.Combine(__SOURCE_DIRECTORY__, "Fixtures"))
    let schema = discoverSchema fixturesDir "SKILL.md"

    // Keys present in all 3 files
    Assert.True(schema.[key "name"].PresentInAll)
    Assert.True(schema.[key "description"].PresentInAll)

    // Keys present in only some files
    Assert.False(schema.[key "version"].PresentInAll)
    Assert.False(schema.[key "active"].PresentInAll)
    Assert.False(schema.[key "priority"].PresentInAll)
    Assert.False(schema.[key "tags"].PresentInAll)
    Assert.False(schema.[key "metadata"].PresentInAll)

[<Fact>]
let ``discoverSchema infers correct types for fixture fields`` () =
    let fixturesDir =
        System.IO.Path.GetFullPath(
            System.IO.Path.Combine(__SOURCE_DIRECTORY__, "Fixtures"))
    let schema = discoverSchema fixturesDir "SKILL.md"

    Assert.Equal(TString,     schema.[key "name"].Type)
    Assert.Equal(TString,     schema.[key "description"].Type)
    Assert.Equal(TString,     schema.[key "version"].Type)
    Assert.Equal(TBool,       schema.[key "active"].Type)
    Assert.Equal(TInt,        schema.[key "priority"].Type)
    Assert.Equal(TList TString, schema.[key "tags"].Type)

    match schema.[key "metadata"].Type with
    | TMapping fields ->
        Assert.Equal(TString, fields.[key "author"].Type)
        Assert.Equal(TInt,    fields.[key "revision"].Type)
    | other -> failwith $"Expected TMapping for metadata, got %A{other}"

