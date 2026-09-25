using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VrcPicSorter.Core.FileSystem;

/// <summary>Copy, flush and verify before removing the locked source file.</summary>
internal static class VerifiedArchiveTransfer
{
    public static async Task RecycleThroughLocalCopyAsync(string source, string stagingRoot, Action<string> recycle,
        CancellationToken cancellationToken)
    {
        PathBoundary.EnsureNoReparsePoints(source, "Recycling source");
        PathBoundary.EnsureNoReparsePoints(stagingRoot, "Local recycling folder");
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = CreateFile(source, 0x80000000 | 0x00010000, 1, IntPtr.Zero, 3, 0x48000000, IntPtr.Zero);
        if (handle.IsInvalid) throw Error("Cannot lock recycling source");
        await using var input = new FileStream(handle, FileAccess.Read, 65536, isAsync: true);
        cancellationToken.ThrowIfCancellationRequested();
        var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        // Stable staging identity permits safe retry without accumulating full local copies.
        var identity = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant()
            + "\n" + Convert.ToHexString(hash));
        var folder = Path.Combine(stagingRoot, Convert.ToHexString(SHA256.HashData(identity)));
        var localCopy = Path.Combine(folder, Path.GetFileName(source));
        PathBoundary.EnsureSafeDestination(stagingRoot, localCopy, "Local recycling copy");
        Directory.CreateDirectory(folder);
        // Keep origin information alongside staging: Restore in Windows returns the copy here,
        // not to a network share which may no longer be connected.
        await File.WriteAllTextAsync(localCopy + ".origin.txt", source, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(localCopy))
        {
            var partial = localCopy + ".partial";
            PathBoundary.EnsureNoReparsePoints(partial, "Local recycling temporary file");
            try
            {
                // A prior interrupted attempt's partial file is disposable; the source is locked.
                await using (var copy = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite,
                    FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await CopyAndVerifyAsync(input, copy, hash, cancellationToken).ConfigureAwait(false);
                    copy.Flush(flushToDisk: true);
                }
                File.Move(partial, localCopy, overwrite: false);
            }
            finally
            {
                // Keep the verified final copy for retry, but never accumulate incomplete copies.
                try { File.Delete(partial); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        PathBoundary.EnsureNoReparsePoints(localCopy, "Local recycling copy");
        await using (var verified = new FileStream(localCopy, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.Asynchronous))
            if (verified.Length != input.Length || !(await SHA256.HashDataAsync(verified, cancellationToken)
                .ConfigureAwait(false)).AsSpan().SequenceEqual(hash))
                throw new IOException("Local recycling copy verification failed; the original was kept.");
        File.SetLastWriteTimeUtc(localCopy, File.GetLastWriteTimeUtc(source));
        cancellationToken.ThrowIfCancellationRequested();
        recycle(localCopy); // Failure leaves the locked original intact.
        if (File.Exists(localCopy)) throw new IOException("Local copy was not recycled; the original was kept.");
        DeleteLockedSource(handle);
    }

    internal static async Task CopyAndVerifyAsync(Stream input, Stream copy, byte[] hash, CancellationToken token)
    {
        input.Position = 0;
        await input.CopyToAsync(copy, 65536, token).ConfigureAwait(false);
        await copy.FlushAsync(token).ConfigureAwait(false);
        copy.Position = 0;
        if (copy.Length != input.Length || !(await SHA256.HashDataAsync(copy, token).ConfigureAwait(false))
            .AsSpan().SequenceEqual(hash))
            throw new IOException("Local recycling copy verification failed; the original was kept.");
    }

    public static string Move(string source, string destinationRoot, string requestedDestination)
    {
        // DELETE access lets us remove this exact open file, not whatever later occupies its path.
        // Read-only sharing prevents another process changing or replacing either verified copy.
        using var handle = CreateFile(source, 0x80000000 | 0x00010000, 1, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid) throw Error("Cannot lock relocation source");
        using var input = new FileStream(handle, FileAccess.Read);
        var hash = SHA256.HashData(input);
        var suffix = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        string? temporary = null;
        try
        {
            for (var attempt = 0; attempt < 10000; attempt++)
            {
                var destination = attempt == 0 ? requestedDestination : Path.Combine(
                    Path.GetDirectoryName(requestedDestination)!,
                    $"{Path.GetFileNameWithoutExtension(requestedDestination)}.relocated-{suffix}"
                    + (attempt == 1 ? "" : $"-{attempt}") + Path.GetExtension(requestedDestination));
                PathBoundary.EnsureSafeDestination(destinationRoot, destination, "Relocation destination");
                if (Directory.Exists(destination)) continue;
                if (File.Exists(destination))
                {
                    using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (existing.Length != input.Length || !SHA256.HashData(existing).AsSpan().SequenceEqual(hash)) continue;
                    DeleteLockedSource(handle);
                    input.Dispose(); // Complete deletion while the verified destination is still locked.
                    return destination;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (temporary is null)
                {
                    temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".vrc-relocate-{Guid.NewGuid():N}.partial");
                    PathBoundary.EnsureSafeDestination(destinationRoot, temporary, "Relocation temporary file");
                    using (var copy = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        input.Position = 0;
                        input.CopyTo(copy);
                        copy.Flush(flushToDisk: true);
                        copy.Position = 0;
                        if (copy.Length != input.Length || !SHA256.HashData(copy).AsSpan().SequenceEqual(hash))
                            throw new IOException("Relocation copy verification failed; the source was kept.");
                    }
                    File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
                }

                PathBoundary.EnsureSafeDestination(destinationRoot, destination, "Relocation destination");
                try { File.Move(temporary, destination, overwrite: false); }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { continue; }
                temporary = null;
                // Reopen the final name and verify it before allowing source removal. A failed
                // connection leaves the source intact; retry recognizes the completed copy.
                using var verified = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (verified.Length != input.Length || !SHA256.HashData(verified).AsSpan().SequenceEqual(hash))
                    throw new IOException("Destination changed during relocation; the source was kept.");
                DeleteLockedSource(handle);
                input.Dispose();
                return destination;
            }
            throw new IOException("No available relocation filename; the source was kept.");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void DeleteLockedSource(SafeFileHandle handle)
    {
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw Error("Verified destination is complete, but source removal failed; retry the move");
    }

    private static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition
    {
        [MarshalAs(UnmanagedType.U1)] public bool DeleteFile;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDisposition information, uint size);
}
