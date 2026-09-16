namespace VrcPicSorter.Core.FileSystem;

public static class PathBoundary
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Contains(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Path.GetFullPath(candidate);

        // A volume root keeps its separator through Normalize - "D:\" stays "D:\" - so appending
        // one here would search for "D:\\" and match nothing. A whole drive chosen as the output
        // root would then appear to contain no folder at all, which turns the overlap guards off
        // and makes every archive destination look like an escape.
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedRoot, Path.TrimEndingDirectorySeparator(normalizedCandidate), StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);

    public static void EnsureContained(string root, string candidate, string description)
    {
        if (!Contains(root, candidate))
        {
            throw new InvalidOperationException($"{description} escapes its allowed folder.");
        }
    }

    /// <summary>
    /// Refuses a path that a link would send somewhere else, whether the link stands in for the
    /// file itself or for any folder above it.
    /// </summary>
    /// <remarks>
    /// The walk does not stop at a folder that is missing. A file about to be written usually has
    /// no parent yet - that is what CreateDirectory is for - and stopping there skipped every
    /// existing folder higher up, which is exactly where a junction would be waiting: an Animated
    /// junction under an archive that has never held an export would have passed this check and
    /// then taken the export out of the archive entirely.
    /// </remarks>
    /// <param name="stopAt">
    /// The highest folder worth inspecting, when the caller has one. Everything above a folder the
    /// caller has already vouched for is that person's filesystem rather than this operation's
    /// business - an archive kept on a mounted volume or under a redirected home folder is a
    /// perfectly ordinary arrangement, and walking past it would refuse every write they make.
    /// </param>
    public static void EnsureNoReparsePoints(string path, string description, string? stopAt = null)
    {
        if (File.Exists(path) && IsRedirectingLink(new FileInfo(path)))
        {
            throw new InvalidOperationException($"{description} cannot use a symbolic link: {Path.GetFullPath(path)}");
        }

        var boundary = string.IsNullOrWhiteSpace(stopAt) ? null : Normalize(stopAt);
        var current = new DirectoryInfo(Directory.Exists(path) ? Normalize(path) : Path.GetDirectoryName(Path.GetFullPath(path))!);
        while (current is not null)
        {
            if (current.Exists && IsRedirectingLink(current))
            {
                throw new InvalidOperationException($"{description} cannot use a junction or symbolic link: {current.FullName}");
            }

            if (boundary is not null
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    boundary,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = current.Parent;
        }
    }

    /// <summary>
    /// Refuses to write to <paramref name="path"/> when a link would redirect the bytes, and when
    /// the place they would land is outside <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Checking the source of an operation is only ever half of it. A junction standing in for the
    /// destination folder redirects a finished file just as effectively as one standing in for the
    /// file being read, and the file that lands somewhere unexpected is the one nobody goes looking
    /// for. Applied to final outputs, the temporary file an output is built as, and anything a move
    /// replaces.
    /// </remarks>
    public static void EnsureSafeDestination(string root, string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        EnsureContained(root, path, description);
        EnsureNoReparsePoints(path, description, stopAt: root);
    }

    public static IReadOnlyList<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var normalizedRoot = Normalize(root);
        EnsureNoReparsePoints(normalizedRoot, "Scanned folder");
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                EnsureContained(normalizedRoot, file, "Scanned file");
                if (!IsRedirectingLink(new FileInfo(file)))
                {
                    files.Add(file);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsRedirectingLink(new DirectoryInfo(directory)))
                {
                    continue;
                }

                EnsureContained(normalizedRoot, directory, "Scanned directory");
                pending.Push(directory);
            }
        }

        return files;
    }

    private static bool IsRedirectingLink(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return item.LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
