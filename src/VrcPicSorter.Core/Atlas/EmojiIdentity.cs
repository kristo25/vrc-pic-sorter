using System.Text.RegularExpressions;

namespace VrcPicSorter.Core.Atlas;

/// <summary>
/// Which emoji a file is of, rather than what it happens to be called.
/// </summary>
/// <remarks>
/// A file name is not an identity. Windows appends " (2)" to one that is already taken, a sync
/// client adds its own, a person renames a folder - and every time that happened the app lost track
/// of what it had already made and made it again beside the copy it could no longer see. VRChat
/// writes the emoji's own inventory id into the name, and that part never drifts, so it is what
/// the app asks about now.
/// </remarks>
public static class EmojiIdentity
{
    private static readonly Regex InventoryId = new(
        @"_inv_(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Trailing " (2)", " (3)" and so on, however many have accumulated.</summary>
    private static readonly Regex CopySuffix = new(
        @"\s*\(\d{1,4}\)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The emoji's own id, or null for a file that does not carry one - a renamed sheet, or an
    /// image that never came from VRChat at all.
    /// </summary>
    public static string? TryReadInventoryId(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return null;
        }

        try
        {
            var match = InventoryId.Match(Path.GetFileName(fileNameOrPath));
            return match.Success ? match.Groups["id"].Value : null;
        }
        catch (Exception exception) when (exception is RegexMatchTimeoutException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// What to group a file by. The inventory id when it has one; otherwise its name with any
    /// accumulated copy numbers taken off, so a file and the " (2)" the archiver made of it still
    /// count as the same thing. Compare with <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    public static string KeyFor(string fileNameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameOrPath);
        if (TryReadInventoryId(fileNameOrPath) is { } id)
        {
            return id;
        }

        var name = Path.GetFileNameWithoutExtension(fileNameOrPath);
        try
        {
            string trimmed;
            while ((trimmed = CopySuffix.Replace(name, string.Empty)) != name)
            {
                name = trimmed;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Path.GetFileNameWithoutExtension(fileNameOrPath);
        }

        return string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fileNameOrPath) : name;
    }

    /// <summary>
    /// Whether a copy number has been put on this name - by the archiver when the name it wanted
    /// was taken, or by Windows or a sync client for the same reason.
    /// </summary>
    public static bool HasCopySuffix(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return false;
        }

        try
        {
            return CopySuffix.IsMatch(Path.GetFileNameWithoutExtension(fileNameOrPath));
        }
        catch (Exception exception) when (exception is RegexMatchTimeoutException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether two files are of the same emoji.</summary>
    public static bool IsSameEmoji(string first, string second) =>
        !string.IsNullOrWhiteSpace(first)
        && !string.IsNullOrWhiteSpace(second)
        && KeyFor(first).Equals(KeyFor(second), StringComparison.OrdinalIgnoreCase);
}
