using System.Diagnostics;
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
    private static extern int GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint SnapshotModule = 0x0000_0008;

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Module32First(IntPtr snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Module32Next(IntPtr snapshot, ref ModuleEntry32 entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public int Size;
        public int ModuleID;
        public int ProcessID;
        public int GlobalUsage;
        public int ProcessUsage;
        public IntPtr ModuleBase;
        public int ModuleSize;
        public IntPtr Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ModuleName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExePath;
    }

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file,
        uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    /// 卡住时把现场存下来：MiniDumpWriteDump 会挂起所有线程再写，界面线程正停在哪个
    /// 模块哪个偏移一目了然（结合 omniplay-core.map / 系统模块名就能判）。
    /// 返回 null = 连 dmp 都没写出来，这本身就是线索：大概卡在加载器锁或内核调用里。
    public static string? WriteHangDump(string phase)
    {
        try
        {
            // 文件名带上阶段：光看目录里有什么，就知道上一趟卡在哪儿
            var tag = new string(phase.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray())
                .Replace("：", " ").Trim();
            var name = $"hang-{DateTime.Now:HHmmss}-{(tag.Length > 28 ? tag[..28] : tag)}.dmp";
            var path = Path.Combine(Path.GetDirectoryName(StartupLog.FilePath)!, name);
            using var file = new FileStream(path, FileMode.Create);
            const uint withThreadInfo = 0x0000_0200;
            const uint ignoreInaccessible = 0x0000_4000;
            const uint withModuleHeaders = 0x0001_0000;
            var ok = MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId,
                file.SafeFileHandle.DangerousGetHandle(),
                withThreadInfo | ignoreInaccessible | withModuleHeaders,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return ok ? path : null;
        }
        catch (Exception) { return null; }
    }

    /// 别再让系统在出事的正上方摆一个「已停止工作」对话框：它会把进程吊在那儿
    /// 等用户点，冒烟因此「90 秒不退出」，用户那边则是点了才没影。原因我们自己
    /// 已经写进日志了，弹窗与 WER 采集都关掉。
    ///
    /// 用写注册表而不是 WerAddExcludedApplication：后者顺手把「崩了别重启」也写进去，
    /// 内部同样要走注册表，多一次不确定。
    public static void SilenceErrorDialogs()
    {
        const uint semNoGpFaultErrorBox = 0x0002;    // 「已停止工作」对话框
        SetErrorMode(semNoGpFaultErrorBox);
        // 提前把 dbghelp 拉起来：卡住之后再写 dmp 还要现 LoadLibrary，万一卡点正抱着
        // 加载器锁，那连现场都存不下来
        LoadLibraryExW("dbghelp.dll", IntPtr.Zero, 0x0000_0001 | 0x0000_0800);
        try
        {
            var exe = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "OmniPlaySpike");
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\Windows Error Reporting\AppExclusion");
            key?.SetValue(exe, 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception) { /* 这层只是让失败好看点，可有可无 */ }
    }

    /// 装载失败或依赖有缺口时抛这个，界面能直接把原因显示出来。
    public sealed class StartupFailureException : Exception
    {
        public StartupFailureException(string message) : base(message) { }
    }

    // MARK: - 本机崩溃兜底

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointers
    {
        public IntPtr ExceptionRecord;
        public IntPtr ContextRecord;
    }

    private delegate int ExceptionFilter(ref ExceptionPointers pointers);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(ExceptionFilter filter);

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, ExceptionFilter handler);

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(IntPtr address, out MemoryBasicInformation info, nuint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder buffer, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetModuleHandleEx(uint flags, string name, out IntPtr module);

    // 委托必须被静态字段持有，否则 GC 一走，回调指针就成了野指针
    private static readonly ExceptionFilter CrashFilter = OnFirstChance;
    private static readonly ExceptionFilter DeathFilter = OnUnhandled;
    private const int ContinueSearch = -1;
    private const int ContinueSearchFromFilter = 0;

    /// 原生崩溃（访问违例、栈溢出这类）不会走 .NET 的异常管道，进程直接没了，
    /// 用户看到的就是「点开就闪退」。这里挂两道：
    ///
    /// * 未处理过滤器：进程确定要死了，怎么折腾都行，异常码、出错指令在哪个模块
    ///   的哪个偏移、被访问的地址，全写下来。
    /// * 向量处理器（第一手）：只对「一定是死」的异常码动手（__fastfail、栈溢出
    ///   这类会跳过未处理过滤器）。**访问违例不在其列**——CLR、WPF、显卡驱动都拿
    ///   它当正常控制流用，在第一手处理器里写文件等于在别人持锁的中间插一段磁盘 IO，
    ///   装上杀软/输入法钩子的机器上能把进程直接吊住（实测症状：日志停在
    ///   「主窗口已创建」，界面压根不出来，也没任何崩溃记录）。
    public static void InstallCrashReporter()
    {
        try
        {
            SetUnhandledExceptionFilter(DeathFilter);
            AddVectoredExceptionHandler(1, CrashFilter);
        }
        catch (Exception error) { StartupLog.Write("崩溃兜底没挂上：" + error.Message); }
    }

    // 「一定是死」：没人会接着处理这种异常，在第一手处理器里留字是安全的
    private static readonly int[] AlwaysFatal =
    {
        unchecked((int)0xC000001D),   // 非法指令：Swift 的 fatalError 走这条
        unchecked((int)0xC0000409),   // __fastfail：CRT 参数校验、栈护检测
        unchecked((int)0xC00000FD),   // 栈溢出
        unchecked((int)0xC000041D),   // 数组边界检查失败
    };

    // 未处理过滤器里额外报访问违例：Swift 的内联 incref 就是一条 lock xadd，
    // 指针是空的直接炸，托管层一行都看不见。
    private static readonly int[] Notable = AlwaysFatal.Concat([unchecked((int)0xC0000005)]).ToArray();

    private static int _noted;
    private static readonly HashSet<string> Noted = new();

    private static int OnFirstChance(ref ExceptionPointers pointers) =>
        Describe(ref pointers, AlwaysFatal, ContinueSearch);

    private static int OnUnhandled(ref ExceptionPointers pointers) =>
        Describe(ref pointers, Notable, ContinueSearchFromFilter);

    private static int Describe(ref ExceptionPointers pointers, int[] codes, int verdict)
    {
        try
        {
            var record = pointers.ExceptionRecord;
            var code = Marshal.ReadInt32(record);
            if (!codes.Contains(code)) return verdict;
            var address = Marshal.ReadIntPtr(record, 16);
            var line = $"原生崩溃：异常码 0x{code:X8}，指令 {WhereIs(address)}";
            if (code == unchecked((int)0xC0000005))
            {
                // EXCEPTION_RECORD 里 ExceptionInformation 从偏移 32 开始，[1] 是被访问的地址
                var touched = Marshal.ReadIntPtr(record, 40);
                line += $"，被访问地址 0x{touched.ToInt64():X}";
            }
            lock (Noted)
            {
                if (Noted.Count >= 6 || !Noted.Add(line)) return verdict;
            }
            Interlocked.Increment(ref _noted);
            StartupLog.Write(line);
        }
        catch (Exception)
        {
            // 崩在这里也没关系，至少前面的阶段标记还在
        }
        return verdict;
    }

    /// 这个地址落在哪个模块的哪个偏移。偏移是关键：把包里的 omniplay-core.map
    /// （链接器 /MAP 出的符号表）按偏移一查就是函数名，不用等崩溃现场复现。
    private static string WhereIs(IntPtr address)
    {
        if (address == IntPtr.Zero ||
            VirtualQuery(address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == nuint.Zero)
            return "读不出来";
        var buffer = new System.Text.StringBuilder(1024);
        if (GetModuleFileNameW(info.AllocationBase, buffer, (uint)buffer.Capacity) == 0) return "未知模块";
        return $"{Path.GetFileName(buffer.ToString())}+0x{address.ToInt64() - info.AllocationBase.ToInt64():X}";
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
        // 只装载核心一个：Swift 运行时由加载器按依赖自己拉，我们不插手。
        // 之前先按全路径把 13 个抢下来（防同名劫持），结果用户机器上 WPF 建窗直接卡死——
        // 多出来的那批 LoadLibrary 动了加载器锁的时序，构建机看不出来。改成事后核对来源。
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
        CheckRuntimeSources(appDir);
        AuditShippedNativeLibraries(appDir);
        LogLiveInstances();
    }

    /// 核心已经起来了，顺手把包里其他原生 DLL 也扫一遍：真出问题至少要留下记录。
    private static void AuditShippedNativeLibraries(string appDir)
    {
        var runtime = SwiftRuntimeDllNames;
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
        LogForeignModules();
    }

    private static readonly string[] SwiftRuntimeDllNames =
    {
        "swiftCore.dll", "swiftCRT.dll", "swiftWinSDK.dll", "swiftDispatch.dll", "dispatch.dll",
        "Foundation.dll", "FoundationEssentials.dll", "FoundationInternationalization.dll",
        "_FoundationICU.dll", "BlocksRuntime.dll", "swift_Concurrency.dll",
        "swift_StringProcessing.dll", "swift_RegexParser.dll",
    };

    /// 进程里实际装载的「既不在包里也不在 Windows 目录下」的模块。
    /// 这是「同一份包，构建机上跑得掉、你这台机器跑不掉」最常见的解释：有人在
    /// 进程里塞了自己的 DLL，或者同名模块被别处的版本抢先装载了。
    private static void LogForeignModules()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotModule, (uint)GetCurrentProcessId());
        if (snapshot == IntPtr.Zero || (long)snapshot == -1) return;
        try
        {
            var entry = new ModuleEntry32 { Size = Marshal.SizeOf<ModuleEntry32>() };
            if (!Module32First(snapshot, ref entry)) return;
            var appDir = AppContext.BaseDirectory;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var foreign = new List<string>();
            do
            {
                var path = entry.ExePath ?? "";
                if (path.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                foreign.Add($"{Path.GetFileName(path)}（{path}）");
            }
            while (foreign.Count < 12 && Module32Next(snapshot, ref entry));
            StartupLog.Write(foreign.Count == 0
                ? "外来模块：没有，进程里全是包里和系统目录的东西"
                : $"外来模块 {foreign.Count} 个：" + string.Join("；", foreign));
        }
        catch (Exception error) { StartupLog.Write("外来模块清单没列出来：" + error.Message); }
        finally { CloseHandle(snapshot); }
    }

    /// 这 13 个运行时 DLL 到底从哪儿装载的：同名劫持要看得见，但不用靠抢先装载去防。
    private static void CheckRuntimeSources(string appDir)
    {
        const uint unchangedRefCount = 0x0000_0002;
        var strays = new List<string>();
        var loaded = 0;
        foreach (var name in SwiftRuntimeDllNames)
        {
            if (!GetModuleHandleEx(unchangedRefCount, name, out var module) || module == IntPtr.Zero) continue;
            loaded++;
            var buffer = new System.Text.StringBuilder(1024);
            if (GetModuleFileNameW(module, buffer, (uint)buffer.Capacity) != 0 &&
                !buffer.ToString().StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
                strays.Add($"{name}（来自 {System.IO.Path.GetDirectoryName(buffer.ToString())}）");
        }
        StartupLog.Write(strays.Count == 0
            ? $"运行时来源核对：{loaded} 个在进程里，都在包目录"
            : "运行时来源不对头：" + string.Join("；", strays));
    }

    /// 界面上一个窗都没有，用户就会再点一次图标，于是又多一个卡住的进程：日志被几趟写在
    /// 一起，桌面广播消息还得等这些不泵消息的窗口超时（WPF 的 SystemEvents 就靠广播消息，
    /// 最容易被这种僵尸拖住）。所以开跑前先收掉——只收「同一个 exe、活了 20 秒以上、
    /// 始终没建出窗口」的，正常看得见窗口的实例不受影响。
    private static void LogLiveInstances()
    {
        try
        {
            var own = Environment.ProcessId;
            var path = Environment.ProcessPath ?? "";
            var reaped = new List<string>();
            foreach (var other in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                using (other)
                {
                    if (other.Id == own) continue;
                    try
                    {
                        if (!string.Equals(other.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (Exception) { continue; }       // 路径读不准就当不是自己人
                    if (other.MainWindowHandle != IntPtr.Zero) continue;
                    if (DateTime.Now - other.StartTime < TimeSpan.FromSeconds(20)) continue;
                    try
                    {
                        other.Kill();
                        other.WaitForExit(2000);
                        reaped.Add(other.Id.ToString());
                    }
                    catch (Exception) { }
                }
            }
            if (reaped.Count > 0)
                StartupLog.Write($"收掉了 {reaped.Count} 个没窗口的旧实例（PID {string.Join("、", reaped)}）");
        }
        catch (Exception) { /* 这一行少了不影响判断 */ }
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
