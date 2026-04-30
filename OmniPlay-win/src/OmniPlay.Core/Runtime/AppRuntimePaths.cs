namespace OmniPlay.Core.Runtime;

public static class AppRuntimePaths
{
    private const string AppRootEnvironmentVariable = "OMNIPLAY_APP_ROOT";
    private static readonly object SyncRoot = new();
    private static string? resolvedAppRoot;

    public static string ResolveAppRoot()
    {
        lock (SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(resolvedAppRoot))
            {
                return resolvedAppRoot;
            }

            var overrideRoot = Environment.GetEnvironmentVariable(AppRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                resolvedAppRoot = NormalizeRoot(overrideRoot);
                EnsureWritable(resolvedAppRoot, isOverride: true);
                return resolvedAppRoot;
            }

            foreach (var candidateRoot in GetDefaultCandidates())
            {
                if (TryEnsureWritable(candidateRoot, out _))
                {
                    resolvedAppRoot = candidateRoot;
                    return resolvedAppRoot;
                }
            }

            throw new InvalidOperationException("Unable to resolve a writable application root for OmniPlay.");
        }
    }

    private static string[] GetDefaultCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return [Path.Combine(Path.GetTempPath(), "OmniPlay")];
        }

        return
        [
            Path.Combine(localAppData, "OmniPlay"),
            Path.Combine(Path.GetTempPath(), "OmniPlay")
        ];
    }

    private static string NormalizeRoot(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return Path.GetFullPath(expanded);
    }

    private static void EnsureWritable(string path, bool isOverride)
    {
        if (TryEnsureWritable(path, out var exception))
        {
            return;
        }

        var message = isOverride
            ? $"Configured app root is not writable: {path}"
            : $"Default app root is not writable: {path}";
        throw new IOException(message, exception);
    }

    private static bool TryEnsureWritable(string path, out Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(path);

            // Probe the directory once so later logging/database setup does not fail after startup.
            var probePath = Path.Combine(path, ".omniplay-write-test");
            using (var stream = new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.WriteByte(0);
            }

            File.Delete(probePath);

            exception = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            exception = ex;
            return false;
        }
    }
}
