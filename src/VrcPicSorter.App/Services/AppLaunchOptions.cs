using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VrcPicSorter.App.Services;

public sealed record AppLaunchOptions(bool Background, string? DataDirectory)
{
    public bool VerifyCodec { get; init; }
    public bool IsIsolated => DataDirectory is not null;

    public string InstanceName
    {
        get
        {
            if (DataDirectory is null)
            {
                return "VrcPicSorter";
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant()));
            return $"VrcPicSorter-{Convert.ToHexString(hash.AsSpan(0, 8))}";
        }
    }

    public static AppLaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 1 && arguments[0].Equals("--verify-codec", StringComparison.OrdinalIgnoreCase))
        {
            return new AppLaunchOptions(false, null) { VerifyCodec = true };
        }

        var background = false;
        string? dataDirectory = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.Equals("--background", StringComparison.OrdinalIgnoreCase))
            {
                background = true;
                continue;
            }

            if (argument.Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                {
                    throw new ArgumentException("--data-dir requires an absolute folder path.");
                }

                if (!Path.IsPathFullyQualified(arguments[index]))
                {
                    throw new ArgumentException("--data-dir must be an absolute folder path.");
                }

                dataDirectory = Path.GetFullPath(arguments[index]);
                continue;
            }

            throw new ArgumentException($"Unknown command-line argument: {argument}");
        }

        return new AppLaunchOptions(background, dataDirectory);
    }
}
