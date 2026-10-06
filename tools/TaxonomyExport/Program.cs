// Developer-only tool: regenerates src/ArbetsWatch.Core/Places/places.json from the JobTech Taxonomy API.
//
// Usage (from the repository root):
//   dotnet run --project tools/TaxonomyExport -- [--out <path>] [--allow-count-change]
//
// Sweden (i46j_HmG_v64) is traversed through its "narrower" region and municipality relations, pinned to the
// latest published taxonomy version. The expected counts are 21 regions and 290 municipalities; a different
// count stops the export unless --allow-count-change is given, so a change is investigated, not shipped silently.
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

const string SwedenId = "i46j_HmG_v64";
const string Api = "https://taxonomy.api.jobtechdev.se/v1/taxonomy";
const int SchemaVersion = 1;
const int ExpectedRegions = 21;
const int ExpectedMunicipalities = 290;

var outPath = Path.Combine("src", "ArbetsWatch.Core", "Places", "places.json");
var allowCountChange = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        case "--allow-count-change":
            allowCountChange = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("ArbetsWatch-TaxonomyExport/0.1");

// Latest published version, so the export is reproducible and records what it used.
var versions = JsonNode.Parse(await http.GetStringAsync($"{Api}/main/versions"))!.AsArray();
var version = versions.Max(v => v!["taxonomy/version"]!.GetValue<int>());
Console.WriteLine($"Taxonomy version {version}");

var query = $$"""
    query {
      concepts(id: "{{SwedenId}}", version: "{{version}}") {
        id preferred_label
        narrower(type: "region") {
          id preferred_label national_nuts_level_3_code_2019
          narrower(type: "municipality") { id preferred_label lau_2_code_2015 }
        }
      }
    }
    """;
var response = JsonNode.Parse(await http.GetStringAsync($"{Api}/graphql?query={Uri.EscapeDataString(query)}"))!;
if (response["errors"] is JsonArray errors && errors.Count > 0)
{
    Console.Error.WriteLine($"GraphQL errors: {errors.ToJsonString()}");
    return 1;
}

var sweden = response["data"]!["concepts"]!.AsArray().Single()!;
var sv = CultureInfo.GetCultureInfo("sv-SE");

var regions = new JsonArray();
var municipalityCount = 0;
foreach (var region in sweden["narrower"]!.AsArray().OfType<JsonNode>()
             .OrderBy(r => Code(r, "national_nuts_level_3_code_2019"), StringComparer.Ordinal))
{
    var regionId = Text(region, "id");
    var municipalities = new JsonArray();
    foreach (var m in region["narrower"]!.AsArray().OfType<JsonNode>()
                 .OrderBy(m => Code(m, "lau_2_code_2015"), StringComparer.Ordinal)
                 .ThenBy(m => Text(m, "preferred_label"), StringComparer.Create(sv, false)))
    {
        municipalities.Add(new JsonObject
        {
            ["id"] = Text(m, "id"),
            ["label"] = Text(m, "preferred_label"),
            // Codes are strings so leading zeroes (e.g. "0114") survive.
            ["code"] = Code(m, "lau_2_code_2015"),
            ["regionId"] = regionId,
        });
        municipalityCount++;
    }

    regions.Add(new JsonObject
    {
        ["id"] = regionId,
        ["label"] = Text(region, "preferred_label"),
        ["code"] = Code(region, "national_nuts_level_3_code_2019"),
        ["countryId"] = SwedenId,
        ["municipalities"] = municipalities,
    });
}

Console.WriteLine($"{regions.Count} regions, {municipalityCount} municipalities");
if ((regions.Count != ExpectedRegions || municipalityCount != ExpectedMunicipalities) && !allowCountChange)
{
    Console.Error.WriteLine(
        $"Expected {ExpectedRegions} regions and {ExpectedMunicipalities} municipalities. Investigate the change, " +
        "then rerun with --allow-count-change and update the expected counts in the tests.");
    return 1;
}

var document = new JsonObject
{
    ["schemaVersion"] = SchemaVersion,
    ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    ["taxonomyVersion"] = version,
    ["source"] = $"{Api}/graphql",
    ["country"] = new JsonObject { ["id"] = SwedenId, ["label"] = Text(sweden, "preferred_label") },
    ["regions"] = regions,
};

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await File.WriteAllTextAsync(outPath, document.ToJsonString(options).ReplaceLineEndings("\n") + "\n");
Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}");
return 0;

static string Text(JsonNode node, string name) =>
    node[name]?.GetValue<string>() is { Length: > 0 } value
        ? value
        : throw new InvalidDataException($"Missing '{name}' in {node.ToJsonString()}");

static string Code(JsonNode node, string name) => Text(node, name);
