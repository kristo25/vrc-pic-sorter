using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VrcPicSorter.Core.FileSystem;

/// <summary>Copy, flush and verify before removing the locked source file.</summary>
internal static class VerifiedArchiveTransfer
{
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
