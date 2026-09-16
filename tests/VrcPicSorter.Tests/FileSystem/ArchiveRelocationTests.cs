using System.IO;
using VrcPicSorter.Core.FileSystem;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.FileSystem;

public sealed class ArchiveRelocationTests
{
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
    public async Task AFileAlreadyAtTheDestinationIsLeftWhereItIs()
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

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.LeftBehind);
        Assert.Equal("already filed", File.ReadAllText(Path.Combine(to, "same.png")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(from, "same.png")));
        Assert.Single(result.Errors);
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
