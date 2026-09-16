using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Tests.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Storage;

namespace VrcPicSorter.Tests.Storage;

public sealed class JsonStateStoreTests
{
    [Fact]
    public async Task CorruptStateIsQuarantinedAndDefaultsRemainUsable()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        _ = await store.LoadAsync();
        await File.WriteAllTextAsync(store.StatePath, "{not-json");

        var recovered = await store.LoadAsync();

        Assert.Equal(AppStateDocument.CurrentSchemaVersion, recovered.SchemaVersion);
        Assert.NotNull(store.LastRecoveryNotice);
        Assert.True(File.Exists(store.LastRecoveryNotice.QuarantinedPath));
        Assert.Equal("{not-json", await File.ReadAllTextAsync(store.LastRecoveryNotice.QuarantinedPath));
        Assert.True(File.Exists(store.StatePath));
    }

    /// <summary>
    /// Every record in the quarantined document points at a fingerprint in the sidecar. Writing
    /// defaults over the top replaced that sidecar with an empty one, so restoring the quarantined
    /// file by hand afterwards gave an index with no fingerprints at all - the quarantine kept the
    /// document and threw away the half that made it useful.
    /// </summary>
    [Fact]
    public async Task RecoveringToDefaultsSetsTheFingerprintSidecarAsideRatherThanEmptyingIt()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        var category = state.ArchiveIndex.Categories[0];
        category.Status = IndexStatus.Current;
        using var image = ImageFixtureFactory.CreatePattern(seed: 41);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        category.Images.Add(new IndexedImageRecord
        {
            Id = Guid.NewGuid(),
            Category = category.Category,
            Path = @"D:\Archive\first.png",
            Width = fingerprint.Width,
            Height = fingerprint.Height,
            ExactFingerprint = fingerprint.ExactIdentity,
            PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
            Fingerprint = fingerprint,
        });
        await store.SaveAsync(state);
        var sidecar = await File.ReadAllTextAsync(store.FingerprintPath);

        // Both documents unreadable, so there is no backup to fall back to and defaults are all
        // that is left.
        await File.WriteAllTextAsync(store.StatePath, "{not-json");
        await File.WriteAllTextAsync(store.BackupPath, "{not-json either");
        _ = await store.LoadAsync();

        Assert.NotNull(store.LastRecoveryNotice);
        Assert.False(store.LastRecoveryNotice.RestoredBackup);
        var setAside = Directory
            .GetFiles(Path.GetDirectoryName(store.FingerprintPath)!, "fingerprints.corrupt-*.json")
            .Single();
        Assert.Equal(sidecar, await File.ReadAllTextAsync(setAside));
    }

    [Fact]
    public async Task CorruptPrimaryStateRecoversFromLastValidBackup()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        state.Settings.SimilarityProfile = SimilarityProfile.Strict;
        await store.SaveAsync(state);
        state.Settings.SimilarityProfile = SimilarityProfile.Broad;
        await store.SaveAsync(state);
        Assert.True(File.Exists(store.BackupPath));
        await File.WriteAllTextAsync(store.StatePath, "{not-json");

        var recovered = await store.LoadAsync();

        Assert.Equal(SimilarityProfile.Strict, recovered.Settings.SimilarityProfile);
        Assert.NotNull(store.LastRecoveryNotice);
        Assert.True(store.LastRecoveryNotice.RestoredBackup);
    }

    [Fact]
    public async Task FirstLoadCreatesFixedDefaultsWithoutTouchingImageFolders()
    {
        using var directory = new TestDirectory();
        var profilePath = directory.GetPath("profile");
        var localAppDataPath = directory.GetPath("local-app-data");
        using var store = new JsonStateStore(
            directory.GetPath("state"),
            () => AppStateDefaults.Create(profilePath, localAppDataPath));

        var state = await store.LoadAsync();

        Assert.Equal(AppStateDocument.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(
            AppStateDefaults.FixedCategories,
            state.Settings.CategoryMappings.Select(mapping => mapping.Category));
        Assert.Equal(
            AppStateDefaults.FixedCategories,
            state.ArchiveIndex.Categories.Select(category => category.Category));
        Assert.All(state.Settings.CategoryMappings, mapping => Assert.False(mapping.IsEnabled));
        Assert.All(state.ArchiveIndex.Categories, category => Assert.Equal(IndexStatus.Stale, category.Status));
        Assert.Equal(SimilarityProfile.Conservative, state.Settings.SimilarityProfile);
        Assert.Equal(OrganizationPolicy.CategoryRoot, state.Settings.OrganizationPolicy);
        Assert.Equal(
            Path.Combine(profilePath, "Pictures", "VRChat", "Archived Images"),
            state.Settings.OutputRootPath);
        Assert.All(
            state.Settings.CategoryMappings,
            mapping => Assert.Equal(
                Path.Combine(profilePath, "Pictures", "VRChat", mapping.Category.ToString()),
                mapping.SourcePath));
        Assert.True(File.Exists(store.StatePath));
        Assert.All(
            state.Settings.CategoryMappings,
            mapping =>
            {
                Assert.False(Directory.Exists(mapping.SourcePath));
                Assert.False(Directory.Exists(mapping.ArchivePath));
            });
        Assert.False(Directory.Exists(state.Settings.HoldingRootPath));
    }

    /// <summary>
    /// Archiving one image adds one fingerprint but rewrites every other one with it, so a scan
    /// pays the whole sidecar per file. A caller working through many files holds the writes and
    /// pays once; nothing may be lost when the hold is released.
    /// </summary>
    [Fact]
    public async Task FingerprintWritesHeldForABatchReachDiskOnRelease()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        var category = state.ArchiveIndex.Categories[0];
        category.Status = IndexStatus.Current;

        static IndexedImageRecord Record(
            VrcImageCategory imageCategory,
            string path,
            ImageFingerprint fingerprint) =>
            new()
            {
                Id = Guid.NewGuid(),
                Category = imageCategory,
                Path = path,
                FileSize = 1,
                LastWriteUtc = DateTimeOffset.UnixEpoch,
                Width = fingerprint.Width,
                Height = fingerprint.Height,
                ExactFingerprint = fingerprint.ExactIdentity,
                PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
                Fingerprint = fingerprint,
            };

        using var first = ImageFixtureFactory.CreatePattern(seed: 11);
        category.Images.Add(Record(
            category.Category,
            @"D:\Archive\first.png",
            ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(first))));
        await store.SaveAsync(state);
        var afterFirstWrite = await File.ReadAllTextAsync(store.FingerprintPath);

        await store.HoldFingerprintWritesAsync();
        using var second = ImageFixtureFactory.CreatePattern(seed: 12);
        category.Images.Add(Record(
            category.Category,
            @"D:\Archive\second.png",
            ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(second))));
        await store.SaveAsync(state);

        // The state document now references a fingerprint the sidecar does not carry yet.
        Assert.Equal(afterFirstWrite, await File.ReadAllTextAsync(store.FingerprintPath));

        await store.ReleaseFingerprintWritesAsync();

        Assert.NotEqual(afterFirstWrite, await File.ReadAllTextAsync(store.FingerprintPath));
        var reloaded = await store.LoadAsync();
        var reloadedCategory = reloaded.ArchiveIndex.Categories[0];
        Assert.Equal(2, reloadedCategory.Images.Count);
        Assert.All(reloadedCategory.Images, image => Assert.NotNull(image.Fingerprint));
        Assert.Equal(IndexStatus.Current, reloadedCategory.Status);
        Assert.Null(reloadedCategory.LastError);
    }

    /// <summary>
    /// While a scan holds its writes the fingerprints live only in memory, and reading the state
    /// back mid-scan used to answer from the sidecar alone. If the sidecar had gone - a cleanup
    /// tool, a sync client, anything - that answer was "no fingerprints", which stripped them off
    /// every record the scan had just added, took the index stale in the middle of the scan that
    /// added them, and failed every remaining image with "rescan required".
    /// </summary>
    [Fact]
    public async Task HeldFingerprintsSurviveASidecarThatDisappearsMidScan()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        var category = state.ArchiveIndex.Categories[0];
        category.Status = IndexStatus.Current;

        await store.HoldFingerprintWritesAsync();
        using var image = ImageFixtureFactory.CreatePattern(seed: 31);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        category.Images.Add(new IndexedImageRecord
        {
            Id = Guid.NewGuid(),
            Category = category.Category,
            Path = @"D:\Archive\first.png",
            FileSize = 1,
            LastWriteUtc = DateTimeOffset.UnixEpoch,
            Width = fingerprint.Width,
            Height = fingerprint.Height,
            ExactFingerprint = fingerprint.ExactIdentity,
            PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
            Fingerprint = fingerprint,
        });
        await store.SaveAsync(state);

        // Held, so the new fingerprint is in memory only - and now the file it would have gone to
        // is taken away underneath the scan.
        File.Delete(store.FingerprintPath);

        var reloaded = await store.LoadAsync();

        var reloadedCategory = reloaded.ArchiveIndex.Categories[0];
        Assert.Equal(IndexStatus.Current, reloadedCategory.Status);
        Assert.Null(reloadedCategory.LastError);
        Assert.NotNull(Assert.Single(reloadedCategory.Images).Fingerprint);

        await store.ReleaseFingerprintWritesAsync();
        Assert.True(File.Exists(store.FingerprintPath));
    }

    [Fact]
    public async Task SaveThenLoadPreservesTheCompleteStateDocument()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        var timestamp = new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.Zero);
        var imageId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        using var sample = ImageFixtureFactory.CreatePattern(seed: 7);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(sample));

        state.Settings.CategoryMappings[0].SourcePath = @"C:\Incoming\Emoji";
        state.Settings.CategoryMappings[0].ArchivePath = @"D:\Archive\Emoji";
        state.Settings.CategoryMappings[0].IsEnabled = true;
        state.Settings.OrganizationPolicy = OrganizationPolicy.PreserveIncomingRelativeFolder;
        state.Settings.SimilarityProfile = SimilarityProfile.Broad;
        state.Settings.Automation.WatchWhileOpen = true;
        state.Settings.Automation.StartWithWindows = true;
        state.Settings.HoldingRootPath = @"C:\State\Holding";
        state.Settings.BringReviewForwardWhenHeld = false;

        state.ArchiveIndex.Categories[0].Status = IndexStatus.Current;
        state.ArchiveIndex.Categories[0].Generation = 42;
        state.ArchiveIndex.Categories[0].LastCompletedUtc = timestamp;
        state.ArchiveIndex.Categories[0].LastError = "prior error";
        state.ArchiveIndex.Categories[0].Images.Add(new IndexedImageRecord
        {
            Id = imageId,
            Category = VrcImageCategory.Emoji,
            Path = @"D:\Archive\Emoji\sample.png",
            FileSize = 1234,
            LastWriteUtc = timestamp,
            Width = 512,
            Height = 256,
            ExactFingerprint = "exact",
            PerceptualFingerprint = "perceptual",
            Fingerprint = fingerprint,
        });

        state.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            Status = ReviewStatus.NeedsReconciliation,
            IncomingOriginalPath = @"C:\Incoming\Emoji\sample.png",
            HeldFilePath = @"C:\State\Holding\Emoji\sample.png",
            IncomingFingerprint = "incoming",
            IndexGeneration = 42,
            CreatedUtc = timestamp,
            Candidates =
            [
                new ReviewCandidate
                {
                    Id = Guid.NewGuid(),
                    IndexedImageId = imageId,
                    ArchivePath = @"D:\Archive\Emoji\sample.png",
                    ExpectedFingerprint = "exact",
                    MatchKind = MatchKind.Similar,
                    SimilarityScore = 0.93,
                    MatchReasons = ["same dimensions", "close thumbnail"],
                    IsSelected = true,
                    IsStale = true,
                },
            ],
        });

        state.OperationJournal.Add(new JournalEntry
        {
            Id = operationId,
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            Phase = JournalPhase.Completed,
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Incoming\Emoji\sample.png",
            DestinationPath = @"C:\State\Holding\Emoji\sample.png",
            ExpectedSource = new ExpectedFileIdentity
            {
                Fingerprint = "incoming",
                FileSize = 1234,
                LastWriteUtc = timestamp,
            },
            CreatedUtc = timestamp,
            UpdatedUtc = timestamp.AddMinutes(1),
            LastError = "recovered",
        });

        state.History.Add(new ActivityEntry
        {
            Id = Guid.NewGuid(),
            OccurredUtc = timestamp,
            Kind = ActivityKind.ReviewDecision,
            Level = ActivityLevel.Warning,
            Category = VrcImageCategory.Emoji,
            Message = "Queued for reconciliation.",
            SourcePath = @"C:\Incoming\Emoji\sample.png",
            DestinationPath = @"C:\State\Holding\Emoji\sample.png",
            OperationId = operationId,
        });

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Equivalent(state, loaded, strict: true);
    }

    [Fact]
    public async Task InterruptedTemporaryWriteLeavesLastSnapshotReadable()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        state.Settings.SimilarityProfile = SimilarityProfile.Strict;
        await store.SaveAsync(state);

        await File.WriteAllTextAsync(store.TemporaryPath, """{"schemaVersion":1,"settings":""");

        var loaded = await store.LoadAsync();

        Assert.Equal(SimilarityProfile.Strict, loaded.Settings.SimilarityProfile);
        Assert.True(File.Exists(store.TemporaryPath));
    }

    [Fact]
    public async Task SavingAStaleSnapshotCannotEraseANewerJournalEntry()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var stale = await store.LoadAsync();
        var journal = new OperationJournal(store);
        await journal.RecordIntentAsync(new JournalEntry
        {
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Incoming\Emoji\sample.png",
            DestinationPath = @"D:\Archive\Emoji\sample.png",
            ExpectedSource = new ExpectedFileIdentity { Fingerprint = "exact" },
        });

        stale.Settings.SimilarityProfile = SimilarityProfile.Broad;

        var conflict = await Assert.ThrowsAsync<StateRevisionConflictException>(
            () => store.SaveAsync(stale));
        var current = await store.LoadAsync();

        Assert.Equal(stale.Revision + 1, conflict.ActualRevision);
        Assert.Single(current.OperationJournal);
        Assert.NotEqual(SimilarityProfile.Broad, current.Settings.SimilarityProfile);
    }

    [Fact]
    public async Task ClearLocalDataIsBlockedByPendingJournalOperation()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var journal = new OperationJournal(store);
        var operation = await journal.RecordIntentAsync(new JournalEntry
        {
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Incoming\Emoji\sample.png",
            DestinationPath = @"D:\Archive\Emoji\sample.png",
            ExpectedSource = new ExpectedFileIdentity { Fingerprint = "exact" },
        });

        var result = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByPendingOperation, result.Status);
        Assert.Equal([operation.Id], result.BlockingIds);
    }

    [Fact]
    public async Task ClearLocalDataNeverDeletesAnOrphanedHeldImage()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        Directory.CreateDirectory(state.Settings.HoldingRootPath);
        var orphan = Path.Combine(state.Settings.HoldingRootPath, "orphan.png");
        await File.WriteAllTextAsync(orphan, "image bytes");

        var result = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByHeldFiles, result.Status);
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(store.StatePath));
    }

    [Fact]
    public async Task ClearLocalDataFindsOrphanedHeldImageWhenStateFileIsMissing()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        var defaults = AppStateDefaults.Create(
            directory.GetPath("profile"),
            directory.GetPath("local-app-data"),
            stateDirectory);
        Directory.CreateDirectory(defaults.Settings.HoldingRootPath);
        var orphan = Path.Combine(defaults.Settings.HoldingRootPath, "orphan.png");
        await File.WriteAllTextAsync(orphan, "image bytes");
        using var store = CreateStore(directory);

        var result = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByHeldFiles, result.Status);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task ClearLocalDataRejectsHoldingRootOutsideStateDirectory()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var outside = directory.GetPath("unrelated-empty-folder", "nested");
        Directory.CreateDirectory(outside);
        var state = await store.LoadAsync();
        state.Settings.HoldingRootPath = Path.GetDirectoryName(outside)!;
        await store.SaveAsync(state);

        var result = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByUnsafeHoldingRoot, result.Status);
        Assert.True(Directory.Exists(outside));
        Assert.True(File.Exists(store.StatePath));
    }

    [Fact]
    public async Task LoadingCompactsResolvedReviewsCompletedOperationsAndOldHistory()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        state.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Status = ReviewStatus.Resolved,
            IncomingOriginalPath = "resolved.png",
            RoutingContext = new ScanRoutingContext(),
        });
        state.OperationJournal.Add(new JournalEntry
        {
            Id = Guid.NewGuid(),
            Phase = JournalPhase.Completed,
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            SourcePath = "source.png",
            DestinationPath = "destination.png",
            ExpectedSource = new ExpectedFileIdentity { Fingerprint = "exact" },
        });
        state.History.AddRange(Enumerable.Range(0, AppStateCompactor.MaximumHistoryEntries + 5).Select(index =>
            new ActivityEntry
            {
                Id = Guid.NewGuid(),
                OccurredUtc = DateTimeOffset.UnixEpoch.AddMinutes(index),
                Message = index.ToString(),
            }));

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.ReviewQueue);
        Assert.Empty(loaded.OperationJournal);
        Assert.Equal(AppStateCompactor.MaximumHistoryEntries, loaded.History.Count);
        Assert.Equal("5", loaded.History[0].Message);
    }

    [Fact]
    public async Task ClearLocalDataIsBlockedByHeldReviewAndSucceedsAfterResolution()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        var reviewId = Guid.NewGuid();
        state.ReviewQueue.Add(new ReviewItem
        {
            Id = reviewId,
            Category = VrcImageCategory.Prints,
            Status = ReviewStatus.Pending,
            IncomingOriginalPath = @"C:\Incoming\Prints\held.png",
            HeldFilePath = @"C:\State\Holding\Prints\held.png",
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await store.SaveAsync(state);

        var blocked = await store.TryClearLocalDataAsync();

        Assert.Equal(ClearLocalDataStatus.BlockedByPendingReview, blocked.Status);
        Assert.Equal([reviewId], blocked.BlockingIds);
        Assert.True(File.Exists(store.StatePath));

        state.ReviewQueue.Single().Status = ReviewStatus.Resolved;
        await store.SaveAsync(state);
        var cleared = await store.TryClearLocalDataAsync();

        Assert.True(cleared.WasCleared);
        Assert.False(File.Exists(store.StatePath));
        Assert.False(File.Exists(store.TemporaryPath));
    }

    [Fact]
    public async Task ClearLocalDataRemovesBackupAndQuarantinedStateFiles()
    {
        using var directory = new TestDirectory();
        using var store = CreateStore(directory);
        var state = await store.LoadAsync();
        await store.SaveAsync(state);
        var quarantine = Path.Combine(store.StateDirectory, "state.corrupt-test.json");
        await File.WriteAllTextAsync(quarantine, "corrupt");

        var result = await store.TryClearLocalDataAsync();

        Assert.True(result.WasCleared);
        Assert.False(File.Exists(store.StatePath));
        Assert.False(File.Exists(store.BackupPath));
        Assert.False(File.Exists(quarantine));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PreviousMatcherIndexesAndReviewsRequireRefresh(int schemaVersion)
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        Directory.CreateDirectory(stateDirectory);
        var legacy = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        legacy.SchemaVersion = schemaVersion;
        legacy.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Status = ReviewStatus.Pending,
            IncomingOriginalPath = directory.GetPath("incoming.png"),
            HeldFilePath = directory.GetPath("held.png"),
            RoutingContext = new ScanRoutingContext(),
        });
        foreach (var category in legacy.ArchiveIndex.Categories)
        {
            category.Status = IndexStatus.Current;
            category.LastError = null;
        }

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, JsonStateStore.StateFileName),
            JsonSerializer.Serialize(legacy, options));
        using var store = new JsonStateStore(
            stateDirectory,
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"), stateDirectory));

        var migrated = await store.LoadAsync();

        Assert.Equal(AppStateDocument.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.All(
            migrated.ArchiveIndex.Categories,
            category =>
            {
                Assert.Equal(IndexStatus.Stale, category.Status);
                Assert.Contains("fingerprints", category.LastError, StringComparison.OrdinalIgnoreCase);
            });
        Assert.Equal(ReviewStatus.NeedsReconciliation, Assert.Single(migrated.ReviewQueue).Status);
    }

    [Fact]
    public async Task VersionOneSiblingArchivesMigrateToTheirCommonOutputRoot()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        Directory.CreateDirectory(stateDirectory);
        var legacy = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        legacy.SchemaVersion = 1;
        var legacyRoot = directory.GetPath("legacy archive");
        foreach (var mapping in legacy.Settings.CategoryMappings)
        {
            mapping.ArchivePath = Path.Combine(legacyRoot, mapping.Category.ToString());
        }

        legacy.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Status = ReviewStatus.Pending,
            IncomingOriginalPath = directory.GetPath("profile", "Emoji", "pending.png"),
            HeldFilePath = directory.GetPath("local", "Holding", "pending.png"),
            RoutingContext = new ScanRoutingContext
            {
                SourceRootPath = directory.GetPath("profile", "Emoji"),
                OutputRootPath = legacyRoot,
            },
        });

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, JsonStateStore.StateFileName),
            JsonSerializer.Serialize(legacy, options));
        using var store = new JsonStateStore(
            stateDirectory,
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"), stateDirectory));

        var migrated = await store.LoadAsync();

        Assert.Equal(AppStateDocument.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(Path.GetFullPath(legacyRoot), migrated.Settings.OutputRootPath);
        Assert.Empty(migrated.Settings.LegacyArchiveMappings);
        Assert.All(migrated.ArchiveIndex.Categories, index => Assert.Equal(IndexStatus.Stale, index.Status));
        Assert.Equal(ReviewStatus.NeedsReconciliation, Assert.Single(migrated.ReviewQueue).Status);
    }

    [Fact]
    public async Task VersionOneUnrelatedArchivesRequireOutputConfirmationAndRepairReviewRouting()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        Directory.CreateDirectory(stateDirectory);
        var legacy = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        legacy.SchemaVersion = 1;
        foreach (var mapping in legacy.Settings.CategoryMappings)
        {
            mapping.ArchivePath = directory.GetPath($"legacy-{mapping.Category}");
        }

        var emoji = legacy.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Emoji);
        legacy.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            IncomingOriginalPath = Path.Combine(emoji.SourcePath, "2025-05", "held.png"),
            HeldFilePath = directory.GetPath("holding", "held.png"),
            RoutingContext = new ScanRoutingContext(),
        });
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, JsonStateStore.StateFileName),
            JsonSerializer.Serialize(legacy, options));
        using var store = new JsonStateStore(
            stateDirectory,
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"), stateDirectory));

        var migrated = await store.LoadAsync();

        Assert.False(migrated.Settings.OutputRootConfirmed);
        Assert.Equal(3, migrated.Settings.LegacyArchiveMappings.Count);
        var review = Assert.Single(migrated.ReviewQueue);
        Assert.Equal("2025-05", review.RoutingContext.RelativeDirectory);
        Assert.False(string.IsNullOrWhiteSpace(review.RoutingContext.OutputRootPath));
    }

    private static JsonStateStore CreateStore(TestDirectory directory) =>
        new(
            directory.GetPath("state"),
            () => AppStateDefaults.Create(
                directory.GetPath("profile"),
                directory.GetPath("local-app-data"),
                directory.GetPath("state")));

    [Fact]
    public async Task RevisionIsReadCorrectlyWhenTheDocumentIsLargerThanTheProbe()
    {
        using var directory = new TestDirectory();
        using var store = new JsonStateStore(
            directory.GetPath("state"),
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local")));

        // Push the document well past the 4 KB prefix the revision probe reads, so a wrong
        // revision would surface as a stale-revision conflict on the next write.
        await store.UpdateAsync(state =>
        {
            for (var index = 0; index < 400; index++)
            {
                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = DateTimeOffset.UtcNow,
                    Kind = ActivityKind.Scan,
                    Level = ActivityLevel.Information,
                    Message = new string('x', 200),
                });
            }

            return true;
        });

        Assert.True(new FileInfo(store.StatePath).Length > 8192);

        // Each of these reads the revision, and would throw StateRevisionConflictException if the
        // probe returned the wrong number.
        for (var index = 0; index < 3; index++)
        {
            await store.UpdateAsync(state => state.Revision);
        }

        var reloaded = await store.LoadAsync();
        Assert.True(reloaded.Revision > 3);
    }

    private static JsonSerializerOptions FixtureOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static (AppStateDocument Seed, ImageFingerprint Fingerprint, Guid RecordId) SeedWithIndexedImage(
        TestDirectory directory)
    {
        using var image = ImageFixtureFactory.CreatePattern(320);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var recordId = Guid.NewGuid();
        var seed = AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local"));
        var category = seed.ArchiveIndex.Categories[0];
        category.Status = IndexStatus.Current;
        category.Images.Add(new IndexedImageRecord
        {
            Id = recordId,
            Category = category.Category,
            Path = directory.GetPath("archive", "one.png"),
            ExactFingerprint = fingerprint.ExactIdentity,
            Fingerprint = fingerprint,
        });
        return (seed, fingerprint, recordId);
    }

    [Fact]
    public async Task FingerprintsEmbeddedByOlderVersionsAreMovedIntoTheSidecar()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        Directory.CreateDirectory(stateDirectory);
        var (seed, fingerprint, recordId) = SeedWithIndexedImage(directory);

        // Write the document the way version 4 did: schemaVersion 4, fingerprint inline.
        var options = FixtureOptions();
        var node = JsonSerializer.SerializeToNode(seed, options)!;
        node["schemaVersion"] = 4;
        node["archiveIndex"]!["categories"]![0]!["images"]![0]!["fingerprint"] =
            JsonSerializer.SerializeToNode(fingerprint, options);
        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, JsonStateStore.StateFileName),
            node.ToJsonString());

        using var store = new JsonStateStore(
            stateDirectory,
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local")));

        var loaded = await store.LoadAsync();

        // The existing settings and index survive; only where the fingerprint lives changed.
        Assert.Equal(AppStateDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        var record = Assert.Single(loaded.ArchiveIndex.Categories[0].Images);
        Assert.Equal(recordId, record.Id);
        Assert.NotNull(record.Fingerprint);
        Assert.Equal(fingerprint.ExactIdentity, record.Fingerprint!.ExactIdentity);
        Assert.Equal(IndexStatus.Current, loaded.ArchiveIndex.Categories[0].Status);

        Assert.True(File.Exists(store.FingerprintPath));
        Assert.DoesNotContain(
            "perceptualFrames",
            await File.ReadAllTextAsync(store.StatePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateWritesLeaveTheFingerprintSidecarAloneWhenTheImageSetIsUnchanged()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        var (seed, _, _) = SeedWithIndexedImage(directory);
        using var store = new JsonStateStore(stateDirectory, () => seed);

        _ = await store.LoadAsync();
        Assert.True(File.Exists(store.FingerprintPath));
        var written = File.GetLastWriteTimeUtc(store.FingerprintPath);
        await Task.Delay(30);

        // Three writes that do not change which images are indexed. This is the shape of a file
        // operation, and none of them should rewrite the large sidecar.
        for (var index = 0; index < 3; index++)
        {
            await store.UpdateAsync(state =>
            {
                state.History.Add(new ActivityEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredUtc = DateTimeOffset.UtcNow,
                    Kind = ActivityKind.Scan,
                    Level = ActivityLevel.Information,
                    Message = "unchanged image set",
                });
                return true;
            });
        }

        Assert.Equal(written, File.GetLastWriteTimeUtc(store.FingerprintPath));
    }

    [Fact]
    public async Task AnIndexedImageWithoutItsFingerprintForcesARebuild()
    {
        using var directory = new TestDirectory();
        var stateDirectory = directory.GetPath("state");
        var (seed, _, _) = SeedWithIndexedImage(directory);
        using var store = new JsonStateStore(stateDirectory, () => seed);
        _ = await store.LoadAsync();
        Assert.True(File.Exists(store.FingerprintPath));

        File.Delete(store.FingerprintPath);
        var reloaded = await store.LoadAsync();

        // Without its fingerprint the record would be skipped when matching, which shows up as
        // duplicates being archived instead of queued. Rebuilding is the safe response.
        Assert.Equal(IndexStatus.Stale, reloaded.ArchiveIndex.Categories[0].Status);
        Assert.NotNull(reloaded.ArchiveIndex.Categories[0].LastError);
    }
}
