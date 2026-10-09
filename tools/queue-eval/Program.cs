using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;

if (args.Length != 3)
    throw new ArgumentException("Usage: QueueEval <state.json> <fingerprint-cache.json> <report.json>");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
options.Converters.Add(new JsonStringEnumConverter());
var stateBytes = File.ReadAllBytes(args[0]);
using var document = JsonDocument.Parse(stateBytes);
var root = document.RootElement;
var settings = root.GetProperty("settings");
var profile = Enum.Parse<SimilarityProfile>(settings.GetProperty("similarityProfile").GetString()!);
var custom = settings.TryGetProperty("customSimilarityThresholds", out var limits) && limits.ValueKind == JsonValueKind.Object;
var minimum = custom ? limits.GetProperty("minimumPercent").GetDouble() / 100 : ImageMatcher.GetProfile(profile).MinimumScore;
var fingerprints = File.Exists(args[1])
    ? JsonSerializer.Deserialize<Dictionary<string, ImageFingerprint>>(File.ReadAllText(args[1]), options)!
    : new Dictionary<string, ImageFingerprint>(StringComparer.OrdinalIgnoreCase);
var decoder = new ImageDecoder();
var totalTimer = Stopwatch.StartNew();
var decodeTimer = new Stopwatch();
var errors = new List<string>();
var rows = new List<object>();
var csv = new List<string> { "IncomingPath,ArchivePath,Label,SavedScore,RecomputedScore,AcceptedAtConfiguredMinimum,MeasurementMilliseconds" };
var pairCount = 0;
var labels = new Dictionary<string, string>
{
    ["c8eab5f6-479e-48e4-aeff-7fa333eb8c58"] = "useful-near-duplicate",
    ["8556af05-4363-4f6e-bd3a-33752162daa3"] = "different-text-variant",
    ["e033e29b-389f-43e3-a389-d79f70419c39"] = "useful-near-duplicate",
    ["77094b46-0d38-4f93-8a38-81365c1932f5"] = "unrelated",
    ["e6fcb90d-643e-4de2-bdbf-12fd03226f5b"] = "unrelated",
    ["655397ab-4d80-41d7-b17b-fc0a6795e40d"] = "unrelated",
    ["5c37275e-3d9a-4f0c-aacf-79b4d5b04b7a"] = "unrelated",
};
var labelArchives = new Dictionary<string, string>
{
    ["c8eab5f6-479e-48e4-aeff-7fa333eb8c58"] = "30a5fe46-5cfb-4d73-8243-e39bfa0737a7",
    ["8556af05-4363-4f6e-bd3a-33752162daa3"] = "34585e8b-442a-425d-9452-d50ea1deb9fe",
    ["e033e29b-389f-43e3-a389-d79f70419c39"] = "a2e1471d-a9e5-4a49-aa69-af1ca304631f",
    ["77094b46-0d38-4f93-8a38-81365c1932f5"] = "12f76156-ac47-44aa-8f9c-d85f43ee53bc",
    ["e6fcb90d-643e-4de2-bdbf-12fd03226f5b"] = "5d87e8a0-51ff-488a-8e3d-089de5185f2e",
    ["655397ab-4d80-41d7-b17b-fc0a6795e40d"] = "ad4b992f-e284-4f3b-9452-25c6c242bb33",
    ["5c37275e-3d9a-4f0c-aacf-79b4d5b04b7a"] = "ad4b992f-e284-4f3b-9452-25c6c242bb33",
};
var holdout = new Dictionary<string, (string Archive, string Label)>
{
    ["c3bf483b-4fb0-4471-823b-847a78c3a443"] = ("a635f4d4-b2c0-441b-9acf-f7ae47e36bf9", "unrelated"),
    ["d235f9fd-7680-4997-89d9-ee95c9b3c5eb"] = ("419ce04f-dd1f-43a2-ab35-081c3096575c", "unrelated"),
    ["6b841323-b11c-46d5-9cab-72c9740ed440"] = ("9c26113c-561b-441d-a9cf-88fd11d014cf", "unrelated"),
    ["19807060-5dd3-4102-845d-a8d30d65aa05"] = ("304aa628-5137-48b2-80ca-87c0bd009013", "unrelated"),
    ["3473e25d-80d4-42a3-9c3f-292579771458"] = ("ca0f3821-528d-46bd-bd22-3ae25fb99c4e", "useful-near-duplicate"),
    ["6743c285-3fc7-4501-9ea9-ba4c9af4c719"] = ("9c26113c-561b-441d-a9cf-88fd11d014cf", "unrelated"),
};
foreach (var review in root.GetProperty("reviewQueue").EnumerateArray())
{
    var incomingPath = review.GetProperty("heldFilePath").GetString()!;
    var incoming = review.TryGetProperty("incomingImageFingerprint", out var saved) && saved.ValueKind == JsonValueKind.Object
        ? saved.Deserialize<ImageFingerprint>(options)! : await Fingerprint(incomingPath);
    if (incoming is null) continue;
    var rank = 0;
    foreach (var candidate in review.GetProperty("candidates").EnumerateArray())
    {
        pairCount++;
        rank++;
        var path = candidate.GetProperty("archivePath").GetString()!;
        var fingerprint = await Fingerprint(path);
        if (fingerprint is null) continue;
        var timer = Stopwatch.StartNew();
        var measurement = ImageMatcher.MeasureSimilarity(incoming, fingerprint);
        var measurementMilliseconds = timer.Elapsed.TotalMilliseconds;
        var candidates = new[] { new ImageCandidate(path, fingerprint) };
        var accepted = ImageMatcher.RankCandidates(incoming, candidates, profile, custom ? minimum : null).Count != 0;
        var profiles = Enum.GetValues<SimilarityProfile>().ToDictionary(p => p.ToString(), p => ImageMatcher.RankCandidates(incoming, candidates, p).Count != 0);
        var label = labels.FirstOrDefault(p => incomingPath.Contains(p.Key, StringComparison.OrdinalIgnoreCase)
            && path.Contains(labelArchives[p.Key], StringComparison.OrdinalIgnoreCase)).Value;
        var holdoutLabel = holdout.FirstOrDefault(p => incomingPath.Contains(p.Key, StringComparison.OrdinalIgnoreCase)
            && path.Contains(p.Value.Archive, StringComparison.OrdinalIgnoreCase)).Value.Label;
        var split = label is not null ? "development" : holdoutLabel is not null ? "holdout" : "unlabelled";
        label ??= holdoutLabel;
        rows.Add(new { IncomingPath = incomingPath, ArchivePath = path, Rank = rank, Label = label, Split = split, SavedScore = candidate.GetProperty("similarityScore").GetDouble(), RecomputedScore = measurement.Score, measurement.Components, AcceptedAtConfiguredMinimum = accepted, DefaultProfiles = profiles, MeasurementMilliseconds = measurementMilliseconds });
        csv.Add(string.Join(",", new[] { incomingPath, path, label ?? "", candidate.GetProperty("similarityScore").GetRawText(), measurement.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture), accepted.ToString(), measurementMilliseconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) }.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")));
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(fingerprints, options));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
File.WriteAllText(args[2], JsonSerializer.Serialize(new { StateSha256 = Convert.ToHexString(SHA256.HashData(stateBytes)), GeneratedUtc = DateTimeOffset.UtcNow, Profile = profile, Custom = custom, Minimum = minimum, PairCount = pairCount, Evaluated = rows.Count, DecodeMilliseconds = decodeTimer.Elapsed.TotalMilliseconds, TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds, Errors = errors, Pairs = rows }, options));
File.WriteAllLines(Path.ChangeExtension(args[2], ".csv"), csv);
Console.WriteLine($"Evaluated {rows.Count}/{pairCount}; errors {errors.Count}; report {args[2]}");

async Task<ImageFingerprint?> Fingerprint(string path)
{
    if (fingerprints.TryGetValue(path, out var fingerprint)) return fingerprint;
    decodeTimer.Start();
    try
    {
        var result = await decoder.DecodeAsync(path);
        if (result.Image is null) { errors.Add($"{path}: {result.Failure}"); return null; }
        fingerprint = ImageFingerprint.Create(result.Image);
        fingerprints[path] = fingerprint;
        return fingerprint;
    }
    catch (Exception exception) { errors.Add($"{path}: {exception.Message}"); return null; }
    finally { decodeTimer.Stop(); }
}
