using System.Diagnostics;
using System.Text;
using OmniPlay.Core.Runtime;

namespace OmniPlay.Desktop.Diagnostics;

internal static class AppLog
{
    private static readonly object SyncRoot = new();
    private static string? logFilePath;
    private static bool initialized;

    public static string LogFilePath
    {
        get
        {
            EnsureInitialized();
            return logFilePath!;
        }
    }

    public static void Initialize()
    {
        EnsureInitialized();
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        var builder = new StringBuilder(message);
        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        Write("ERROR", builder.ToString());
    }

    private static void EnsureInitialized()
    {
        lock (SyncRoot)
        {
            if (initialized)
            {
                return;
            }

            var appRoot = AppRuntimePaths.ResolveAppRoot();
            var logsDirectory = Path.Combine(appRoot, "logs");
            Directory.CreateDirectory(logsDirectory);

            logFilePath = Path.Combine(logsDirectory, "app.log");

            Trace.AutoFlush = true;
            Trace.Listeners.Add(new FileTraceListener(logFilePath));

            initialized = true;
            Write("INFO", $"日志已初始化: {logFilePath}");
        }
    }

    private static void Write(string level, string message)
    {
        EnsureInitialized();

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        Trace.WriteLine(line);
    }

    private sealed class FileTraceListener(string path) : TraceListener
    {
        private readonly string path = path;
        private readonly object writeLock = new();

        public override void Write(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            lock (writeLock)
            {
                File.AppendAllText(path, message, Encoding.UTF8);
            }
        }

        public override void WriteLine(string? message)
        {
            lock (writeLock)
            {
                File.AppendAllText(path, (message ?? string.Empty) + Environment.NewLine, Encoding.UTF8);
            }
        }
    }
}
