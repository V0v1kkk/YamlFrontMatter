---
name: yamlfm-collection-analysis
description: >
  Use the YamlFrontMatter F# Type Provider from a `dotnet fsi` script to inspect,
  query, and audit any directory of Markdown files with YAML front matter
  (skill collections, recipes, blog posts, knowledge bases, etc). Trigger when
  the user asks to "analyse / audit / list / find skills (or recipes, or notes)
  in a directory", "what's in my skill collection", "find inconsistencies in
  this folder of Markdown files", "show me which skills are missing X" — or
  whenever the user has a directory of `*.md` files with `---`-delimited YAML
  front matter and wants typed, programmatic access. The skill teaches the
  describe-first-then-query workflow: use `Describe()` to learn the inferred
  schema, then write a targeted `.fsx` query against the strongly-typed
  generated record.
metadata:
  author: Vladimir Rogozhin
  version: '1.0'
  sources: >
    https://www.nuget.org/packages/YamlFrontMatter.TypeProvider
    https://github.com/V0v1kkk/YamlFrontMatter
dependsOnSkills: [ "fsharp-style" ]
requiresDependencies:
  - ".NET 8 SDK or later (for `dotnet fsi`)"
  - "YamlFrontMatter.TypeProvider NuGet package (auto-resolved by `#r \"nuget: ...\"`)"
requiresEnvironmentVariables: []
---

# YAML Front Matter Collection Analysis

## What this skill is for

The `YamlFrontMatter.TypeProvider` NuGet package gives F# scripts **compile-time-typed access** to any directory of Markdown files with YAML front matter. Point it at a directory; it scans every `*.md` (or matching pattern), infers the union schema across all files, and exposes the result as a real F# record type. You write `Seq.filter`, `Seq.groupBy`, `Seq.choose` over typed properties — F# checks the field names, F# inflects the types.

This skill captures the **workflow** for using that capability:

1. **Describe first** — print the inferred schema. You learn what fields exist, how often each occurs, and whether any field has nested structure. This is your map of the territory.
2. **Then query** — write a targeted `.fsx` against the typed record. The agent doesn't have to guess field names or remember casing — autocomplete + type checks do it.

The output of step 1 is human-readable F# record source. The output of step 2 is whatever the user asked for: a list, a CSV, a markdown table, a `Seq.iter` printf, etc.

## When to use

Trigger this skill when:

- The user asks to **survey, audit, list, or query** a directory of Markdown files with YAML front matter (skills, recipes, blog posts, ADRs, knowledge-base entries).
- The user asks for **inconsistency / typo / outlier detection** across a collection of Markdown documents.
- The user asks for **categorisation, counting, or grouping** of such documents.
- The user wants to **generate a report or table** from the YAML metadata of many Markdown files.

