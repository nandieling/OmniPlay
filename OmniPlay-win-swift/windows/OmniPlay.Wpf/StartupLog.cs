using System.IO;
using System.Text;

namespace OmniPlay.WpfSpike;

/// 启动日志。包是装在别人的机器上跑的，出事时必须留下一行能被读到的字，
/// 而不是只留一个「已停止工作」弹窗。
///
/// 同一个进程同时往两处写：`%LOCALAPPDATA%\OmniPlay\startup.log` 是正常位置，
/// `%TEMP%\OmniPlay-startup.log` 是兜底——AppData 被同步盘接管、目录只读、
/// 杀软拦写入这类机器上，第一处可能根本没落盘，那就只剩 TEMP 那份能看。
public static class StartupLog
{
    private static readonly List<string> writable = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmniPlay", "startup.log");

    private static string FallbackPath { get; } = Path.Combine(Path.GetTempPath(), "OmniPlay-startup.log");

    /// 给用户看的一句话：日志落在哪儿了。
    public static string WhereToLook => writable.Count switch
    {
        0 => "日志写不下去（两处都写不了）",
        1 => writable[0],
        _ => string.Join(" 或 ", writable),
    };

    public static void Reset()
    {
        writable.Clear();
        foreach (var path in new[] { FilePath, FallbackPath })
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,
                    $"=== 觅影 Windows 演示 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}",
                    new UTF8Encoding(false));
                writable.Add(path);
            }
            catch (Exception) { /* 这一处写不下，换下一处 */ }
        }
    }

    public static void Write(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}";
        foreach (var path in writable)
        {
            try { File.AppendAllText(path, text, new UTF8Encoding(false)); }
            catch (Exception) { /* 日志不能把程序带崩 */ }
        }
    }

    public static void Fail(string stage, Exception? error)
    {
        Write($"{stage}失败：{error?.GetType().Name}：{error?.Message}");
        if (error?.StackTrace is { } stack)
            foreach (var row in stack.Split('\n').Take(6)) Write("    " + row.TrimEnd('\r'));
        for (var inner = error?.InnerException; inner is not null; inner = inner.InnerException)
            Write($"    内层：{inner.GetType().Name}：{inner.Message}");
    }
}
