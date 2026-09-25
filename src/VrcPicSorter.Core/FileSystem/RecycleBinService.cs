using Microsoft.VisualBasic.FileIO;

namespace VrcPicSorter.Core.FileSystem;

public interface IRecycleBinService
{
    bool CanRecycle(string path);

    Task RecycleAsync(string path, CancellationToken cancellationToken = default);
}

/// <param name="recycleBinDisabledForVolume">
/// Answers whether Windows has been told to delete permanently on a given volume root. Left out,
/// the question is not asked and only the drive type is considered - which is what this did before,
/// and what made it possible to destroy a file while reporting a recycle.
/// </param>
public sealed class WindowsRecycleBinService(
    Func<string, bool>? recycleBinDisabledForVolume = null,
    Func<string, DriveType>? driveType = null,
    Action<string>? recycleFile = null,
    string? stagingRoot = null) : IRecycleBinService
{
    private readonly string _stagingRoot = stagingRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcPicSorter-RecycleStaging");

    private DriveType TypeOf(string root)
    {
        if (driveType is not null) return driveType(root);
        if (root.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || (root.StartsWith(@"\\", StringComparison.Ordinal)
                && !root.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !root.StartsWith(@"\\.\", StringComparison.Ordinal)))
            return DriveType.Network;
        return new DriveInfo(root.StartsWith(@"\\?\", StringComparison.Ordinal) ? root[4..] : root).DriveType;
    }

    public bool CanRecycle(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            if (root is null) return false;
            if (TypeOf(root) == DriveType.Network)
            {
                var localRoot = Path.GetPathRoot(_stagingRoot);
                return Path.IsPathFullyQualified(_stagingRoot) && localRoot is not null
                    && TypeOf(localRoot) == DriveType.Fixed
                    && recycleBinDisabledForVolume?.Invoke(localRoot) != true;
            }
            if (TypeOf(root) != DriveType.Fixed)
            {
                return false;
            }

            // A drive can be set to "Don't move files to the Recycle Bin. Remove files immediately
            // when deleted", and Windows then deletes permanently while still reporting success.
            // Everything that recycles here does so on the app's own initiative - an exact
            // duplicate resolved without asking - so on such a drive the one rule the app has,
            // that it never destroys a picture, was being broken silently. A drive that says so is
            // treated as having no Recycle Bin at all, and every caller already answers that by
            // asking the user instead of deleting.
            return recycleBinDisabledForVolume?.Invoke(root) != true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public Task RecycleAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!CanRecycle(path))
        {
            throw new NotSupportedException("Windows Recycle Bin behavior is unavailable for this path.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (TypeOf(Path.GetPathRoot(path)!) == DriveType.Network)
            return Task.Run(() => VerifiedArchiveTransfer.RecycleThroughLocalCopyAsync(path, _stagingRoot,
                localCopy =>
                {
                    if (!CanRecycle(localCopy)) throw new NotSupportedException("The local Recycle Bin is unavailable; the network original was kept.");
                    RecycleLocal(localCopy);
                }, cancellationToken), cancellationToken);
        RecycleLocal(path);
        return Task.CompletedTask;
    }

    private void RecycleLocal(string path)
    {
        if (recycleFile is not null) { recycleFile(path); return; }
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
    }
}
