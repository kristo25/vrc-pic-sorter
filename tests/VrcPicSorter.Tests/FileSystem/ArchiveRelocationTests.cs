using System.IO;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class ArchiveRelocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressFailurePreservesCompletedMoveReceipt(bool cancelled)
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old", "Emoji");
        var to = directory.GetPath("new", "Emoji");
        Directory.CreateDirectory(from);
        var source = Path.Combine(from, "a.png");
        var destination = Path.Combine(to, "a.png");
        File.WriteAllText(source, "a");
        var result = await ArchiveRelocation.RelocateAsync([
            new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 1, 1)],
            new CallbackProgress(_ =>
            {
                if (cancelled) throw new OperationCanceledException("Progress reporting stopped.");
                throw new IOException("Progress reporting failed.");
            }));

        Assert.False(File.Exists(source));
        Assert.Equal("a", File.ReadAllText(destination));
        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.LeftBehind);
        Assert.Equal(destination, Assert.Single(result.Moves).To);
        Assert.NotEmpty(result.Errors);
        Assert.True(result.Stopped);
    }

    [Fact]
    public async Task RelocationReturnsToCallerAndStopsBetweenFiles()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old", "Emoji");
        var to = directory.GetPath("new", "Emoji");
        Directory.CreateDirectory(from);
        File.WriteAllText(Path.Combine(from, "a.png"), "a");
        File.WriteAllText(Path.Combine(from, "b.png"), "b");
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<Task<ArchiveRelocationResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                returned.SetResult(ArchiveRelocation.RelocateAsync([
                    new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 2, 2)],
                    new CallbackProgress(_ => { reached.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }), cancellation.Token));
            }
            catch (Exception exception) { returned.TrySetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool callerResponsive;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            callerResponsive = await Task.WhenAny(returned.Task, Task.Delay(TimeSpan.FromSeconds(2))) == returned.Task;
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
        }
        var result = await (await returned.Task.WaitAsync(TimeSpan.FromSeconds(10))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(callerResponsive, "Relocation blocked its caller during file work.");
        Assert.True(result.Stopped);
        Assert.Equal(1, result.Moved);
        Assert.Single(result.Moves);
        Assert.Single(Directory.GetFiles(from));
        Assert.Single(Directory.GetFiles(to));
    }

    private sealed class CallbackProgress(Action<ArchiveRelocationProgress> callback) : IProgress<ArchiveRelocationProgress>
    {
        public void Report(ArchiveRelocationProgress value) => callback(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingRelocationMovesNothing(bool reverse)
    {
        using var directory = new TestDirectory();
        var parent = directory.GetPath("old", "Emoji");
        var child = Path.Combine(parent, "new-output", "Emoji");
        Directory.CreateDirectory(child);
        var original = Path.Combine(parent, "old.png");
        var alreadyNew = Path.Combine(child, "new.png");
        File.WriteAllText(original, "old");
        File.WriteAllText(alreadyNew, "new");
        var step = new ArchiveRelocationStep(VrcImageCategory.Emoji,
            reverse ? child : parent, reverse ? parent : child, 2, 6);
        var result = await ArchiveRelocation.RelocateAsync([step]);
        Assert.Equal(0, result.Moved);
        Assert.NotEmpty(result.Errors);
        Assert.Equal("old", File.ReadAllText(original));
        Assert.Equal("new", File.ReadAllText(alreadyNew));
    }

    [Fact]
    public async Task MissingRelocationRootIsNotReportedAsAnEmptySuccess()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("offline", "Emoji");
        var result = await ArchiveRelocation.RelocateAsync([
            new ArchiveRelocationStep(VrcImageCategory.Emoji, from, directory.GetPath("new", "Emoji"), 25, 100)]);
        Assert.Equal(0, result.Moved);
        Assert.NotEmpty(result.Errors);
        Assert.NotEmpty(ArchiveRelocation.Plan(Settings(from), directory.GetPath("new")));
    }

    [Fact]
    public void ARetainedArchiveStillHoldingFilesIsPlanned()
    {
        // The case that stranded a real archive: the output root had already moved on, so the
        // current archive folder was empty and everything was sitting in the retained one.
        using var directory = new TestDirectory();
        var retained = directory.GetPath("old", "Emoji");
        var newRoot = directory.GetPath("new");
        Directory.CreateDirectory(retained);
        File.WriteAllText(Path.Combine(retained, "a.png"), "aa");
        File.WriteAllText(Path.Combine(retained, "b.png"), "bbb");

        var settings = Settings(Path.Combine(newRoot, "Emoji"), retained);

        var steps = ArchiveRelocation.Plan(settings, newRoot);

        var step = Assert.Single(steps);
        Assert.Equal(VrcImageCategory.Emoji, step.Category);
        Assert.Equal(retained, step.From);
        Assert.Equal(Path.Combine(newRoot, "Emoji"), step.To);
        Assert.Equal(2, step.FileCount);
        Assert.Equal(5, step.TotalBytes);
    }

    [Fact]
    public void AnArchiveAlreadyInTheDestinationIsNotPlanned()
    {
        using var directory = new TestDirectory();
        var newRoot = directory.GetPath("new");
        var current = Path.Combine(newRoot, "Emoji");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "a.png"), "a");

        var settings = Settings(current);

        Assert.Empty(ArchiveRelocation.Plan(settings, newRoot));
    }

    [Fact]
    public void AnEmptyArchiveIsNothingToMove()
    {
        using var directory = new TestDirectory();
        var old = directory.GetPath("old", "Emoji");
        Directory.CreateDirectory(old);

        Assert.Empty(ArchiveRelocation.Plan(Settings(old), directory.GetPath("new")));
    }

    [Fact]
    public async Task MovingKeepsTheFoldersTheImagesSatIn()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old", "Emoji");
        var to = directory.GetPath("new", "Emoji");
        Directory.CreateDirectory(Path.Combine(from, "2025-05"));
        File.WriteAllText(Path.Combine(from, "loose.png"), "1");
        File.WriteAllText(Path.Combine(from, "2025-05", "nested.png"), "2");
        var step = new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 2, 2);

        var result = await ArchiveRelocation.RelocateAsync([step]);

        Assert.Equal(2, result.Moved);
        Assert.Equal(0, result.LeftBehind);
        Assert.True(File.Exists(Path.Combine(to, "loose.png")));
        Assert.True(File.Exists(Path.Combine(to, "2025-05", "nested.png")));
        Assert.False(File.Exists(Path.Combine(from, "loose.png")));
    }

    [Fact]
    public async Task DifferentContentAtTheDestinationKeepsBothFiles()
    {
        // Two archives can hold different images under one name. The copy already filed is the
        // one the index describes, so the incoming one stays put and is reported rather than
        // quietly overwriting it.
        using var directory = new TestDirectory();
        var from = directory.GetPath("old", "Emoji");
        var to = directory.GetPath("new", "Emoji");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "same.png"), "incoming");
        File.WriteAllText(Path.Combine(to, "same.png"), "already filed");
        var step = new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 1, 8);

        var result = await ArchiveRelocation.RelocateAsync([step]);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.LeftBehind);
        Assert.Equal("already filed", File.ReadAllText(Path.Combine(to, "same.png")));
        Assert.False(File.Exists(Path.Combine(from, "same.png")));
        Assert.Equal("incoming", File.ReadAllText(Assert.Single(result.Moves).To));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task IdenticalDestinationCompletesInterruptedCopyWithoutAnotherFile()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old", "Emoji");
        var to = directory.GetPath("new", "Emoji");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "same.png"), "verified bytes");
        File.WriteAllText(Path.Combine(to, "same.png"), "verified bytes");
        var result = await ArchiveRelocation.RelocateAsync([
            new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 1, 14)]);
        Assert.Equal(1, result.Moved);
        Assert.Empty(result.Errors);
        Assert.Empty(Directory.GetFiles(from));
        Assert.Single(Directory.GetFiles(to));
        Assert.Equal("verified bytes", File.ReadAllText(Path.Combine(to, "same.png")));
    }

    [Fact]
    public void TheIndexFollowsTheFilesRatherThanBeingRebuilt()
    {
        // Rebuilding would mean decoding the whole archive again to rediscover fingerprints it
        // already holds. Repointing the records keeps them, because the sidecar is keyed by id.
        var from = @"C:\old\Emoji";
        var to = @"K:\new\Emoji";
        var state = new AppStateDocument();
        var index = new CategoryIndexState { Category = VrcImageCategory.Emoji };
        state.ArchiveIndex.Categories.Add(index);
        var id = Guid.NewGuid();
        var moved = Path.Combine(from, "2025-05", "a.png");
        index.Images.Add(new IndexedImageRecord { Id = id, Category = VrcImageCategory.Emoji, Path = moved });
        index.Images.Add(new IndexedImageRecord { Id = Guid.NewGuid(), Category = VrcImageCategory.Emoji, Path = @"D:\elsewhere\b.png" });

        var rebased = ArchiveRelocation.RebaseIndex(
            state,
            [new ArchiveRelocationMove(VrcImageCategory.Emoji, moved, Path.Combine(to, "2025-05", "a.png"))]);

        Assert.Equal(1, rebased);
        Assert.Equal(Path.Combine(to, "2025-05", "a.png"), index.Images[0].Path);
        Assert.Equal(id, index.Images[0].Id);
        Assert.Equal(@"D:\elsewhere\b.png", index.Images[1].Path);
        Assert.Equal(1, index.Generation);
    }

    /// <summary>
    /// The audit's fifth finding: a mixed batch rebased every record under the old root, including
    /// the records of files that never moved.
    /// </summary>
    /// <remarks>
    /// The collision is the dangerous half. A different image already filed under the same name at
    /// the destination is exactly what a record must not be pointed at, because the old fingerprint
    /// travels with the record and would then describe somebody else's picture.
    /// </remarks>
    [Fact]
    public async Task OnlyTheFilesThatMovedAreRebased()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old", "Emoji");
        var to = directory.GetPath("new", "Emoji");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        var movedFile = Path.Combine(from, "moved.png");
        var collidingFile = Path.Combine(from, "same.png");
        File.WriteAllText(movedFile, "moved");
        File.WriteAllText(collidingFile, "mine");
        File.WriteAllText(Path.Combine(to, "same.png"), "someone else's");

        var state = new AppStateDocument();
        var index = new CategoryIndexState { Category = VrcImageCategory.Emoji };
        state.ArchiveIndex.Categories.Add(index);
        var movedId = Guid.NewGuid();
        var stayedId = Guid.NewGuid();
        index.Images.Add(new IndexedImageRecord
        {
            Id = movedId,
            Category = VrcImageCategory.Emoji,
            Path = movedFile,
            ExactFingerprint = "moved-fingerprint",
        });
        index.Images.Add(new IndexedImageRecord
        {
            Id = stayedId,
            Category = VrcImageCategory.Emoji,
            Path = collidingFile,
            ExactFingerprint = "mine-fingerprint",
        });

        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            Status = ReviewStatus.Pending,
            HeldFilePath = directory.GetPath("held", "incoming.png"),
        };
        review.Candidates.Add(new ReviewCandidate
        {
            Id = Guid.NewGuid(),
            IndexedImageId = movedId,
            ArchivePath = movedFile,
        });
        review.Candidates.Add(new ReviewCandidate
        {
            Id = Guid.NewGuid(),
            IndexedImageId = stayedId,
            ArchivePath = collidingFile,
        });
        state.ReviewQueue.Add(review);

        // A real transfer failure must leave references untouched. Name collisions now move
        // safely under a different name, so hold this source open instead.
        using var locked = new FileStream(collidingFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await ArchiveRelocation.RelocateAsync(
            [new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 2, 9)]);

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.LeftBehind);
        var recorded = Assert.Single(result.Moves);
        Assert.Equal(movedFile, recorded.From);
        Assert.Equal(Path.Combine(to, "moved.png"), recorded.To);

        Assert.Equal(1, ArchiveRelocation.RebaseIndex(state, result.Moves));

        Assert.Equal(Path.Combine(to, "moved.png"), index.Images.Single(item => item.Id == movedId).Path);
        Assert.Equal(collidingFile, index.Images.Single(item => item.Id == stayedId).Path);
        locked.Dispose();
        Assert.Equal("mine", File.ReadAllText(collidingFile));
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(to, "same.png")));
        Assert.Equal(
            Path.Combine(to, "moved.png"),
            review.Candidates.Single(item => item.IndexedImageId == movedId).ArchivePath);
        Assert.Equal(
            collidingFile,
            review.Candidates.Single(item => item.IndexedImageId == stayedId).ArchivePath);
    }

    /// <summary>
    /// A relocation stopped part-way still has to say what it moved, or the index goes on naming
    /// files that are no longer there and nothing can put it right.
    /// </summary>
    [Fact]
    public async Task AStoppedRelocationStillReportsWhatItMoved()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old", "Emoji");
        var to = directory.GetPath("new", "Emoji");
        Directory.CreateDirectory(from);
        File.WriteAllText(Path.Combine(from, "a.png"), "a");
        File.WriteAllText(Path.Combine(from, "b.png"), "b");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await ArchiveRelocation.RelocateAsync(
            [new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 2, 2)],
            cancellationToken: cancellation.Token);

        Assert.Equal(0, result.Moved);
        Assert.Empty(result.Moves);
        Assert.Single(result.Errors);
        Assert.True(File.Exists(Path.Combine(from, "a.png")));

        // And it says it stopped. Reporting the same thing as a clean finish made an interrupted
        // move look like one that had brought the whole archive across.
        Assert.True(result.Stopped);
    }

    [Fact]
    public void AnEmptiedArchiveStopsBeingTreatedAsOne()
    {
        // This is what lets the folder be scanned again afterwards: while it is still a known
        // archive, scanning it is refused because every file would match itself.
        var settings = new AppSettings();
        settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
        {
            Category = VrcImageCategory.Emoji,
            ArchivePath = @"C:\old\Emoji",
        });
        settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
        {
            Category = VrcImageCategory.Prints,
            ArchivePath = @"C:\old\Prints",
        });

        var removed = ArchiveRelocation.ForgetRelocated(
            settings,
            [new ArchiveRelocationStep(VrcImageCategory.Emoji, @"C:\old\Emoji", @"K:\new\Emoji", 1, 1)]);

        Assert.Equal(1, removed);
        Assert.Equal(@"C:\old\Prints", Assert.Single(settings.LegacyArchiveMappings).ArchivePath);
    }

    [Fact]
    public async Task FailedSourceRemovalCanRetryWithoutDuplicatingVerifiedDestination()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old");
        var to = directory.GetPath("new");
        Directory.CreateDirectory(from);
        var source = Path.Combine(from, "a.png");
        File.WriteAllText(source, "preserve these bytes");
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        File.SetAttributes(source, FileAttributes.ReadOnly);
        var step = new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 1, 20);
        try
        {
            var failed = await ArchiveRelocation.RelocateAsync([step]);
            Assert.Equal(0, failed.Moved);
            Assert.Single(failed.Errors);
            Assert.Equal("preserve these bytes", File.ReadAllText(source));
            Assert.Equal("preserve these bytes", File.ReadAllText(Path.Combine(to, "a.png")));
        }
        finally { File.SetAttributes(source, FileAttributes.Normal); }
        var retried = await ArchiveRelocation.RelocateAsync([step]);
        Assert.Equal(1, retried.Moved);
        Assert.Empty(retried.Errors);
        Assert.Single(Directory.GetFiles(to));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(to, "a.png")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task LockedDestinationLeavesSourceIntactAndRetryCompletes()
    {
        using var directory = new TestDirectory();
        var from = directory.GetPath("old");
        var to = directory.GetPath("new");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        var source = Path.Combine(from, "a.png");
        var destination = Path.Combine(to, "a.png");
        File.WriteAllText(source, "same");
        File.WriteAllText(destination, "same");
        var step = new ArchiveRelocationStep(VrcImageCategory.Emoji, from, to, 1, 4);
        using (var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = await ArchiveRelocation.RelocateAsync([step]);
            Assert.Equal(0, failed.Moved);
            Assert.Single(failed.Errors);
            Assert.Equal("same", File.ReadAllText(source));
        }
        Assert.Equal(1, (await ArchiveRelocation.RelocateAsync([step])).Moved);
        Assert.Single(Directory.GetFiles(to));
    }

    [Fact]
    public void EmptyNestedFoldersAreVerifiedEmptyButMissingFoldersAreNot()
    {
        using var directory = new TestDirectory();
        var root = directory.GetPath("old");
        var nested = Path.Combine(root, "Animated", "Gif Ref");
        Directory.CreateDirectory(nested);
        Assert.True(ArchiveRelocation.IsVerifiedEmpty(root));
        File.WriteAllText(Path.Combine(nested, "remaining.png"), "remaining");
        Assert.False(ArchiveRelocation.IsVerifiedEmpty(root));
        Assert.False(ArchiveRelocation.IsVerifiedEmpty(directory.GetPath("missing")));
    }

    [Fact]
    public void ForgetUnavailableRemovesOnlyOldMetadataAndPreservesPendingReview()
    {
        using var directory = new TestDirectory();
        var old = directory.GetPath("missing");
        var current = directory.GetPath("current");
        Directory.CreateDirectory(current);
        var state = new AppStateDocument { Settings = Settings(current, old) };
        var oldImage = Path.Combine(old, "a.png");
        var kept = Path.Combine(current, "b.png");
        File.WriteAllText(kept, "keep");
        var index = new CategoryIndexState { Category = VrcImageCategory.Emoji };
        index.Images.Add(new IndexedImageRecord { Path = oldImage });
        index.Images.Add(new IndexedImageRecord { Path = kept });
        state.ArchiveIndex.Categories.Add(index);
        var review = new ReviewItem { HeldFilePath = directory.GetPath("incoming.png") };
        review.Candidates.Add(new ReviewCandidate { ArchivePath = oldImage });
        state.ReviewQueue.Add(review);
        Assert.Equal(1, ArchiveRelocation.ForgetUnavailable(state, old));
        Assert.Empty(state.Settings.LegacyArchiveMappings);
        Assert.Equal(kept, Assert.Single(index.Images).Path);
        Assert.Equal(IndexStatus.Stale, index.Status);
        Assert.Equal(ReviewStatus.NeedsReconciliation, review.Status);
        Assert.Empty(review.Candidates);
        Assert.Single(state.ReviewQueue);
        Assert.Equal("keep", File.ReadAllText(kept));
    }

    [Fact]
    public void ForgetUnavailableRefusesExistingFolderAndPendingOperations()
    {
        using var directory = new TestDirectory();
        var old = directory.GetPath("old");
        Directory.CreateDirectory(old);
        var state = new AppStateDocument { Settings = Settings(directory.GetPath("new"), old) };
        Assert.Throws<InvalidOperationException>(() => ArchiveRelocation.ForgetUnavailable(state, old));
        Directory.Delete(old);
        state.OperationJournal.Add(new JournalEntry { SourcePath = Path.Combine(old, "a.png") });
        Assert.Throws<InvalidOperationException>(() => ArchiveRelocation.ForgetUnavailable(state, old));
        Assert.Single(state.Settings.LegacyArchiveMappings);
    }

    private static AppSettings Settings(string currentArchive, string? retainedArchive = null)
    {
        var settings = new AppSettings();
        settings.CategoryMappings.Add(new CategoryMapping
        {
            Category = VrcImageCategory.Emoji,
            IsEnabled = true,
            SourcePath = @"C:\incoming\Emoji",
            ArchivePath = currentArchive,
        });

        if (retainedArchive is not null)
        {
            settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
            {
                Category = VrcImageCategory.Emoji,
                ArchivePath = retainedArchive,
            });
        }

        return settings;
    }
}
