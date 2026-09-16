using System.Text.Json;
using SixLabors.ImageSharp;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Core.Scanning;
using VrcPicSorter.Core.Storage;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class FileRouterTests
{
    [Fact]
    public async Task UniqueMoveUsesDeterministicCollisionSuffixAndCommitsJournal()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var source = Path.Combine(sourceRoot, "image.png");
        var existing = Path.Combine(archiveRoot, "image.png");
        using var sourceImage = ImageFixtureFactory.CreatePattern(7);
        await sourceImage.SaveAsPngAsync(source);
        await File.WriteAllTextAsync(existing, "existing");
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(sourceImage));
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        var result = await router.MoveUniqueAsync(source, VrcImageCategory.Emoji, fingerprint);

        Assert.Equal(Path.Combine(archiveRoot, "image (2).png"), result.DestinationPath);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(result.DestinationPath));
        var state = await store.LoadAsync();
        Assert.Contains(state.ArchiveIndex.Categories[0].Images, item => item.Path == result.DestinationPath);
        Assert.Empty(state.OperationJournal);
    }

    [Fact]
    public async Task ChangedArchiveCandidateIsNotRecycled()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var candidatePath = Path.Combine(archiveRoot, "candidate.png");
        using var original = ImageFixtureFactory.CreatePattern(10);
        var expected = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(original));
        using var replacement = ImageFixtureFactory.CreatePattern(11);
        await replacement.SaveAsPngAsync(candidatePath);
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var recycleBin = new FakeRecycleBinService();
        var router = new FileRouter(store, new ImageDecoder(), recycleBin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.DeleteArchiveCandidateAsync(
                new ReviewItem { Id = Guid.NewGuid(), Category = VrcImageCategory.Emoji },
                new ReviewCandidate
                {
                    IndexedImageId = Guid.NewGuid(),
                    ArchivePath = candidatePath,
                    ExpectedFingerprint = expected.ExactIdentity,
                }));

        Assert.True(File.Exists(candidatePath));
        Assert.Empty(recycleBin.RecycledPaths);
        Assert.Empty((await store.LoadAsync()).OperationJournal);
    }

    [Fact]
    public async Task RecycleUnavailableNeverDeletesSource()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var held = Path.Combine(sourceRoot, "held.png");
        await File.WriteAllTextAsync(held, "held");
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService(canRecycle: false));
        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            HeldFilePath = held,
            IncomingFingerprint = "fingerprint",
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => router.KeepExistingAsync(review));

        Assert.True(File.Exists(held));
        Assert.Empty((await store.LoadAsync()).OperationJournal);
    }

    [Fact]
    public async Task RestoreReviewReturnsHeldImageToOriginalFolderAndResolvesIt()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var holdingRoot = directory.GetPath("holding");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(holdingRoot);
        var original = Path.Combine(sourceRoot, "2025-05", "emoji.png");
        var held = Path.Combine(holdingRoot, "emoji.png");
        using var image = ImageFixtureFactory.CreatePattern(22);
        await image.SaveAsPngAsync(held);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            IncomingOriginalPath = original,
            HeldFilePath = held,
            IncomingFingerprint = fingerprint.ExactIdentity,
            IncomingImageFingerprint = fingerprint,
        };
        await store.UpdateAsync(state =>
        {
            state.ReviewQueue.Add(review);
            return true;
        });
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        var result = await router.RestoreReviewAsync(review);

        Assert.Equal(original, result.DestinationPath);
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(held));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task RestoreReviewsReportsProgressForEveryHeldImage()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var holdingRoot = directory.GetPath("holding");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(holdingRoot);
        using var image = ImageFixtureFactory.CreatePattern(222);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var reviews = new[]
        {
            CreateReview("first.png"),
            CreateReview("second.png"),
        };

        foreach (var review in reviews)
        {
            await image.SaveAsPngAsync(review.HeldFilePath);
        }

        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.ReviewQueue.AddRange(reviews);
            return true;
        });
        var progress = new RecordingProgress<ReviewRestoreProgress>();
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        var result = await router.RestoreReviewsAsync(reviews, progress);

        Assert.Equal(2, result.Requested);
        Assert.Equal(2, result.Restored);
        Assert.Empty(result.Failures);
        Assert.Equal(new ReviewRestoreProgress(0, 2, 0), progress.Values[0]);
        Assert.Equal(new ReviewRestoreProgress(1, 2, 1), progress.Values[1]);
        Assert.Equal(new ReviewRestoreProgress(2, 2, 2), progress.Values[2]);
        Assert.All(reviews, review => Assert.True(File.Exists(review.IncomingOriginalPath)));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);

        ReviewItem CreateReview(string fileName) => new()
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            IncomingOriginalPath = Path.Combine(sourceRoot, fileName),
            HeldFilePath = Path.Combine(holdingRoot, fileName),
            IncomingFingerprint = fingerprint.ExactIdentity,
            IncomingImageFingerprint = fingerprint,
        };
    }

    [Fact]
    public async Task ClearingInPlaceReviewsLeavesIncomingFilesUntouchedAndUsesOneStateWrite()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(223);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        var reviews = new[]
        {
            CreateReview("first.png"),
            CreateReview("second.png"),
        };
        foreach (var review in reviews)
        {
            await image.SaveAsPngAsync(review.IncomingOriginalPath);
        }

        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.ReviewQueue.AddRange(reviews);
            return true;
        });
        var revisionBeforeClear = (await store.LoadAsync()).Revision;
        var progress = new RecordingProgress<ReviewRestoreProgress>();
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        var result = await router.RestoreReviewsAsync(reviews, progress);

        var state = await store.LoadAsync();
        Assert.Equal(2, result.Restored);
        Assert.Empty(result.Failures);
        Assert.All(reviews, review => Assert.True(File.Exists(review.IncomingOriginalPath)));
        Assert.Empty(state.ReviewQueue);
        Assert.Equal(revisionBeforeClear + 1, state.Revision);
        Assert.Equal(new ReviewRestoreProgress(2, 2, 2), progress.Values[^1]);

        ReviewItem CreateReview(string fileName)
        {
            var path = Path.Combine(sourceRoot, fileName);
            return new ReviewItem
            {
                Id = Guid.NewGuid(),
                Category = VrcImageCategory.Emoji,
                IncomingOriginalPath = path,
                HeldFilePath = path,
                IncomingFingerprint = fingerprint.ExactIdentity,
                IncomingImageFingerprint = fingerprint,
            };
        }
    }

    [Fact]
    public async Task KeepMatchRefusesChangedArchiveImageAndLeavesIncomingHeld()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var held = Path.Combine(sourceRoot, "held.png");
        var archived = Path.Combine(archiveRoot, "match.png");
        using var incomingImage = ImageFixtureFactory.CreatePattern(23);
        using var expectedImage = ImageFixtureFactory.CreatePattern(24);
        using var changedImage = ImageFixtureFactory.CreatePattern(25);
        await incomingImage.SaveAsPngAsync(held);
        await changedImage.SaveAsPngAsync(archived);
        var incomingFingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(incomingImage));
        var expectedFingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(expectedImage));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            HeldFilePath = held,
            IncomingFingerprint = incomingFingerprint.ExactIdentity,
        };
        var candidate = new ReviewCandidate
        {
            IndexedImageId = Guid.NewGuid(),
            ArchivePath = archived,
            ExpectedFingerprint = expectedFingerprint.ExactIdentity,
        };
        var recycleBin = new FakeRecycleBinService();
        var router = new FileRouter(store, new ImageDecoder(), recycleBin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => router.KeepMatchAsync(review, candidate));

        Assert.True(File.Exists(held));
        Assert.Empty(recycleBin.RecycledPaths);
    }

    [Fact]
    public async Task KeepIncomingOverFinalMatchMovesIncomingIntoArchive()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var held = Path.Combine(sourceRoot, "held.png");
        var archived = Path.Combine(archiveRoot, "match.png");
        using var image = ImageFixtureFactory.CreatePattern(26);
        await image.SaveAsPngAsync(held);
        await image.SaveAsPngAsync(archived);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var indexedId = Guid.NewGuid();
        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            IncomingOriginalPath = Path.Combine(sourceRoot, "original.png"),
            HeldFilePath = held,
            IncomingFingerprint = fingerprint.ExactIdentity,
            IncomingImageFingerprint = fingerprint,
            RoutingContext = new ScanRoutingContext
            {
                SourceRootPath = sourceRoot,
                OutputRootPath = Path.GetDirectoryName(archiveRoot)!,
            },
        };
        var candidate = new ReviewCandidate
        {
            Id = Guid.NewGuid(),
            IndexedImageId = indexedId,
            ArchivePath = archived,
            ExpectedFingerprint = fingerprint.ExactIdentity,
        };
        review.Candidates.Add(candidate);
        await store.UpdateAsync(state =>
        {
            state.ReviewQueue.Add(review);
            state.ArchiveIndex.Categories[0].Images.Add(new IndexedImageRecord
            {
                Id = indexedId,
                Category = VrcImageCategory.Emoji,
                Path = archived,
                ExactFingerprint = fingerprint.ExactIdentity,
                Fingerprint = fingerprint,
            });
            return true;
        });
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        var resolved = await router.KeepIncomingOverMatchAsync(review, candidate);

        Assert.True(resolved.ReviewResolved);
        Assert.Null(resolved.PreservedMatchPath);
        Assert.False(File.Exists(archived));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "held.png")));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task KeepIncomingDoesNotRecycleMatchWhenArchiveDestinationIsUnavailable()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var held = Path.Combine(sourceRoot, "held.png");
        var archived = Path.Combine(archiveRoot, "match.png");
        using var image = ImageFixtureFactory.CreatePattern(27);
        await image.SaveAsPngAsync(held);
        await image.SaveAsPngAsync(archived);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            IncomingOriginalPath = Path.Combine(sourceRoot, "original.png"),
            HeldFilePath = held,
            IncomingFingerprint = fingerprint.ExactIdentity,
            IncomingImageFingerprint = fingerprint,
            RoutingContext = new ScanRoutingContext
            {
                SourceRootPath = sourceRoot,
                OutputRootPath = Path.GetDirectoryName(archiveRoot)!,
            },
        };
        var candidate = new ReviewCandidate
        {
            Id = Guid.NewGuid(),
            IndexedImageId = Guid.NewGuid(),
            ArchivePath = archived,
            ExpectedFingerprint = fingerprint.ExactIdentity,
        };
        review.Candidates.Add(candidate);
        await store.UpdateAsync(state =>
        {
            state.Settings.CategoryMappings[0].ArchivePath = directory.GetPath("missing-output", "Emoji");
            state.ReviewQueue.Add(review);
            return true;
        });
        var recycleBin = new FakeRecycleBinService();
        var router = new FileRouter(store, new ImageDecoder(), recycleBin);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => router.KeepIncomingOverMatchAsync(review, candidate));

        Assert.True(File.Exists(archived));
        Assert.True(File.Exists(held));
        Assert.Empty(recycleBin.RecycledPaths);
    }

    [Fact]
    public async Task KeepIncomingMovesFinalIncomingBeforeAttemptingRecycle()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var held = Path.Combine(sourceRoot, "held.png");
        var archived = Path.Combine(archiveRoot, "match.png");
        using var image = ImageFixtureFactory.CreatePattern(28);
        await image.SaveAsPngAsync(held);
        await image.SaveAsPngAsync(archived);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var indexedId = Guid.NewGuid();
        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            IncomingOriginalPath = Path.Combine(sourceRoot, "original.png"),
            HeldFilePath = held,
            IncomingFingerprint = fingerprint.ExactIdentity,
            IncomingImageFingerprint = fingerprint,
            RoutingContext = new ScanRoutingContext
            {
                SourceRootPath = sourceRoot,
                OutputRootPath = Path.GetDirectoryName(archiveRoot)!,
            },
        };
        var candidate = new ReviewCandidate
        {
            Id = Guid.NewGuid(),
            IndexedImageId = indexedId,
            ArchivePath = archived,
            ExpectedFingerprint = fingerprint.ExactIdentity,
        };
        review.Candidates.Add(candidate);
        await store.UpdateAsync(state =>
        {
            state.ReviewQueue.Add(review);
            state.ArchiveIndex.Categories[0].Images.Add(new IndexedImageRecord
            {
                Id = indexedId,
                Category = VrcImageCategory.Emoji,
                Path = archived,
                ExactFingerprint = fingerprint.ExactIdentity,
                Fingerprint = fingerprint,
            });
            return true;
        });
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService(throwOnRecycle: true));

        await Assert.ThrowsAsync<IOException>(() => router.KeepIncomingOverMatchAsync(review, candidate));

        Assert.False(File.Exists(held));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "held.png")));
        Assert.True(File.Exists(archived));

        // The second half failed, so the decision must still be in the queue to retry rather
        // than silently resolved with the duplicate left behind.
        var pending = Assert.Single((await store.LoadAsync()).ReviewQueue);
        Assert.NotEqual(ReviewStatus.Resolved, pending.Status);
    }

    [Fact]
    public async Task KeepIncomingPreservesMatchWhenRecycleBinIsUnavailable()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var held = Path.Combine(sourceRoot, "held.png");
        var archived = Path.Combine(archiveRoot, "2025-05", "match.png");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        using var image = ImageFixtureFactory.CreatePattern(29);
        await image.SaveAsPngAsync(held);
        await image.SaveAsPngAsync(archived);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var indexedId = Guid.NewGuid();
        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            IncomingOriginalPath = Path.Combine(sourceRoot, "original.png"),
            HeldFilePath = held,
            IncomingFingerprint = fingerprint.ExactIdentity,
            IncomingImageFingerprint = fingerprint,
            RoutingContext = new ScanRoutingContext
            {
                SourceRootPath = sourceRoot,
                OutputRootPath = Path.GetDirectoryName(archiveRoot)!,
            },
        };
        var candidate = new ReviewCandidate
        {
            Id = Guid.NewGuid(),
            IndexedImageId = indexedId,
            ArchivePath = archived,
            ExpectedFingerprint = fingerprint.ExactIdentity,
        };
        review.Candidates.Add(candidate);
        await store.UpdateAsync(state =>
        {
            state.ReviewQueue.Add(review);
            state.ArchiveIndex.Categories[0].Images.Add(new IndexedImageRecord
            {
                Id = indexedId,
                Category = VrcImageCategory.Emoji,
                Path = archived,
                ExactFingerprint = fingerprint.ExactIdentity,
                Fingerprint = fingerprint,
            });
            return true;
        });
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService(canRecycle: false));

        var result = await router.KeepIncomingOverMatchAsync(review, candidate);

        Assert.True(result.ReviewResolved);
        Assert.NotNull(result.PreservedMatchPath);
        Assert.True(File.Exists(result.PreservedMatchPath));
        Assert.Contains(Path.Combine("VRC Pic Sorter Replaced", "Emoji", "2025-05"), result.PreservedMatchPath);
        Assert.False(File.Exists(archived));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "held.png")));
        Assert.Empty((await store.LoadAsync()).ReviewQueue);
    }

    [Fact]
    public async Task StartupRecoveryCommitsAMoveAlreadyAppliedOnDisk()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var source = Path.Combine(sourceRoot, "recover.png");
        var destination = Path.Combine(archiveRoot, "recover.png");
        using var image = ImageFixtureFactory.CreatePattern(55);
        await image.SaveAsPngAsync(source);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var journal = new OperationJournal(store);
        var indexedId = Guid.NewGuid();
        var entry = await journal.RecordIntentAsync(new JournalEntry
        {
            Id = Guid.NewGuid(),
            Purpose = JournalOperationPurpose.MoveUnique,
            OperationType = JournalOperationType.Move,
            Category = VrcImageCategory.Emoji,
            SourcePath = source,
            DestinationPath = destination,
            ExpectedSource = new ExpectedFileIdentity
            {
                Fingerprint = fingerprint.ExactIdentity,
                FileSize = new FileInfo(source).Length,
                LastWriteUtc = File.GetLastWriteTimeUtc(source),
            },
            IndexedImageAfterCommit = new IndexedImageRecord
            {
                Id = indexedId,
                Category = VrcImageCategory.Emoji,
                Path = destination,
                Width = fingerprint.Width,
                Height = fingerprint.Height,
                ExactFingerprint = fingerprint.ExactIdentity,
                PerceptualFingerprint = fingerprint.PerceptualFrames[0].DifferenceHash,
                Fingerprint = fingerprint,
            },
        });
        await journal.AdvanceAsync(entry.Id, JournalPhase.SideEffectStarted);
        File.Move(source, destination);

        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());
        var decisions = await router.RecoverPendingOperationsAsync();

        Assert.Equal(JournalReconciliationAction.CommitState, Assert.Single(decisions).Action);
        var state = await store.LoadAsync();
        Assert.Empty(state.OperationJournal);
        Assert.Equal(indexedId, Assert.Single(state.ArchiveIndex.Categories[0].Images).Id);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task DismissingNeedsAttentionPreservesFilesAndUnblocksState()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var source = Path.Combine(sourceRoot, "source.png");
        var destination = Path.Combine(archiveRoot, "destination.png");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(destination, "destination");
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.OperationJournal.Add(new JournalEntry
            {
                Id = Guid.NewGuid(),
                Phase = JournalPhase.NeedsAttention,
                Purpose = JournalOperationPurpose.MoveUnique,
                OperationType = JournalOperationType.Move,
                Category = VrcImageCategory.Emoji,
                SourcePath = source,
                DestinationPath = destination,
                ExpectedSource = new ExpectedFileIdentity { Fingerprint = "unknown" },
            });
            return true;
        });
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        var dismissed = await router.DismissNeedsAttentionOperationsAsync();

        Assert.Equal(1, dismissed);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
        var state = await store.LoadAsync();
        Assert.Empty(state.OperationJournal);
        Assert.Equal(IndexStatus.Stale, state.ArchiveIndex.Categories[0].Status);
    }

    [Theory]
    [InlineData(OrganizationPolicy.CategoryRoot, "")]
    [InlineData(OrganizationPolicy.PreserveIncomingRelativeFolder, "2026-09")]
    public async Task TheArchiveFolderFollowsTheOrganizationPolicy(OrganizationPolicy policy, string expectedBranch)
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var incomingFolder = Path.Combine(sourceRoot, "2026-09");
        Directory.CreateDirectory(incomingFolder);
        Directory.CreateDirectory(archiveRoot);
        var incoming = Path.Combine(incomingFolder, "emoji.png");
        using (var image = ImageFixtureFactory.CreatePattern(61))
        {
            await image.SaveAsPngAsync(incoming);
        }

        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        await store.UpdateAsync(state =>
        {
            state.Settings.OrganizationPolicy = policy;
            return true;
        });
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        var result = await router.MoveUniqueAsync(
            incoming,
            VrcImageCategory.Emoji,
            Fingerprint(61),
            new ScanRoutingContext
            {
                SourceRootPath = sourceRoot,
                RelativeDirectory = "2026-09",
                OutputRootPath = Path.GetDirectoryName(archiveRoot)!,
            });

        var expected = string.IsNullOrEmpty(expectedBranch)
            ? Path.Combine(archiveRoot, "emoji.png")
            : Path.Combine(archiveRoot, expectedBranch, "emoji.png");
        Assert.Equal(expected, result.DestinationPath);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task ArchivingFollowsTheOutputFolderSetNowRatherThanTheOneTheScanSaw()
    {
        // A review can sit in the queue while the output folder is changed under it. Its stored
        // routing context still names the old root, and honouring that filed the image into the
        // folder the person had just stopped using - while the availability check, which reads the
        // current mapping, was guarding a folder nothing was written to.
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        var abandonedRoot = directory.GetPath("old-archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(abandonedRoot);
        var incoming = Path.Combine(sourceRoot, "emoji.png");
        using (var image = ImageFixtureFactory.CreatePattern(62))
        {
            await image.SaveAsPngAsync(incoming);
        }

        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        var result = await router.MoveUniqueAsync(
            incoming,
            VrcImageCategory.Emoji,
            Fingerprint(62),
            new ScanRoutingContext
            {
                SourceRootPath = sourceRoot,
                RelativeDirectory = string.Empty,
                OutputRootPath = directory.GetPath("old-archive"),
            });

        Assert.Equal(Path.Combine(archiveRoot, "emoji.png"), result.DestinationPath);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "emoji.png")));
        Assert.Empty(Directory.GetFiles(abandonedRoot));
    }

    /// <summary>
    /// Nothing else in the app can remove a file the archive is holding twice - deduplication only
    /// ever ran incoming-against-archive, so once both copies were inside they stayed forever.
    /// </summary>
    [Fact]
    public async Task ARemovedArchiveDuplicateGoesToTheRecycleBinAndLeavesTheIndex()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(seed: 80);
        var keep = Path.Combine(archiveRoot, "emoji.png");
        var extra = Path.Combine(archiveRoot, "emoji (2).png");
        await image.SaveAsPngAsync(keep);
        await image.SaveAsPngAsync(extra);
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var recycleBin = new FakeRecycleBinService();
        var router = new FileRouter(store, decoder, recycleBin);
        var indexed = await new ArchiveIndexer(store, decoder).RefreshAsync(VrcImageCategory.Emoji);
        Assert.Equal(IndexStatus.Current, indexed.Status);
        var state = await store.LoadAsync();
        var record = state.ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji)
            .Images.Single(item => item.Path == extra);

        await router.RemoveArchivedDuplicateAsync(record);

        Assert.Equal(extra, Assert.Single(recycleBin.RecycledPaths));
        Assert.True(File.Exists(keep));
        var after = (await store.LoadAsync()).ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji);
        Assert.Equal(keep, Assert.Single(after.Images).Path);
    }

    /// <summary>
    /// The file is verified against what was indexed before it goes, so a copy that changed since
    /// the duplicate was found is refused rather than discarded on stale information.
    /// </summary>
    [Fact]
    public async Task ADuplicateThatChangedSinceItWasFoundIsNotRemoved()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        using var image = ImageFixtureFactory.CreatePattern(seed: 81);
        var keep = Path.Combine(archiveRoot, "emoji.png");
        var extra = Path.Combine(archiveRoot, "emoji (2).png");
        await image.SaveAsPngAsync(keep);
        await image.SaveAsPngAsync(extra);
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var decoder = new ImageDecoder();
        var recycleBin = new FakeRecycleBinService();
        var router = new FileRouter(store, decoder, recycleBin);
        await new ArchiveIndexer(store, decoder).RefreshAsync(VrcImageCategory.Emoji);
        var record = (await store.LoadAsync()).ArchiveIndex.Categories
            .Single(item => item.Category == VrcImageCategory.Emoji)
            .Images.Single(item => item.Path == extra);

        using (var replacement = ImageFixtureFactory.CreatePattern(seed: 82))
        {
            await replacement.SaveAsPngAsync(extra);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.RemoveArchivedDuplicateAsync(record));
        Assert.Empty(recycleBin.RecycledPaths);
        Assert.True(File.Exists(extra));
    }

    internal static JsonStateStore CreateStore(
        TestDirectory directory,
        string sourceRoot,
        string archiveRoot)
    {
        var store = new JsonStateStore(
            directory.GetPath("state"),
            () => AppStateDefaults.Create(directory.GetPath("profile"), directory.GetPath("local")));
        var state = store.LoadAsync().GetAwaiter().GetResult();
        var mapping = state.Settings.CategoryMappings.Single(item => item.Category == VrcImageCategory.Emoji);
        mapping.SourcePath = sourceRoot;
        mapping.ArchivePath = archiveRoot;
        mapping.IsEnabled = true;
        state.Settings.OutputRootPath = Path.GetDirectoryName(archiveRoot)!;
        state.Settings.OrganizationPolicy = OrganizationPolicy.CategoryRoot;
        store.SaveAsync(state).GetAwaiter().GetResult();
        return store;
    }

    internal static ImageFingerprint Fingerprint(int seed)
    {
        using var image = ImageFixtureFactory.CreatePattern(seed);
        return ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
    }

    internal sealed class FakeRecycleBinService(bool canRecycle = true, bool throwOnRecycle = false) : IRecycleBinService
    {
        public List<string> RecycledPaths { get; } = [];

        public bool CanRecycle(string path) => canRecycle;

        public Task RecycleAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!canRecycle)
            {
                throw new NotSupportedException();
            }

            if (throwOnRecycle)
            {
                throw new IOException("Simulated recycle failure.");
            }

            RecycledPaths.Add(path);
            File.Delete(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    [Fact]
    public async Task UniqueMoveUsesThreeDurableStateWrites()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var source = Path.Combine(sourceRoot, "image.png");
        using var image = ImageFixtureFactory.CreatePattern(29);
        await image.SaveAsPngAsync(source);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());
        var before = ReadRevision(store);

        await router.MoveUniqueAsync(source, VrcImageCategory.Emoji, fingerprint);

        // Intent, side-effect-started, then a single write that applies the mutation and
        // completes the entry. Each one rewrites the whole state document, so this count is a
        // cost the app pays per image and is pinned deliberately.
        Assert.Equal(3, ReadRevision(store) - before);
        Assert.True(File.Exists(Path.Combine(archiveRoot, "image.png")));
    }

    private static long ReadRevision(JsonStateStore store)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(store.StatePath));
        return document.RootElement.GetProperty("revision").GetInt64();
    }

    [Fact]
    public async Task UniqueMoveKeepsTheArchivedRecordsFingerprintAcrossAReload()
    {
        using var directory = new TestDirectory();
        var sourceRoot = directory.GetPath("incoming");
        var archiveRoot = directory.GetPath("archive", "Emoji");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);
        var source = Path.Combine(sourceRoot, "image.png");
        using var image = ImageFixtureFactory.CreatePattern(30);
        await image.SaveAsPngAsync(source);
        var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
        using var store = CreateStore(directory, sourceRoot, archiveRoot);
        var router = new FileRouter(store, new ImageDecoder(), new FakeRecycleBinService());

        await router.MoveUniqueAsync(source, VrcImageCategory.Emoji, fingerprint);

        // The journal entry is reloaded from disk when the mutation commits, and fingerprints are
        // no longer persisted on journal entries. If the in-memory one is not carried across, the
        // archived record lands without a fingerprint and the whole index rebuilds every move.
        var reloaded = await store.LoadAsync();
        var record = Assert.Single(reloaded.ArchiveIndex.Categories[0].Images);
        Assert.NotNull(record.Fingerprint);
        Assert.Equal(fingerprint.ExactIdentity, record.Fingerprint!.ExactIdentity);
        Assert.True(record.Fingerprint.HasCurrentFeatures);
    }
}
