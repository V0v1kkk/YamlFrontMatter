using System.Threading.Channels;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using YamlFrontMatter;
using static YamlFrontMatter.Types;
using static YamlFrontMatter.Schemas;
using static YamlFrontMatter.Scanner;
using static YamlFrontMatter.SchemaInference;
using static YamlFrontMatter.FrontMatterReader;
using static YamlFrontMatter.Skill;

var skillsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Skills"));

Console.WriteLine("=== YamlFrontMatter — C# Interop Demo ===");
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 1. Single-file read with schema validation: FrontMatterReader.tryRead
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 1. Read a single file with schema validation ──");
Console.WriteLine();

var complexPath = AbsoluteFilePathModule.createUnsafe(
    Path.Combine(skillsDir, "complex", "SKILL.md"));

// `tryRead` takes a FrontMatterSchema. `Skill` requires non-empty name+description.
// Use `General` if you want to read any front matter without enforcing fields.
var readResult = FrontMatterReader.tryRead(FrontMatterSchema.Skill, complexPath);

if (readResult.IsOk)
{
    var data = readResult.ResultValue;
    Console.WriteLine($"  Path: {data.Path.Value}");
    Console.WriteLine("  Fields:");
    PrintFields(data.Fields, indent: 4);
}
else
{
    Console.WriteLine($"  [{FormatReadProblem(readResult.ErrorValue)}]");
}
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Skill-identity API: focused "is this a skill, and what's its name?"
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 2. tryReadSkillIdentity — typed name+description in one call ──");
Console.WriteLine();

var skillIdResult = Skill.tryReadSkillIdentity(complexPath);
if (skillIdResult.IsOk)
{
    var id = skillIdResult.ResultValue;
    Console.WriteLine($"  Name:        {id.Name.Value}");
    Console.WriteLine($"  Description: {id.Description.Value}");
}
else
{
    Console.WriteLine($"  Not a skill: {FormatSkillReadProblem(skillIdResult.ErrorValue)}");
}
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 3. Schema inference — discover the union schema across all files
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 3. Schema Inference ──");
Console.WriteLine();

var report = SchemaInference.discoverSchemaWithStats(skillsDir, "SKILL.md");
var schemaText = SchemaInference.formatSchema(report);

Console.WriteLine($"  Files scanned: {report.FilesScanned}");
Console.WriteLine("  Inferred F# record type:");
Console.WriteLine();
foreach (var line in schemaText.Split('\n'))
    Console.WriteLine($"    {line}");
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 4. Streaming scanner — schema-aware, three buckets (Valid / Rejected / Skipped)
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 4. Channel-based streaming scanner ──");
Console.WriteLine();

var scanOptions = new ScanOptions(
    rootDirectory: AbsoluteFilePathModule.createUnsafe(skillsDir),
    pattern: "SKILL.md",
    parallelism: 4,
    pathQueueCapacity: 64,
    resultQueueCapacity: 64);

using var cts = new CancellationTokenSource();
ChannelReader<ScanItem> reader = Scanner.scan(FrontMatterSchema.Skill, scanOptions, cts.Token);

var validCount = 0;
var rejectedCount = 0;
var skippedCount = 0;

while (reader.WaitToReadAsync(cts.Token).AsTask().GetAwaiter().GetResult())
{
    while (reader.TryRead(out var item))
    {
        switch (item)
        {
            case ScanItem.ItemValid v:
                validCount++;
                var nameKey = YamlKey.NewYamlKey("name");
                var name = MapModule.TryFind(nameKey, v.Item.Fields);
                var displayName = FSharpOption<YamlValue>.get_IsSome(name)
                    ? FormatYamlValue(name.Value)
                    : "(unnamed)";
                Console.WriteLine($"  [VALID]    {displayName} — {v.Item.Path.Value}");
                break;

            case ScanItem.ItemRejected r:
                rejectedCount++;
                Console.WriteLine($"  [REJECTED] {r.path.Value}");
                foreach (var f in r.failures)
                    Console.WriteLine($"             • {FormatValidationFailure(f)}");
                break;

            case ScanItem.ItemSkipped s:
                skippedCount++;
                Console.WriteLine($"  [SKIPPED]  {s.path.Value}: {FormatSkipReason(s.reason)}");
                break;
        }
    }
}

Console.WriteLine();
Console.WriteLine($"  Totals — valid={validCount} rejected={rejectedCount} skipped={skippedCount}");
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 5. YamlValue pattern matching from C#
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 5. Pattern matching YamlValue from C# ──");
Console.WriteLine();