Do **not** trigger when:
- The user has a single Markdown file (parse it inline; don't pull in a TP).
- The user wants to **edit** the front matter (this skill is read-only analysis).
- The user wants schema-less / dynamic JSON-like access — for that, parse the YAML directly with VYaml/YamlDotNet.

## The describe-first-then-query workflow

### Step 1 — Describe the collection

Always start here on an unfamiliar directory. Save as `describe.fsx`:

```fsharp
#r "nuget: YamlFrontMatter.TypeProvider"

open YamlFrontMatter

[<Literal>]
let Root = "/absolute/path/to/the/collection"   // must be a string literal

// For a SKILL.md collection, request Skill mode — this gives typed
// `Name : SkillName` / `Description : SkillDescription` accessors AND
// surfaces files that fail validation via `GetRejected()`.
type Collection = FrontMatterProvider<Root, Mode = "skill">

// For arbitrary YAML-front-matter collections (recipes, blog posts, ADRs)
// drop the Mode argument — default is "general", every field becomes optional.
//   type Posts = FrontMatterProvider<Root, Pattern = "*.md">

printfn "%s" (Collection.Describe())
```

Run with `dotnet fsi describe.fsx`. Output is **valid F# record source code** annotated with frequency stats, e.g.:

```fsharp
type SkillDefinition = {
    Path            : AbsoluteFilePath                   // always (synthesised by the scanner)
    Name            : SkillName                          // required (SKILL.md convention)
    Description     : SkillDescription                   // required (SKILL.md convention)
    Origin          : string option                      // present in 17/19 files
    Version         : string option                      // present in 8/19 files
    Metadata        : MetadataData option                // present in 3/19 files
    Source          : string option                      // present in 1/19 files
    ...
}

and MetadataData = {
    Author          : string option                      // always present in this record
    Sources         : string option                      // optional within this record
    ...
}
```

This output is your contract for the next step. Read off:
- **What fields exist** at the top level and inside each nested mapping
- **How often** each field appears — universal vs common vs rare
- **Type of each field** — `string` / `int` / `bool` / `string list` / nested-record-name `option`

Two practical names you'll need in the next script:
- The provided collection type is `Collection` (whatever you named it on `type Collection = FrontMatterProvider<...>`).
- The provided record type is `Collection.FrontMatterDefinition` — the actual generated name. *(Heads-up: the textual schema may say `type SkillDefinition` in the comment header — that's fixed text in the formatter, not the actual type name. Always use `Collection.FrontMatterDefinition` in code.)*

### Step 2 — Write the targeted query

Now you know the shape. Write a query script. Examples below — pick the closest pattern, adjust.

---

## Pattern: filter and list

> "Show me all skills tagged with X" / "find all entries from author Y"

```fsharp
#r "nuget: YamlFrontMatter.TypeProvider"

open YamlFrontMatter
open YamlFrontMatter.Types

[<Literal>]
let Root = "/absolute/path/to/the/collection"

// Skill mode → typed Name / Description accessors.
type Collection = FrontMatterProvider<Root, Mode = "skill">

Collection.GetAll()
|> Seq.filter (fun s -> s.Version.IsSome)
|> Seq.iter (fun s ->
    printfn "  %-30s v%s" (SkillName.value s.Name) (s.Version.Value))
```

`SkillName.value` unwraps the typed `SkillName` (a private single-case DU); `.Value` (member accessor) works equivalently.

## Pattern: group by category

> "How are skills distributed across folders?"

```fsharp
let categoryOf (s: Collection.FrontMatterDefinition) =
    let path = AbsoluteFilePath.value s.Path
    let rel  = path.Substring(Root.Length).TrimStart('/')
    rel.Split('/').[0]

Collection.GetAll()
|> Seq.groupBy categoryOf
|> Seq.sortBy fst
|> Seq.iter (fun (category, items) ->
    printfn "%-22s %d items" category (Seq.length items))
```

The provided record carries the **resolved absolute path** as `s.Path : AbsoluteFilePath`. Substringing off the root prefix gives you the directory hierarchy.

## Pattern: anomaly / outlier detection

> "Find inconsistencies — typos, missing fields, alternate names for the same concept."

This is where the schema's frequency annotations earn their keep. A field present in 75–99% of files is likely *meant* to be in all of them; the missing few are outliers worth review.

```fsharp
let all = Collection.GetAll() |> Seq.toList
let n   = List.length all

// "common but not universal" — fields present in 75-99% of files
let saturation (predicate: Collection.FrontMatterDefinition -> bool) =
    all |> List.filter predicate |> List.length

let originPresent = saturation (fun s -> s.Origin.IsSome)
let sourcePresent = saturation (fun s -> s.Source.IsSome)

if originPresent > 0 && sourcePresent > 0 then
    printfn "Both 'origin' (%d) and 'source' (%d) used — same concept under two names:" originPresent sourcePresent
    for s in all do
        match s.Source with
        | Some src ->
            printfn "  • %s uses 'source: %s' instead of 'origin'"
                (SkillName.value s.Name) src
        | None -> ()

if originPresent > n * 3 / 4 then
    let missing = all |> List.filter (fun s -> s.Origin.IsNone && s.Source.IsNone)
    if not missing.IsEmpty then
        printfn "\n'origin' is in %d/%d (%d%%) — these are missing it entirely:"
            originPresent n (originPresent * 100 / n)
        for s in missing do
            printfn "  • %s" (SkillName.value s.Name)
```

When this ran against the user's real 19-skill collection, it flagged: **one file uses `source:` instead of `origin:`** (typo); and **one file has neither `origin` nor `source`** (missing). The agent doesn't have to know what's wrong in advance — the schema-frequency lens surfaces it.

### Better still: enforce the rule via a custom schema

If you can declare *up-front* that "every file should have `origin`", let the
library enforce it. `GetRejected()` then directly returns the broken files —
no manual `IsSome` filtering, no `if originCount > 3*n/4` heuristic.

The TP's `Mode = "skill"` only validates `name`/`description`. To require
extra fields, drop into the core API (`Scanner.scanAll`) with a custom schema
composed via the pipe-style builders:

```fsharp
#r "nuget: YamlFrontMatter"

open System.Threading
open YamlFrontMatter.Types
open YamlFrontMatter.Schemas
open YamlFrontMatter.Scanner

// "Skill semantics PLUS my own extra requirements" — chain |> through requireXxx
let mySchema =
    Skill
    |> requireString "origin"

let opts =
    { RootDirectory       = AbsoluteFilePath.createUnsafe Root
      Pattern             = "SKILL.md"
      Parallelism         = 8
      PathQueueCapacity   = 256
      ResultQueueCapacity = 256 }

scanAll mySchema opts CancellationToken.None
|> Seq.iter (function
    | ItemValid raw ->
        printfn "OK:       %s" (AbsoluteFilePath.value raw.Path)
    | ItemRejected (path, failures) ->
        printfn "REJECTED: %s" (AbsoluteFilePath.value path)
        for f in failures do printfn "    • %A" f
    | ItemSkipped (path, reason) ->
        printfn "SKIPPED:  %s — %A" (AbsoluteFilePath.value path) reason)
```

`scanAll` returns a lazy `seq<ScanItem>` over the channel-based parallel
scanner — same streaming under the hood as `scan`, but the consumer doesn't
have to write the `WaitToReadAsync` / `TryRead` loop manually.

Pipe-style builders available in `YamlFrontMatter.Schemas`:

```fsharp
val requireString     : key: string -> FrontMatterSchema -> FrontMatterSchema   // non-empty
val requireInt        : key: string -> FrontMatterSchema -> FrontMatterSchema
val requireFloat      : key: string -> FrontMatterSchema -> FrontMatterSchema
val requireBool       : key: string -> FrontMatterSchema -> FrontMatterSchema
val requireStringList : key: string -> FrontMatterSchema -> FrontMatterSchema
val require           : FieldRequirement -> FrontMatterSchema -> FrontMatterSchema
val allowEmpty        : key: string -> FrontMatterSchema -> FrontMatterSchema    // string-only modifier
```

The pipeline can start from `General` (empty), `Skill` (name+description), or
any existing `Required _`. Result is always `Required _`.

> **Why not the TP for custom schemas?** F# Type Provider static parameters can
> only carry primitive types (string/int/bool) — not `FrontMatterSchema`. So
> the TP's `Mode` is limited to the built-in modes; for arbitrary schemas, use
> `Scanner.scanAll` directly as above.

## Pattern: nested-mapping access

When a field is a nested mapping (e.g. `metadata: { author: ..., sources: ... }`), the TP generates a **companion record type** for it. Access it through `Option.bind`:

```fsharp
Collection.GetAll()
|> Seq.choose (fun s ->
    s.Metadata |> Option.bind (fun m -> m.Author |> Option.map (fun a -> SkillName.value s.Name, a)))
|> Seq.groupBy snd
|> Seq.sortByDescending (fun (_, items) -> Seq.length items)
|> Seq.iter (fun (author, items) ->
    let names = items |> Seq.map fst |> String.concat ", "
    printfn "  %s: %s" author names)
```

The companion type's name is `Collection.FrontMatterDefinition.MetadataData` — derived as `<FieldName>Data`. The actual name shows up in the `Describe()` output; use it directly.

## Pattern: surface rejected and skipped files

> "Which files in this collection are *trying* to be skills but failing?"

In `Mode = "skill"`, `GetAll()` returns only files that pass validation — files
missing `name` or `description` are silently filtered out. To find those broken
files, use the companion methods:

```fsharp
type Skills = FrontMatterProvider<Root, Mode = "skill">

// Files that have front matter but failed schema validation.
// Each entry carries the precise per-field failure list.
for r in Skills.GetRejected() do
    printfn "REJECTED: %s" (AbsoluteFilePath.value r.Path)
    for failure in r.Failures do
        printfn "  • %A" failure

// Files that aren't front-matter documents at all (no `---` block, broken
// yaml, IO error). Distinct from rejected — these aren't *trying* to be valid.
for s in Skills.GetSkipped() do
    printfn "SKIPPED:  %s — %A" (AbsoluteFilePath.value s.Path) s.Reason
```

This is the single highest-value pattern for "audit my collection" requests:
`GetRejected()` directly returns the list of files the user almost certainly
wants to fix.

In `Mode = "general"`, schema validation has no requirements, so `GetRejected()`
is always empty. `GetSkipped()` is still useful — it surfaces files that
weren't parseable as front matter at all.

## Pattern: single-file skill identification

> "Here's one file that's *probably* a skill — give me its name, or tell me
> exactly why it isn't."

For a single-file question (not directory-wide), use `Skill.tryReadSkillIdentity`
from the core library — no Type Provider needed:

```fsharp
#r "nuget: YamlFrontMatter"            // Core library; no TP required for this

open YamlFrontMatter.Types
open YamlFrontMatter.Skill

let path = AbsoluteFilePath.createUnsafe "/some/file/that/might/be/a/skill.md"

match tryReadSkillIdentity path with
| Ok identity ->
    printfn "It's a skill: %s — %s"
        (SkillName.value identity.Name)
        (SkillDescription.value identity.Description)

| Error NoFrontMatter        -> printfn "Not a skill — no front matter"
| Error NameMissing          -> printfn "Looks broken — missing 'name'"
| Error NameEmpty            -> printfn "Looks broken — 'name' is empty"
| Error (NameNotString actual) -> printfn "'name' is not a string: %A" actual
| Error problem              -> printfn "Other failure: %A" problem
```

Use `tryReadSkillIdentity` (not `tryRead Skill` from `FrontMatterReader`) when:
- The question is specifically "is this a *skill*?" with skill-shaped error
  reporting.
- The caller wants typed `SkillName` / `SkillDescription` directly, not a raw
  `RawFrontMatter` to project from.

Composes cleanly: `tryReadSkillIdentity path |> Result.map (fun id -> id.Name)`
gives `Result<SkillName, SkillReadProblem>` — useful when the agent only cares
about the name.

## Pattern: full audit (one-shot)

For an unfamiliar collection, generate a complete audit in one pass before any targeted queries. See [examples/audit.fsx](examples/audit.fsx) — it produces:

- The full inferred schema (via `Describe()`)
- A table of skills per top-level subfolder
- ASCII-bar saturation chart for each optional field
- "Common but not universal" anomalies — likely typos / missing fields
- Versioning-style consistency check (`1` / `2` vs `2.1.1` etc)
- Authorship distribution from `metadata.author`
- URL-host distribution from `origin` / `source`

This is a useful first-step dump for a new collection — strictly more informative than just `Describe()`.

---

## Worked example: starting cold on a collection

Concrete walkthrough — the user says **"audit my skill collection at `/some/path/skills`"**. The agent's flow:

1. Run [`describe.fsx`](examples/describe.fsx) (substitute `Root`). Reads schema; identifies optional fields and their occurrence counts.
2. Run [`audit.fsx`](examples/audit.fsx) (substitute `Root`). Reads the consolidated audit.
3. **Inspect the audit output** for surprises:
   - Field saturations between 75–99% → "missing in some, should probably be everywhere"
   - Field saturations under 25% → "rare, probably intentional or a typo"
   - Pairs of fields with similar meaning (`origin`/`source`, `tag`/`tags`) → likely the same concept
4. Report findings to the user as a **bullet list**, citing skill names and what's wrong with each.
5. If the user says "fix them" — generate a separate per-file edit list. Don't auto-edit; this skill is **read-only analysis**.

---

## Common gotchas

| Symptom | Cause | Fix |
|---|---|---|
| `error FS0267: This is not a valid constant expression` on `[<Literal>]` | Path was a `let`-bound string, not a literal | Use `[<Literal>] let Root = "/absolute/path"`. The TP requires a compile-time-known string. |
| `The path '...' is not absolute` or "Directory does not exist" at design time | Path is relative or has a trailing slash issue | Use the absolute form. F# scripts run from CWD; the path is resolved against the script's *design-time* compilation, not its runtime working directory. |
| `The type 'SkillDefinition' is not defined` | Tried to use the comment-header name from `Describe()` | The actual provided record type is `Collection.FrontMatterDefinition`, not `Collection.SkillDefinition`. |
| `Lookup on object of indeterminate type` on `s.SomeField` | F# can't infer the type of `s` in a higher-order helper | Annotate the lambda parameter: `(fun (s: Collection.FrontMatterDefinition) -> ...)`. |
| Schema is unexpectedly empty / all-string | The `Pattern` static parameter doesn't match the files | Default is `"SKILL.md"`. Pass `Pattern = "*.md"` or whatever fits: `FrontMatterProvider<Root, Pattern = "*.md">`. |
| Long compile time when collection has thousands of files | TP scans the whole directory at design time | Acceptable for analysis; if it becomes painful, narrow the pattern or split the collection into subdirs and provide each as a separate `FrontMatterProvider` instance. |
| `Could not load assembly … YamlFrontMatter.TypeProvider.DesignTime` | Old (broken) cached version of the package, or pre-0.9.4 published versions which had a packaging bug | Delete `~/.nuget/packages/yamlfrontmatter.typeprovider/<old-version>/`, pin a known-good version, or rerun `dotnet nuget locals all --clear` if cache state is suspect. |

## Output formats the agent should consider

When reporting findings to the user, default to **what the user asked for**:

- "list X" → bullet list, terse
- "table" / "report" → markdown table
- "csv" / "spreadsheet" → CSV (use `String.concat ","` over the rows, escape commas)
- "I want to write F# code on top" → leave the `Describe()` output intact; that's their starting type definition

Don't always reach for full audit. If the user's question is "which skills declare a license", just answer that. The audit pattern is for cold-start analysis, not every query.

## Examples in this folder

| File | Purpose |
|---|---|
| [examples/describe.fsx](examples/describe.fsx) | Print the inferred schema. Always start here on an unknown collection. |
| [examples/count_by_category.fsx](examples/count_by_category.fsx) | Group by top-level subfolder; prints counts and member lists. |
| [examples/find_outliers.fsx](examples/find_outliers.fsx) | Surface data-quality issues post-hoc: rare fields, alternate spellings, missing universal fields. TP-based. |
| [examples/audit.fsx](examples/audit.fsx) | Consolidated one-shot report. Schema + categories + saturation + anomalies + versioning + authorship + URL hosts. |
| [examples/enforce_origin.fsx](examples/enforce_origin.fsx) | Schema-driven version: declare `origin` required upfront via `Skill \|> requireString "origin"`, let `Scanner.scanAll` surface broken files via `ItemRejected`. |

All four were validated against a real 19-skill collection during this skill's authoring; outputs are real and reproducible.

## Style note

When generating these scripts on the fly, follow the [`fsharp-style`](../fsharp-style/SKILL.md) skill — single-case DU unwrapping via the canonical pattern, `task { }`-style async if any IO is involved, `Result` over exceptions for collection errors, idiomatic `Seq.choose` / `Option.bind` over imperative loops.
