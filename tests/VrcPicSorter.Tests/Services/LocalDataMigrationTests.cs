using System.IO;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Models;

namespace VrcPicSorter.Tests.Services;

public sealed class LocalDataMigrationTests
{
    [Fact]
    public void TheOldFolderIsCarriedOverWhenThereIsNoNewOne()
    {
        using var directory = new TestDirectory();
        var previous = directory.GetPath("VrcImageCurator");
        var current = directory.GetPath("VrcPicSorter");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "state.json"), "{\"SchemaVersion\":5}");

        Assert.True(LocalDataMigration.CarryOver(previous, current));

        Assert.False(Directory.Exists(previous));
        Assert.Equal("{\"SchemaVersion\":5}", File.ReadAllText(Path.Combine(current, "state.json")));
    }

    [Fact]
    public void AnExistingNewFolderIsNeverOverwritten()
    {
        // Both present means the application has already run under the new name. Merging would
        // mean choosing between two state documents, and the newer name's is the live one.
        using var directory = new TestDirectory();
        var previous = directory.GetPath("VrcImageCurator");
        var current = directory.GetPath("VrcPicSorter");
        Directory.CreateDirectory(previous);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(previous, "state.json"), "old");
        File.WriteAllText(Path.Combine(current, "state.json"), "live");

        Assert.False(LocalDataMigration.CarryOver(previous, current));

        Assert.Equal("live", File.ReadAllText(Path.Combine(current, "state.json")));
        Assert.True(Directory.Exists(previous));
    }

    [Fact]
    public void NothingToCarryIsNotAFailure()
    {
        using var directory = new TestDirectory();

        Assert.False(LocalDataMigration.CarryOver(
            directory.GetPath("VrcImageCurator"),
            directory.GetPath("VrcPicSorter")));
    }

    [Fact]
    public void AHoldingFolderInsideTheOldDataFolderIsRepointedAtTheNewOne()
    {
        // The bug this exists for. Moving the folder is only half the job: the holding root is
        // stored absolute, so it went on naming a folder that the move had just emptied, and
        // clearing local data refuses outright when that path sits outside the data folder.
        var state = new AppStateDocument
        {
            Settings = new AppSettings
            {
                HoldingRootPath = @"C:\Users\someone\AppData\Local\VrcImageCurator\Holding",
                OutputRootPath = @"K:\Pictures\VRChat Archive",
            },
        };

        Assert.True(LocalDataMigration.NeedsRebase(
            state,
            @"C:\Users\someone\AppData\Local\VrcImageCurator",
            @"C:\Users\someone\AppData\Local\VrcPicSorter"));
        Assert.Equal(
            1,
            LocalDataMigration.RebasePaths(
                state,
                @"C:\Users\someone\AppData\Local\VrcImageCurator",
                @"C:\Users\someone\AppData\Local\VrcPicSorter"));

        Assert.Equal(@"C:\Users\someone\AppData\Local\VrcPicSorter\Holding", state.Settings.HoldingRootPath);
    }

    [Fact]
    public void FoldersOutsideTheOldDataFolderAreLeftAlone()
    {
        // Someone's archive on another drive has nothing to do with what this application is
        // called, and rewriting it would move their files out from under them.
        var state = new AppStateDocument
        {
            Settings = new AppSettings
            {
                HoldingRootPath = @"K:\Kissou Stuff\Holding",
                OutputRootPath = @"K:\Kissou Stuff\! VRChat Picture\Other Image From VRC",
            },
        };
        state.Settings.CategoryMappings.Add(new CategoryMapping
        {
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Users\someone\OneDrive\Images\VRChat\Emoji",
            ArchivePath = @"K:\Kissou Stuff\! VRChat Picture\Other Image From VRC\Emoji",
        });
        state.ReviewQueue.Add(new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            HeldFilePath = @"K:\Kissou Stuff\Holding\Emoji\held.png",
            IncomingOriginalPath = @"C:\Users\someone\OneDrive\Images\VRChat\Emoji\held.png",
        });

        Assert.False(LocalDataMigration.NeedsRebase(
            state,
            @"C:\Users\someone\AppData\Local\VrcImageCurator",
            @"C:\Users\someone\AppData\Local\VrcPicSorter"));
        Assert.Equal(
            0,
            LocalDataMigration.RebasePaths(
                state,
                @"C:\Users\someone\AppData\Local\VrcImageCurator",
                @"C:\Users\someone\AppData\Local\VrcPicSorter"));

        Assert.Equal(@"K:\Kissou Stuff\Holding", state.Settings.HoldingRootPath);
        Assert.Equal(@"C:\Users\someone\OneDrive\Images\VRChat\Emoji", state.Settings.CategoryMappings[0].SourcePath);
        Assert.Equal(@"K:\Kissou Stuff\Holding\Emoji\held.png", state.ReviewQueue[0].HeldFilePath);
    }

    [Fact]
    public void AnIsolatedProfileUnderTheOldFolderIsCarriedAcrossWholesale()
    {
        // --data-dir puts everything under one folder, so a profile that lived under the old data
        // folder has every one of its paths rewritten rather than just the holding root.
        const string previous = @"C:\Users\someone\AppData\Local\VrcImageCurator";
        const string current = @"C:\Users\someone\AppData\Local\VrcPicSorter";
        var state = new AppStateDocument
        {
            Settings = new AppSettings
            {
                HoldingRootPath = previous + @"\Holding",
                OutputRootPath = previous + @"\Archive",
            },
        };
        state.Settings.CategoryMappings.Add(new CategoryMapping
        {
            Category = VrcImageCategory.Prints,
            SourcePath = previous + @"\Profile\Prints",
            ArchivePath = previous + @"\Archive\Prints",
        });
        state.Settings.LegacyArchiveMappings.Add(new LegacyArchiveMapping
        {
            Category = VrcImageCategory.Prints,
            ArchivePath = previous + @"\Old\Prints",
        });

        Assert.Equal(5, LocalDataMigration.RebasePaths(state, previous, current));

        Assert.Equal(current + @"\Holding", state.Settings.HoldingRootPath);
        Assert.Equal(current + @"\Archive", state.Settings.OutputRootPath);
        Assert.Equal(current + @"\Profile\Prints", state.Settings.CategoryMappings[0].SourcePath);
        Assert.Equal(current + @"\Archive\Prints", state.Settings.CategoryMappings[0].ArchivePath);
        Assert.Equal(current + @"\Old\Prints", state.Settings.LegacyArchiveMappings[0].ArchivePath);
    }

    /// <summary>
    /// The audit's seventh finding: the folder moved, the settings were repaired, and everything
    /// else went on naming a folder that no longer existed.
    /// </summary>
    /// <remarks>
    /// A held review pointing at the vanished name could not be decided, restored or dismissed -
    /// every one of those verifies the held file first. An unfinished operation naming it could not
    /// be reconciled either, so it sat in Needs attention with nothing a person could do about it.
    /// </remarks>
    [Fact]
    public void HeldReviewsAndUnfinishedOperationsAreCarriedAcrossToo()
    {
        const string previous = @"C:\Users\someone\AppData\Local\VrcImageCurator";
        const string current = @"C:\Users\someone\AppData\Local\VrcPicSorter";
        var state = new AppStateDocument
        {
            Settings = new AppSettings
            {
                HoldingRootPath = previous + @"\Holding",
                OutputRootPath = @"K:\Pictures\VRChat Archive",
            },
        };

        var index = new CategoryIndexState { Category = VrcImageCategory.Emoji };
        index.Images.Add(new IndexedImageRecord
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            Path = @"K:\Pictures\VRChat Archive\Emoji\kept.png",
        });
        state.ArchiveIndex.Categories.Add(index);

        var review = new ReviewItem
        {
            Id = Guid.NewGuid(),
            Category = VrcImageCategory.Emoji,
            Status = ReviewStatus.Pending,
            HeldFilePath = previous + @"\Holding\Emoji\incoming.png",
            IncomingOriginalPath = @"C:\Users\someone\OneDrive\Images\VRChat\Emoji\incoming.png",
            KeptIncomingArchivedPath = previous + @"\Holding\Emoji\archived.png",
        };
        review.RoutingContext.SourceRootPath = @"C:\Users\someone\OneDrive\Images\VRChat\Emoji";
        review.RoutingContext.OutputRootPath = previous + @"\Archive";
        review.Candidates.Add(new ReviewCandidate
        {
            Id = Guid.NewGuid(),
            IndexedImageId = index.Images[0].Id,
            ArchivePath = @"K:\Pictures\VRChat Archive\Emoji\kept.png",
        });
        state.ReviewQueue.Add(review);

        state.OperationJournal.Add(new JournalEntry
        {
            Id = Guid.NewGuid(),
            OperationType = JournalOperationType.Move,
            Purpose = JournalOperationPurpose.HoldForReview,
            Phase = JournalPhase.SideEffectStarted,
            Category = VrcImageCategory.Emoji,
            SourcePath = @"C:\Users\someone\OneDrive\Images\VRChat\Emoji\second.png",
            DestinationPath = previous + @"\Holding\Emoji\second.png",
            ReviewItemAfterCommit = new ReviewItem
            {
                Id = Guid.NewGuid(),
                Category = VrcImageCategory.Emoji,
                HeldFilePath = previous + @"\Holding\Emoji\second.png",
                IncomingOriginalPath = @"C:\Users\someone\OneDrive\Images\VRChat\Emoji\second.png",
            },
        });

        Assert.True(LocalDataMigration.NeedsRebase(state, previous, current));
        Assert.Equal(6, LocalDataMigration.RebasePaths(state, previous, current));

        Assert.Equal(current + @"\Holding", state.Settings.HoldingRootPath);
        Assert.Equal(current + @"\Holding\Emoji\incoming.png", review.HeldFilePath);
        Assert.Equal(current + @"\Holding\Emoji\archived.png", review.KeptIncomingArchivedPath);
        Assert.Equal(current + @"\Archive", review.RoutingContext.OutputRootPath);
        Assert.Equal(current + @"\Holding\Emoji\second.png", state.OperationJournal[0].DestinationPath);
        Assert.Equal(
            current + @"\Holding\Emoji\second.png",
            state.OperationJournal[0].ReviewItemAfterCommit!.HeldFilePath);

        // Untouched: an archive on another drive has nothing to do with what the app is called.
        Assert.Equal(@"K:\Pictures\VRChat Archive", state.Settings.OutputRootPath);
        Assert.Equal(@"K:\Pictures\VRChat Archive\Emoji\kept.png", index.Images[0].Path);
        Assert.Equal(
            @"C:\Users\someone\OneDrive\Images\VRChat\Emoji\incoming.png",
            review.IncomingOriginalPath);

        // Running it again finds nothing left to do, however the first run was interrupted.
        Assert.False(LocalDataMigration.NeedsRebase(state, previous, current));
        Assert.Equal(0, LocalDataMigration.RebasePaths(state, previous, current));
    }

    [Fact]
    public void AFreshInstallIsLeftWithNothingToDo()
    {
        // The overwhelmingly common case once the rename is behind everyone: no old folder, and
        // the new one created by the state store a moment later.
        using var directory = new TestDirectory();
        var current = directory.GetPath("VrcPicSorter");

        Assert.False(LocalDataMigration.CarryOver(directory.GetPath("VrcImageCurator"), current));

        Assert.False(Directory.Exists(current));
    }
}
