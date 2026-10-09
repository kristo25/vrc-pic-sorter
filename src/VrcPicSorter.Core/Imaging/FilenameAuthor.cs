using System.Globalization;
using System.Text.RegularExpressions;

namespace VrcPicSorter.Core.Imaging;

/// <summary>Reads the complete filename author, not an account identity.</summary>
public static partial class FilenameAuthor
{
    public static bool IsExactMatch(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name)) return false;
        var extension = Path.GetExtension(path);
        if (!new[] { ".png", ".gif", ".jpg", ".jpeg", ".webp", ".bmp" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;

        var stem = Path.GetFileNameWithoutExtension(path);
        var markers = Marker().Matches(stem);
        if (markers.Count != 1) return false;
        var marker = markers[0];
        if (marker.Index == 0) return false;
        var timestamp = marker.Groups["timestamp"].Value;
        if (timestamp.Length != 0 && !DateTime.TryParseExact(timestamp,
                ["yyyy-MM-dd_HH-mm-ss.fff", "yyyy-MM-dd_HH-mm-ss"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
        if (marker.Groups["kind"].Value == "prnt" && timestamp.Length == 0) return false;
        var suffix = stem[(marker.Index + marker.Length)..];
        if (!Suffix().IsMatch(suffix)) return false;
        // Print and timestamped sticker names do not carry emoji animation descriptors.
        if (timestamp.Length != 0 && suffix.StartsWith('_')) return false;
        return string.Equals(stem[..marker.Index], name, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"_(?:(?<timestamp>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(?:\.\d{3})?)_)?(?<kind>prnt|inv)_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^(?:_[a-zA-Z]+animationStyle(?:_[1-9]\d*frames_[1-9]\d*fps_(?:linear|pingpong)loopStyle)?)?(?: \([1-9]\d*\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Suffix();
}
