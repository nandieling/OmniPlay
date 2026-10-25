using System.IO;
using System.Text;

namespace OmniPlay.WpfSpike;

/// 启动日志。包是装在别人的机器上跑的，出事时必须留下一行能被读到的字，
/// 而不是只留一个「已停止工作」弹窗。位置：%LOCALAPPDATA%\\OmniPlay\\startup.log
public static class StartupLog
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmniPlay", "startup.log");

    /// 每次启动重开一份，日志只描述这一次
    public static void Reset()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                $"=== 觅影 Windows 演示 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { /* 写不下也不能把程序带崩 */ }
    }

    public static void Write(string line)
    {
        try
        {
            File.AppendAllText(FilePath,
                $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { /* 同上 */ }
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
