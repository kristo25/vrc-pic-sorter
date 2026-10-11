using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using System.Text.Json.Nodes;
using VrcPicSorter.Tests.FileSystem;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Scanning;

public sealed class ArchiveIndexerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDecoderMigrationPreservesBackupAndRevalidatesOnlyMatchingKeepIdentities(bool identityMatches)
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("incoming");
        var archive = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(archive);
        var path = Path.Combine(archive, "protected.png");
        using (var pixels = ImageFixtureFactory.CreatePattern(309)) await pixels.SaveAsPngAsync(path);
        string statePath;
        string fingerprintPath;
        string stateDirectory;
        using (var initial = FileRouterTests.CreateStore(directory, source, archive))
        {
            var indexer = new ArchiveIndexer(initial, new ImageDecoder());
            await indexer.RefreshAsync(VrcImageCategory.Emoji);
            await initial.UpdateAsync(state =>
            {
                var image = Assert.Single(state.ArchiveIndex.Categories.Single(c => c.Category == VrcImageCategory.Emoji).Images);
                var identity = identityMatches ? image.ExactFingerprint : new string('a', 64);
                state.ManualKeeps.Add(new(path, identity, image.FileSize, image.LastWriteUtc));
                state.FilterKeeps.Add(new(path, identity, image.FileSize, image.LastWriteUtc, RemovalFilterReason.PrintFrame));
                image.Fingerprint = image.Fingerprint! with { FeatureVersion = 1 };
                return true;
            });
            statePath = initial.StatePath;
            fingerprintPath = initial.FingerprintPath;
            stateDirectory = initial.StateDirectory;
        }

        var legacyDocument = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
        Assert.True(legacyDocument.Remove("decoderBackend"));
        await File.WriteAllTextAsync(statePath, legacyDocument.ToJsonString());
        var originalState = await File.ReadAllBytesAsync(statePath);
        var originalFingerprints = await File.ReadAllBytesAsync(fingerprintPath);
        using (var migrated = new JsonStateStore(stateDirectory))
        {
            var loaded = await migrated.LoadAsync();
            Assert.Equal(ImageFingerprint.CurrentDecoderBackend, loaded.DecoderBackend);
            Assert.True(Assert.Single(loaded.ManualKeeps).DecoderMigrationUnresolved);
            Assert.True(Assert.Single(loaded.FilterKeeps).DecoderMigrationUnresolved);
            var result = await new ArchiveIndexer(migrated, new ImageDecoder()).RefreshAsync(VrcImageCategory.Emoji);
            Assert.Equal(IndexStatus.Current, result.Status);
            var rebuilt = await migrated.LoadAsync();
            var image = Assert.Single(rebuilt.ArchiveIndex.Categories.Single(c => c.Category == VrcImageCategory.Emoji).Images);
            Assert.True(image.Fingerprint!.HasCurrentFeatures);
            Assert.Equal(!identityMatches, Assert.Single(rebuilt.ManualKeeps).DecoderMigrationUnresolved);
            Assert.Equal(!identityMatches, Assert.Single(rebuilt.FilterKeeps).DecoderMigrationUnresolved);
            Assert.True(ManualKeepRules.IsProtected(rebuilt, image));
            Assert.True(FilterKeepRules.IsKept(rebuilt, path, image.ExactFingerprint, RemovalFilterReason.PrintFrame));
        }

        using (var restarted = new JsonStateStore(stateDirectory))
        {
            var state = await restarted.LoadAsync();
            Assert.Equal(!identityMatches, Assert.Single(state.ManualKeeps).DecoderMigrationUnresolved);
            Assert.Equal(!identityMatches, Assert.Single(state.FilterKeeps).DecoderMigrationUnresolved);
        }
        Assert.Equal(originalState, await File.ReadAllBytesAsync(statePath + ".before-magick"));
        Assert.Equal(originalFingerprints, await File.ReadAllBytesAsync(fingerprintPath + ".before-magick"));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableConfiguredRootPreservesCachedIndex(bool currentUnavailable)
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("incoming");
        var current = directory.GetPath("archive", "Emoji");
        var retained = directory.GetPath("retained");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(retained);
        using var image = ImageFixtureFactory.CreatePattern(207);
        var original = Path.Combine(currentUnavailable ? current : retained, "keeper.png");
        await image.SaveAsPngAsync(original);
        using var store = FileRouterTests.CreateStore(directory, source, current);
        await store.UpdateAsync(state =>
        {
            state.Settings.LegacyArchiveMappings.Add(
            new LegacyArchiveMapping { Category = VrcImageCategory.Emoji, ArchivePath = retained }); return true;
        });
        var indexer = new ArchiveIndexer(store, new ImageDecoder());
        Assert.Equal(IndexStatus.Current, (await indexer.RefreshAsync(VrcImageCategory.Emoji)).Status);
        var unavailable = currentUnavailable ? current : retained;
        Directory.Move(unavailable, unavailable + "-offline");
        var result = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        Assert.Equal(IndexStatus.Unavailable, result.Status);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(original, Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images).Path);
        Directory.Move(unavailable + "-offline", unavailable);
        Assert.Equal(IndexStatus.Current, (await indexer.RefreshAsync(VrcImageCategory.Emoji)).Status);
    }

    [Fact]
    public async Task RefreshReusesPersistedFingerprintForUnchangedFile()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var archived = Path.Combine(archiveRoot, "existing.png");
        using (var image = ImageFixtureFactory.CreatePattern(201))
        {
            await image.SaveAsPngAsync(archived);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var indexer = new ArchiveIndexer(store, new ImageDecoder());
        var first = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var generation = first.Generation;
        var indexedId = Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images).Id;

        await using var locked = new FileStream(archived, FileMode.Open, FileAccess.Read, FileShare.None);
        var second = await indexer.RefreshAsync(VrcImageCategory.Emoji);

        Assert.Equal(IndexStatus.Current, second.Status);
        Assert.Equal(generation, second.Generation);
        Assert.Equal(indexedId, Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshRebuildsAnUnchangedNonCurrentFingerprint(bool corruptCurrentFingerprint)
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var archived = Path.Combine(archiveRoot, "existing.png");
        using (var image = ImageFixtureFactory.CreatePattern(205))
        {
            await image.SaveAsPngAsync(archived);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var indexer = new ArchiveIndexer(store, new ImageDecoder());
        var first = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        await store.UpdateAsync(state =>
        {
            var record = Assert.Single(state.ArchiveIndex.Categories[0].Images);
            record.Fingerprint = corruptCurrentFingerprint
                ? record.Fingerprint! with { PerceptualFrames = [] }
                : record.Fingerprint! with { FeatureVersion = 1 };
            return true;
        });

        var second = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var rebuilt = Assert.Single((await store.LoadAsync()).ArchiveIndex.Categories[0].Images);

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.Equal(ImageFingerprint.CurrentFeatureVersion, rebuilt.Fingerprint!.FeatureVersion);
        Assert.True(rebuilt.Fingerprint.HasCurrentFeatures);
    }

    [Fact]
    public async Task RefreshAddsAndRemovesFilesWithoutReprocessingUnchangedRecords()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var retained = Path.Combine(archiveRoot, "retained.png");
        var removed = Path.Combine(archiveRoot, "removed.png");
        using (var image = ImageFixtureFactory.CreatePattern(202))
        {
            await image.SaveAsPngAsync(retained);
        }

        using (var image = ImageFixtureFactory.CreatePattern(203))
        {
            await image.SaveAsPngAsync(removed);
        }

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var indexer = new ArchiveIndexer(store, new ImageDecoder());
        await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var initial = (await store.LoadAsync()).ArchiveIndex.Categories[0];
        var retainedId = initial.Images.Single(item => item.Path == retained).Id;
        var firstGeneration = initial.Generation;

        File.Delete(removed);
        var added = Path.Combine(archiveRoot, "added.png");
        using (var image = ImageFixtureFactory.CreatePattern(204))
        {
            await image.SaveAsPngAsync(added);
        }

        var result = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var refreshed = (await store.LoadAsync()).ArchiveIndex.Categories[0];

        Assert.Equal(IndexStatus.Current, result.Status);
        Assert.Equal(firstGeneration + 1, result.Generation);
        Assert.Equal(2, refreshed.Images.Count);
        Assert.Equal(retainedId, refreshed.Images.Single(item => item.Path == retained).Id);
        Assert.Contains(refreshed.Images, item => item.Path == added);
        Assert.DoesNotContain(refreshed.Images, item => item.Path == removed);
    }

    [Fact]
    public async Task UnreadableArchiveFilePreventsClaimingCompleteCoverage()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var readable = Path.Combine(archiveRoot, "readable.png");
        using (var image = ImageFixtureFactory.CreatePattern(206))
        {
            await image.SaveAsPngAsync(readable);
        }

        var unreadable = Path.Combine(archiveRoot, "unreadable.png");
        await File.WriteAllBytesAsync(unreadable, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

        using var store = FileRouterTests.CreateStore(directory, sourceRoot, archiveRoot);
        var indexer = new ArchiveIndexer(store, new ImageDecoder());

        var result = await indexer.RefreshAsync(VrcImageCategory.Emoji);
        var index = (await store.LoadAsync()).ArchiveIndex.Categories[0];

        Assert.Equal(IndexStatus.Unavailable, result.Status);
        Assert.Equal(IndexStatus.Unavailable, index.Status);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(unreadable, Assert.Single(result.SkippedFiles));
        Assert.Empty(index.Images);
        Assert.NotNull(index.LastError);
    }
}
