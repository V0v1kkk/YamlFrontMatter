using System.Threading.Channels;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using YamlFrontMatter;
using static YamlFrontMatter.Types;
using static YamlFrontMatter.Scanner;
using static YamlFrontMatter.SchemaInference;

var skillsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Skills"));

Console.WriteLine("=== YamlFrontMatter — C# Interop Demo ===");
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 1. Read a single file with Scanner.tryReadOne
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 1. Read a single file ──");
Console.WriteLine();

var complexPath = AbsoluteFilePathModule.createUnsafe(
    Path.Combine(skillsDir, "complex", "SKILL.md"));

var readResult = Scanner.tryReadOne(complexPath);

if (readResult.IsOk && FSharpOption<RawSkillData>.get_IsSome(readResult.ResultValue))
{
    var data = readResult.ResultValue.Value;
    Console.WriteLine($"  Path: {data.Path.Value}");
    Console.WriteLine($"  Fields:");
    PrintFields(data.Fields, indent: 4);
}
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Schema inference — discover unified schema across all files
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 2. Schema Inference ──");
Console.WriteLine();

var report = SchemaInference.discoverSchemaWithStats(skillsDir, "SKILL.md");
var schemaText = SchemaInference.formatSchema(report);

Console.WriteLine($"  Files scanned: {report.FilesScanned}");
Console.WriteLine($"  Inferred F# record type:");
Console.WriteLine();
foreach (var line in schemaText.Split('\n'))
    Console.WriteLine($"    {line}");
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 3. Streaming scanner via System.Threading.Channels
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 3. Channel-based streaming scanner ──");
Console.WriteLine();

var scanOptions = new ScanOptions(
    rootDirectory: AbsoluteFilePathModule.createUnsafe(skillsDir),
    pattern: "SKILL.md",
    parallelism: 4,
    pathQueueCapacity: 64,
    resultQueueCapacity: 64);

using var cts = new CancellationTokenSource();
ChannelReader<FSharpResult<FSharpOption<RawSkillData>, ScanError>> reader =
    Scanner.scan(scanOptions, cts.Token);

while (reader.WaitToReadAsync(cts.Token).AsTask().GetAwaiter().GetResult())
{
    while (reader.TryRead(out var result))
    {
        if (result.IsOk)
        {
            var maybeSkill = result.ResultValue;
            if (FSharpOption<RawSkillData>.get_IsSome(maybeSkill))
            {
                var skill = maybeSkill.Value;
                var nameKey = YamlKey.NewYamlKey("name");
                var name = MapModule.TryFind(nameKey, skill.Fields);

                var displayName = FSharpOption<YamlValue>.get_IsSome(name)
                    ? FormatYamlValue(name.Value)
                    : "(unnamed)";

                Console.WriteLine($"  [OK] {displayName} — {skill.Path.Value}");
            }
        }
        else
        {
            Console.WriteLine($"  [ERR] {FormatScanError(result.ErrorValue)}");
        }
    }
}

Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
// 4. Working with YamlValue DU — pattern matching from C#
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("── 4. Pattern matching YamlValue from C# ──");
Console.WriteLine();

if (readResult.IsOk && FSharpOption<RawSkillData>.get_IsSome(readResult.ResultValue))
{
    var fields = readResult.ResultValue.Value.Fields;

    // Extract typed values by key
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
// Helpers
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

static string FormatScanError(ScanError error) => error switch
{
    ScanError.MissingClosingDelimiter e => $"Missing closing delimiter: {e.Item.Value}",
    ScanError.YamlParseFailed e => $"YAML parse failed: {e.Item1.Value} — {e.Item2.Message}",
    ScanError.FileReadFailed e => $"File read failed: {e.Item1.Value} — {e.Item2.Message}",
    _ => error.ToString() ?? ""
};
