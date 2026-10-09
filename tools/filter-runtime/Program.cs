using System.Security.Cryptography;
using System.Text.Json;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Models;

// Local validation only. Operates solely on a newly created, explicitly supplied disposable root.
var mode = args[0];
var root = Path.GetFullPath(args[1]);
if (!Path.GetFileName(root).StartsWith("VrcPicSorter-filter-proof-", StringComparison.Ordinal))
    throw new ArgumentException("A uniquely named VrcPicSorter-filter-proof-* root is required.");
var manifestPath = Path.Combine(root, "manifest.json");
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
if (mode == "prepare")
{
    if (Directory.Exists(root)) throw new IOException("Use a fresh evidence directory.");
    var originals = Path.GetFullPath(args[2]);
    var files = Directory.GetFiles(originals, "*.png", SearchOption.AllDirectories);
    string Find(string prefix) => files.Single(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal));
    var framed = Find("￰_2026-10-03_20-23-10.000_");
    var unframed = Find("￰_2026-10-03_20-23-10.388_");
    var white = Find("ʚ moonlight ɞ_2025-12-10_23-44-20.119_");
    var uncertain = Find("VastHypz_2026-04-27_04-08-07.563_");
    Directory.CreateDirectory(root);
    using var runtime = new AppRuntime(Path.Combine(root, "data"), false);
    await runtime.InitializeAsync();
    var player = "FilterProof" + Guid.NewGuid().ToString("N");
    await runtime.StateStore.UpdateAsync(state =>
    {
        state.Settings.OutputRootPath = Path.Combine(root, "archive");
        state.Settings.OutputRootConfirmed = true;
        state.Settings.RemovePrintFrame = true; state.Settings.RemoveMyImages = true; state.Settings.PlayerName = player;
        state.Settings.ScanWorkers = 1;
        state.Settings.OrganizationPolicy = OrganizationPolicy.CategoryRoot;
        state.Settings.Automation.WatchMode = WatchMode.OnInterval;
        state.Settings.Automation.WatchScanSeconds = 15;
        foreach (var mapping in state.Settings.CategoryMappings)
        {
            mapping.SourcePath = Path.Combine(root, "incoming", mapping.Category.ToString());
            mapping.ArchivePath = Path.Combine(root, "archive", mapping.Category.ToString());
            mapping.IsEnabled = true;
            Directory.CreateDirectory(mapping.SourcePath); Directory.CreateDirectory(mapping.ArchivePath);
        }
        return true;
    });
    var manifest = new List<ProofFile>();
    void Copy(string original, VrcImageCategory category, string name, string expected, bool archive = false)
    {
        var path = Path.Combine(root, archive ? "archive" : "incoming", category.ToString(), name);
        File.Copy(original, path);
        manifest.Add(new(path, Hash(path), category, expected));
    }
    const string id = "de65e61a-b986-4072-ac4c-c4ed590d01f5";
    Copy(framed, VrcImageCategory.Prints, Path.GetFileName(framed), "recycled");
    Copy(unframed, VrcImageCategory.Prints, Path.GetFileName(unframed), "archived");
    Copy(uncertain, VrcImageCategory.Prints, Path.GetFileName(uncertain), "review");
    Copy(unframed, VrcImageCategory.Emoji, $"{player}_inv_{id}.png", "recycled");
    Copy(white, VrcImageCategory.Emoji, $"{player.ToLowerInvariant()}_inv_{Guid.NewGuid()}.png", "archived");
    Copy(unframed, VrcImageCategory.Stickers, $"{player}_2026-10-03_20-23-10_inv_{id}.png", "recycled");
    Copy(white, VrcImageCategory.Stickers, $"{player}Extra_2026-10-03_20-23-10_inv_{id}.png", "archived");
    Copy(framed, VrcImageCategory.Prints, "ArchiveOnly_" + Path.GetFileName(framed), "archive-filter", true);
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, jsonOptions));
    Console.WriteLine(JsonSerializer.Serialize(new { Prepared = manifest.Count, DataDirectory = runtime.StateDirectory, Player = player }));
}
else if (mode is "verify" or "verify-archive" or "verify-kept")
{
    var manifest = JsonSerializer.Deserialize<List<ProofFile>>(await File.ReadAllTextAsync(manifestPath))!;
    using var runtime = new AppRuntime(Path.Combine(root, "data"), false);
    var state = await runtime.StateStore.LoadAsync();
    var results = new List<object>();
    foreach (var file in manifest)
    {
        var expected = file.Expected == "archive-filter" && mode != "verify" ? "recycled"
            : file.Expected == "review" && mode == "verify-kept" ? "archived" : file.Expected;
        var target = expected == "archived" ? Path.Combine(root, "archive", file.Category.ToString(), Path.GetFileName(file.Source)) : file.Source;
        var valid = expected switch
        {
            "recycled" => !File.Exists(file.Source) && RecycledHash(file.Source) == file.Hash,
            "review" => File.Exists(target) && Hash(target) == file.Hash && state.ReviewQueue.Any(r => r.HeldFilePath == target && r.FilterReview is not null && r.Status == ReviewStatus.Pending),
            _ => File.Exists(target) && Hash(target) == file.Hash
        };
        results.Add(new { file.Source, Expected = expected, Passed = valid, file.Hash });
        if (!valid) throw new InvalidOperationException($"Inventory/recycle verification failed: {file.Source} expected {expected}");
        if (file.Expected == "review" && mode == "verify-kept" && !state.FilterKeeps.Any(item => item.Path == target))
            throw new InvalidOperationException("Kept image has no persisted filter exception.");
    }
    var report = JsonSerializer.Serialize(new { Mode = mode, Passed = results.Count, Files = results }, jsonOptions);
    await File.WriteAllTextAsync(Path.Combine(root, mode + ".json"), report);
    Console.WriteLine(report);
}
else if (mode == "add-review")
{
    var manifest = JsonSerializer.Deserialize<List<ProofFile>>(await File.ReadAllTextAsync(manifestPath))!;
    var reference = manifest.Single(f => f.Expected == "review");
    var archived = Path.Combine(root, "archive", reference.Category.ToString(), Path.GetFileName(reference.Source));
    var incoming = Path.Combine(Path.GetDirectoryName(reference.Source)!, Path.GetFileNameWithoutExtension(reference.Source) + " (2).png");
    File.Copy(archived, incoming);
    manifest.Add(new(incoming, Hash(incoming), reference.Category, "recycled"));
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, jsonOptions));
    Console.WriteLine("Added one disposable uncertain-frame copy for the Remove button test.");
}
else if (mode == "k")
{
    var networkRoot = Path.GetFullPath(args[2]);
    if (!networkRoot.StartsWith(@"K:\VrcPicSorter-isolated-filter-test-", StringComparison.OrdinalIgnoreCase) || Directory.Exists(networkRoot))
        throw new ArgumentException("Use a fresh isolated K: test directory.");
    var reference = JsonSerializer.Deserialize<List<ProofFile>>(await File.ReadAllTextAsync(manifestPath))!.First(f => f.Expected == "archived");
    var archivedReference = Path.Combine(root, "archive", reference.Category.ToString(), Path.GetFileName(reference.Source));
    Directory.CreateDirectory(networkRoot);
    using var runtime = new AppRuntime(Path.Combine(root, "k-data"), false);
    await runtime.InitializeAsync();
    await runtime.StateStore.UpdateAsync(state =>
    {
        state.Settings.RemoveMyImages = true; state.Settings.PlayerName = "KFilterProof";
        state.Settings.OutputRootPath = Path.Combine(root, "k-archive"); state.Settings.OutputRootConfirmed = true;
        state.Settings.ScanWorkers = 1;
        foreach (var mapping in state.Settings.CategoryMappings)
        {
            mapping.IsEnabled = mapping.Category == VrcImageCategory.Emoji;
            mapping.SourcePath = networkRoot;
            mapping.ArchivePath = Path.Combine(root, "k-archive", mapping.Category.ToString());
            Directory.CreateDirectory(mapping.ArchivePath);
        }
        return true;
    });
    var path = Path.Combine(networkRoot, $"KFilterProof_inv_{Guid.NewGuid()}.png");
    File.Copy(archivedReference, path);
    var expectedHash = Hash(path);
    var result = await runtime.Scanner.ScanCategoryAsync(VrcImageCategory.Emoji);
    if (result.Errors.Count != 0 || result.FilterRecycled != 1 || File.Exists(path) || RecycledHash(path) != expectedHash)
        throw new InvalidOperationException("K: recycling proof failed: " + string.Join(";", result.Errors));
    var report = JsonSerializer.Serialize(new { Passed = true, Source = path, Hash = expectedHash, Result = result }, jsonOptions);
    await File.WriteAllTextAsync(Path.Combine(root, "k-proof.json"), report);
    Console.WriteLine(report);
}
else throw new ArgumentException("Unknown mode.");

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static string? RecycledHash(string source)
{
    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
    dynamic bin = shell.NameSpace(10);
    foreach (dynamic item in bin.Items())
    {
        string name = item.ExtendedProperty("System.FileName");
        if (!string.Equals(name, Path.GetFileName(source), StringComparison.OrdinalIgnoreCase)) continue;
        string deletedFrom = item.ExtendedProperty("System.Recycle.DeletedFrom");
        var original = Path.Combine(deletedFrom, name);
        var originNote = original + ".origin.txt";
        if (string.Equals(original, source, StringComparison.OrdinalIgnoreCase)
            || (File.Exists(originNote) && string.Equals(File.ReadAllText(originNote), source, StringComparison.OrdinalIgnoreCase)))
            return Hash((string)item.Path);
    }
    return null;
}

internal sealed record ProofFile(string Source, string Hash, VrcImageCategory Category, string Expected);
