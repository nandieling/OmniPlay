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

    /// 上一次那趟跑到哪儿就没了。用来判断该不该自动退到安全模式：
    /// 崩在首帧之前 = 大概率渲染/显卡驱动，崩在首帧之后 = 大概率片库那一层。
    public static bool PreviousRunDiedBeforeFirstFrame { get; private set; }
    public static bool PreviousRunDiedBeforeLibrary { get; private set; }

    /// 上一趟是不是死在「片库：开始读取」之后、「片库：读取完成」之前。
    /// 这个位置日志会断在半路，只能靠上一份日志缺哪一行反推。
    public static int LibraryDeaths { get; private set; }

    /// 死了几趟的账本。日志每趟重写，没有这个文件就没法知道「已经连着崩第三次了」。
    private static string StatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmniPlay", "crash-state.txt");

    public static void Reset()
    {
        writable.Clear();
        var deaths = 0;
        try { if (File.Exists(StatePath)) int.TryParse(File.ReadAllText(StatePath).Trim(), out deaths); }
        catch (Exception) { }
        try
        {
            if (File.Exists(FilePath))
            {
                var previous = File.ReadAllText(FilePath);
                try { if (File.Exists(PhasePath)) PreviousRunPhase = File.ReadAllText(PhasePath).Trim(); }
                catch (Exception) { }
                if (previous.Contains("退出（"))
                {
                    deaths = 0;       // 上一趟正常收工，账清零
                }
                else
                {
                    PreviousRunNeverRendered = true;
                    PreviousRunDiedBeforeLibrary =
                        previous.Contains("第一帧") && !previous.Contains("片库：");
                    if (previous.Contains("片库：开始读取") && !previous.Contains("片库：读取完成")) deaths += 1;
                    // 停在哪一格以 phase.txt 为准；没有它就退回看日志缺哪一行
                    PreviousRunDiedBeforeFirstFrame = PreviousRunPhase.Length > 0
                        ? !(PreviousRunPhase.Contains("第一帧") || PreviousRunPhase.Contains("已退出"))
                        : !previous.Contains("第一帧");
                }
            }
        }
        catch (Exception) { /* 上一份日志读不出来就按首次启动处理 */ }
        LibraryDeaths = deaths;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, deaths.ToString());
        }
        catch (Exception) { }
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

    /// 走到哪一步了。看门狗会把它打出来，日志因此自带断点，不用靠猜。
    /// 阶段另外单独落一份小文件：日志每趟重写、标记名又容易改，上一趟停在这儿这件事
    /// 得有个不依赖日志文案的记法（上一版就是改了标记名把自愈判定给弄失效了）。
    public static string Phase { get; private set; } = "刚启动";

    private static string PhasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmniPlay", "phase.txt");

    /// 上一趟停在哪个阶段。空字符串 = 不知道（首次运行或文件读不出来）。
    public static string PreviousRunPhase { get; private set; } = "";

    /// 上一趟是不是连一帧都没画出来。命中就别再硬撑显卡特效了。
    public static bool PreviousRunNeverRendered { get; private set; }

    public static void Mark(string phase)
    {
        Phase = phase;
        Write("阶段：" + phase);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PhasePath)!);
            File.WriteAllText(PhasePath, phase);
        }
        catch (Exception) { /* 记不上不影响这一趟 */ }
    }

    /// 收尾时喊一声：下一趟据此判断「上一趟是正常结束的」，别乱降级。
    public static void MarkFinished(string phase = "已退出") => Mark(phase);

    /// 首帧前卡住的哨兵：8 秒、40 秒各报一次停在哪儿，并把现场存成 dmp。
    /// 走普通线程而不是计时器——界面线程本身可能就是卡住的那个。
    public static void ArmWatchdog()
    {
        new Thread(() =>
        {
            foreach (var (at, wait) in new[] { (8, 8), (40, 32) })
            {
                Thread.Sleep(TimeSpan.FromSeconds(wait));
                if (_rendered) return;
                Write($"界面：{at} 秒没画出一帧——进程还活着，停在「{Phase}」");
                if (!_dumped)
                {
                    _dumped = true;
                    var path = NativePreflight.WriteHangDump(Phase);
                    Write(path is null
                        ? "现场没存下来（连写 dmp 都要等锁：多半卡在加载器锁或内核调用里）"
                        : $"现场已存：{path}");
                }
            }
        })
        { IsBackground = true }.Start();
    }

    private static volatile bool _rendered;
    private static volatile bool _dumped;

    public static void NoteRendered() => _rendered = true;

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
