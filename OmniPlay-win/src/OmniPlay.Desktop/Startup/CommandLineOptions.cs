using System.Globalization;

namespace OmniPlay.Desktop.Startup;

public sealed class CommandLineOptions
{
    public static CommandLineOptions Current { get; private set; } = new();

    public string? PlayFilePath { get; init; }

    public string? OverlayPlayFilePath { get; init; }

    public TimeSpan? CloseAfter { get; init; }

    public bool HasStandalonePlaybackRequest => !string.IsNullOrWhiteSpace(PlayFilePath);

    public bool HasOverlayPlaybackRequest => !string.IsNullOrWhiteSpace(OverlayPlayFilePath);

    public bool HasPlaybackRequest => HasStandalonePlaybackRequest || HasOverlayPlaybackRequest;

    public static CommandLineOptions Initialize(string[] args)
    {
        Current = Parse(args);
        return Current;
    }

    private static CommandLineOptions Parse(string[] args)
    {
        string? playFilePath = null;
        string? overlayPlayFilePath = null;
        TimeSpan? closeAfter = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (TryReadValue(args, ref i, arg, "--play-file", out var playFileValue))
            {
                playFilePath = NormalizePath(playFileValue);
                continue;
            }

            if (TryReadValue(args, ref i, arg, "--overlay-play-file", out var overlayPlayFileValue))
            {
                overlayPlayFilePath = NormalizePath(overlayPlayFileValue);
                continue;
            }

            if (TryReadValue(args, ref i, arg, "--close-after", out var closeAfterValue)
                && double.TryParse(closeAfterValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                closeAfter = TimeSpan.FromSeconds(seconds);
            }
        }

        return new CommandLineOptions
        {
            PlayFilePath = playFilePath,
            OverlayPlayFilePath = overlayPlayFilePath,
            CloseAfter = closeAfter
        };
    }

    private static bool TryReadValue(string[] args, ref int index, string currentArg, string optionName, out string value)
    {
        if (currentArg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = currentArg[(optionName.Length + 1)..];
            return true;
        }

        if (string.Equals(currentArg, optionName, StringComparison.OrdinalIgnoreCase)
            && index + 1 < args.Length)
        {
            index++;
            value = args[index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string NormalizePath(string rawValue)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawValue.Trim().Trim('"'));
        return Path.GetFullPath(expanded);
    }
}
