using System.IO;
using System.Runtime.InteropServices;

namespace OmniPlay.WpfSpike;

/// 装载核心之前先在本机验一遍依赖。核心是 Swift 编的 DLL，它自己还要拉
/// swiftCore / Foundation 那一串；构建机上有工具链的 PATH 兜底，装到用户机器上
/// 就只剩包里这几个文件，所以第一步就按全路径装载一次：成功说明依赖齐，
/// 失败要把缺哪个 DLL 说清楚。
public static class NativePreflight
{
    private const uint LoadLibrarySearchDllLoadDir = 0x0000_0100;
    private const uint LoadLibrarySearchDefaultDirs = 0x0000_1100;
    private const uint LoadLibrarySearchApplicationDir = 0x0000_0200;
    private const int ErrorModNotFound = 126;          // 依赖的模块找不到
    private const int ErrorProcNotFound = 127;         // 127：缺导出函数

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr fileHandle, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// 装载失败或依赖有缺口时抛这个，界面能直接把原因显示出来。
    public sealed class StartupFailureException : Exception
    {
        public StartupFailureException(string message) : base(message) { }
    }

    /// 把包内 redist\ 加进 DLL 搜索路径，并让后续所有装载都只看
    /// 「应用目录 -> redist -> 系统目录」，不再蹭构建机留下的 PATH。
    public static void PinSearchPaths()
    {
        // 没有这个调用，AddDllDirectory 加进来的目录对不带旗标的 LoadLibrary 无效
        SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs | LoadLibrarySearchApplicationDir);
        if (Directory.Exists(RedistDir))
            StartupLog.Write($"包内备着 redist：{Directory.GetFiles(RedistDir, "*.dll").Length} 个，本机缺运行库时才用");
    }

    private static string RedistDir => Path.Combine(AppContext.BaseDirectory, "redist");

    /// 装载 omniplay-core.dll。成功后 .NET 的 P/Invoke 会直接复用这个已载入模块。
    public static void EnsureCoreLoaded()
    {
        var appDir = AppContext.BaseDirectory;
        var core = Path.Combine(appDir, "omniplay-core.dll");
        StartupLog.Write($"应用目录：{appDir}");
        if (!File.Exists(core)) throw new StartupFailureException($"安装目录里没有 omniplay-core.dll：{core}");

        var flags = LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs | LoadLibrarySearchApplicationDir;
        var handle = LoadLibraryExW(core, IntPtr.Zero, flags);
        var error = (uint)Marshal.GetLastWin32Error();

        // 报「模块找不到」而本机没装 VC++ 运行库是最常见的一种，这时候才轮到包内 redist
        if (handle == IntPtr.Zero && (int)error == ErrorModNotFound && Directory.Exists(RedistDir) &&
            AddDllDirectory(RedistDir) != IntPtr.Zero)
        {
            StartupLog.Write("首次装载失败（缺模块），补上包内 redist 再试");
            handle = LoadLibraryExW(core, IntPtr.Zero, flags);
            error = (uint)Marshal.GetLastWin32Error();
            if (handle != IntPtr.Zero) StartupLog.Write("用的是包内 redist 里的运行库");
        }

        if (handle == IntPtr.Zero)
            throw new StartupFailureException($"核心装载失败：{DescribeFailure(core, error)}");

        StartupLog.Write("核心 omniplay-core.dll 装载成功");
        AuditShippedNativeLibraries(appDir);
    }

    /// 核心已经起来了，顺手把包里其他原生 DLL 也扫一遍：真出问题至少要留下记录。
    private static void AuditShippedNativeLibraries(string appDir)
    {
        var runtime = new[]
        {
            "swiftCore.dll", "swiftCRT.dll", "swiftWinSDK.dll", "swiftDispatch.dll", "dispatch.dll",
            "Foundation.dll", "FoundationEssentials.dll", "FoundationInternationalization.dll",
            "_FoundationICU.dll", "BlocksRuntime.dll", "swift_Concurrency.dll",
            "swift_StringProcessing.dll", "swift_RegexParser.dll",
        };
        var gaps = new List<string>();
        foreach (var name in runtime)
        {
            var path = Path.Combine(appDir, name);
            if (!File.Exists(path)) { gaps.Add(name + "（没打进包）"); continue; }
            var missing = UnsatisfiedImports(path, appDir, RedistDir);
            if (missing.Count > 0) gaps.Add(name + " 缺 " + string.Join("、", missing));
        }
        StartupLog.Write(gaps.Count == 0
            ? $"本机自检：Swift 运行时 {runtime.Length} 个 DLL 依赖齐"
            : "本机自检有缺口：" + string.Join("；", gaps));
    }

    /// 这个 DLL 的导入里，哪些既不在包里也不在系统目录里。
    public static List<string> UnsatisfiedImports(string dllPath, params string[] extraDirs)
    {
        var systemDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System));
        var result = new List<string>();
        foreach (var imported in PeImports(dllPath))
        {
            if (imported.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) ||
                imported.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase)) continue;   // 系统契约 DLL
            var hit = File.Exists(Path.Combine(systemDir, imported)) ||
                      extraDirs.Any(dir => File.Exists(Path.Combine(dir, imported)));
            if (!hit) result.Add(imported);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string DescribeFailure(string dll, uint win32Error)
    {
        switch ((int)win32Error)
        {
            case ErrorProcNotFound:
                return "DLL 里少了一个导出函数，核心与宿主的版本对不上";
            case ErrorModNotFound:
            {
                var missing = UnsatisfiedImports(dll, AppContext.BaseDirectory, RedistDir);
                return missing.Count > 0
                    ? "缺依赖 " + string.Join("、", missing) + "（包里的文件可能不完整，或被杀软拦了）"
                    : "依赖里有这台机器上没有的模块";
            }
            default:
                return $"Win32 错误 {win32Error}";
        }
    }

    /// 收尾用。Swift 运行时是按依赖动态装载的，进程正常退出时几个 DLL 的 DllMain
    /// 收尾顺序一错就是 fastfail（0xC0000409），用户那边又是一个「已停止工作」弹窗。
    /// 数据库和日志都已经落盘，这里直接结束进程。
    public static void HardExit(int code)
    {
        TerminateProcess(GetCurrentProcess(), (uint)code);
    }

    // 只读导入表，够回答「这个 DLL 还依赖谁」，不做完整解析
    private static List<string> PeImports(string path)
    {
        var bytes = new byte[0];
        try { bytes = File.ReadAllBytes(path); } catch { return new List<string>(); }
        if (bytes.Length < 0x40 || BitConverter.ToUInt16(bytes, 0) != 0x5A4D) return new List<string>();
        var pe = BitConverter.ToInt32(bytes, 0x3C);
        if (pe <= 0 || pe + 24 > bytes.Length) return new List<string>();
        var sections = BitConverter.ToUInt16(bytes, pe + 6);
        var sizeOfOptional = BitConverter.ToUInt16(bytes, pe + 20);
        var optional = pe + 24;
        var magic = BitConverter.ToUInt16(bytes, optional);
        // 数据目录起点：PE32+ 在可选头 +112，PE32 在 +96；导入表是第 2 项，每项 8 字节
        var directory = optional + (magic == 0x20B ? 112 : 96) + 8;
        var importRva = BitConverter.ToUInt32(bytes, directory);
        if (importRva == 0) return new List<string>();
        var sectionTable = optional + sizeOfOptional;

        int OffsetOf(uint rva)
        {
            for (var i = 0; i < sections; i++)
            {
                var row = sectionTable + i * 40;
                var virtualAddress = BitConverter.ToUInt32(bytes, row + 12);
                var virtualSize = BitConverter.ToUInt32(bytes, row + 8);
                var rawSize = BitConverter.ToUInt32(bytes, row + 16);
                var raw = BitConverter.ToUInt32(bytes, row + 20);
                if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, rawSize))
                    return (int)(raw + (rva - virtualAddress));
            }
            return -1;
        }

        var names = new List<string>();
        for (var descriptor = OffsetOf(importRva);
             descriptor >= 0 && descriptor + 20 <= bytes.Length; descriptor += 20)
        {
            var nameRva = BitConverter.ToUInt32(bytes, descriptor + 12);
            if (nameRva == 0) break;
            var at = OffsetOf(nameRva);
            if (at < 0 || at >= bytes.Length) break;
            var end = at;
            while (end < bytes.Length && bytes[end] != 0) end++;
            names.Add(System.Text.Encoding.ASCII.GetString(bytes, at, end - at));
        }
        return names;
    }
}