if (readResult.IsOk)
{
    var fields = readResult.ResultValue.Fields;

    var activeKey = YamlKey.NewYamlKey("active");
    var priorityKey = YamlKey.NewYamlKey("priority");
    var tagsKey = YamlKey.NewYamlKey("tags");
    var metaKey = YamlKey.NewYamlKey("metadata");

    var active = MapModule.TryFind(activeKey, fields);
    if (FSharpOption<YamlValue>.get_IsSome(active) && active.Value is YamlValue.YBool b)
        Console.WriteLine($"  active = {b.Item}");

    var priority = MapModule.TryFind(priorityKey, fields);
    if (FSharpOption<YamlValue>.get_IsSome(priority) && priority.Value is YamlValue.YInt i)
        Console.WriteLine($"  priority = {i.Item}");

    var tags = MapModule.TryFind(tagsKey, fields);
    if (FSharpOption<YamlValue>.get_IsSome(tags) && tags.Value is YamlValue.YList list)
    {
        var tagStrings = list.Item.Select(v => v is YamlValue.YString s ? s.Item : v.ToString());
        Console.WriteLine($"  tags = [{string.Join(", ", tagStrings)}]");
    }

    var metadata = MapModule.TryFind(metaKey, fields);
    if (FSharpOption<YamlValue>.get_IsSome(metadata) && metadata.Value is YamlValue.YMap map)
    {
        Console.WriteLine("  metadata:");
        foreach (var kv in map.Item)
            Console.WriteLine($"    {kv.Key.Value} = {FormatYamlValue(kv.Value)}");
    }
}

Console.WriteLine();
Console.WriteLine("── Done! ──");

// ─────────────────────────────────────────────────────────────────────────────
// Formatting helpers
// ─────────────────────────────────────────────────────────────────────────────

static void PrintFields(FSharpMap<YamlKey, YamlValue> fields, int indent)
{
    var pad = new string(' ', indent);
    foreach (var kv in fields)
        Console.WriteLine($"{pad}{kv.Key.Value}: {FormatYamlValue(kv.Value)}");
}

static string FormatYamlValue(YamlValue value) => value switch
{
    YamlValue.YString s => s.Item,
    YamlValue.YBool b => b.Item.ToString(),
    YamlValue.YInt i => i.Item.ToString(),
    YamlValue.YFloat f => f.Item.ToString("G"),
    YamlValue.YList l => $"[{string.Join(", ", l.Item.Select(FormatYamlValue))}]",
    YamlValue.YMap m => $"{{{string.Join(", ", m.Item.Select(kv => $"{kv.Key.Value}: {FormatYamlValue(kv.Value)}"))}}}",
    _ => value.ToString() ?? ""
};

static string FormatValidationFailure(ValidationFailure f) => f switch
{
    ValidationFailure.MissingField mf => $"missing required field: {mf.Item.Value}",
    ValidationFailure.EmptyString es => $"empty required string: {es.Item.Value}",
    ValidationFailure.WrongType wt => $"field '{wt.key.Value}' has wrong type (expected {wt.expected})",
    ValidationFailure.UnknownField uf => $"unknown field: {uf.Item.Value}",
    ValidationFailure.InvalidFormat ifmt => $"invalid format for '{ifmt.key.Value}': {ifmt.detail}",
    ValidationFailure.InvalidEmbeddedMetadata iem => $"invalid embedded metadata in '{iem.key.Value}': {iem.detail}",
    _ => f.ToString() ?? ""
};

// F# DU cases without payload are singleton values (Is<Case> properties),
// while cases with payload are nested types. The patterns below show both.

static string FormatSkipReason(SkipReason r)
{
    if (r.IsNoFrontMatter)       return "no front matter";
    if (r.IsUnclosedFrontMatter) return "front matter unclosed";
    if (r is SkipReason.YamlMalformed m) return $"yaml malformed: {m.detail}";
    if (r is SkipReason.IoFailure io)    return $"IO error: {io.message}";
    return r.ToString() ?? "";
}

static string FormatReadProblem(ReadProblem p)
{
    if (p.IsNoFrontMatter)       return "no front matter";
    if (p.IsUnclosedFrontMatter) return "front matter unclosed";
    if (p is ReadProblem.YamlMalformed m) return $"yaml malformed: {m.detail}";
    if (p is ReadProblem.IoFailure io)    return $"IO error: {io.message}";
    if (p is ReadProblem.ValidationFailed v)
        return "validation failed: " + string.Join("; ", v.failures.Select(FormatValidationFailure));
    return p.ToString() ?? "";
}

static string FormatSkillReadProblem(SkillReadProblem p)
{
    if (p.IsNoFrontMatter)       return "no front matter (not a skill)";
    if (p.IsUnclosedFrontMatter) return "front matter unclosed";
    if (p is SkillReadProblem.YamlMalformed m) return $"yaml malformed: {m.detail}";
    if (p.IsNameMissing)         return "missing 'name'";
    if (p.IsNameEmpty)           return "'name' is empty";
    if (p is SkillReadProblem.NameNotString ns) return $"'name' is not a string ({ns.actual})";
    if (p.IsDescriptionMissing)  return "missing 'description'";
    if (p.IsDescriptionEmpty)    return "'description' is empty";
    if (p is SkillReadProblem.DescriptionNotString ds) return $"'description' is not a string ({ds.actual})";
    if (p is SkillReadProblem.IoFailure io) return $"IO error: {io.message}";
    return p.ToString() ?? "";
}
