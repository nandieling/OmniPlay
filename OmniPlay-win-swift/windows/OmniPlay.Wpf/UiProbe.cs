using System.Runtime.InteropServices;

namespace OmniPlay.WpfSpike;

/// 完全不碰 WPF 的窗口探针（--win32-probe）。界面起不来时先问这一个：这台机器让不让
/// 这个进程建窗口、显示、泵消息。它能显示，问题就锁死在 WPF 自己身上；它也不出来，
/// 那就跟 WPF、跟觅影的代码都无关，是桌面/内核这一层在拦。
internal static class UiProbe
{
    private const uint WmDestroy = 0x0002;
    private const uint WsOverlappedWindow = 0x00CF_0000;
    private const int SwShow = 5;
    private const uint PmRemove = 0x0001;
    private const uint MbOk = 0x0000;
    private const uint MbSetForeground = 0x0000_0010;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    // 委托得被静态字段拿着：窗口过程是内核回调，GC 一走就成了野指针
    private static WndProc? _keeper;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Wndclassex
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref Wndclassex windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint style, string className, string title,
        uint windowStyle, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg message, IntPtr hwnd, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    public static void Run(bool smoke)
    {
        _keeper = (hwnd, message, wParam, lParam) =>
        {
            if (message == WmDestroy) PostQuitMessage(0);
            return DefWindowProc(hwnd, message, wParam, lParam);
        };
        var instance = GetModuleHandle(null);
        var windowClass = new Wndclassex
        {
            Size = Marshal.SizeOf<Wndclassex>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_keeper),
            Instance = instance,
            Background = new IntPtr(6),     // COLOR_WINDOW
            ClassName = "OmniPlayProbeWindow",
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            StartupLog.Write($"探针（Win32）：窗口类注册失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            return;
        }
        StartupLog.Write("探针（Win32）：窗口类注册成功");

        var hwnd = CreateWindowExW(0, windowClass.ClassName, "觅影 · Win32 窗口探针",
            WsOverlappedWindow, -150_000, -150_000, 460, 180, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            StartupLog.Write($"探针（Win32）：CreateWindowEx 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            return;
        }
        StartupLog.Write($"探针（Win32）：窗口已建出来（HWND 0x{hwnd:X}）");
        ShowWindow(hwnd, SwShow);
        UpdateWindow(hwnd);
        StartupLog.Write("探针（Win32）：ShowWindow 与 UpdateWindow 都返回了，窗口应该在你屏幕上");

        // 泵一会儿消息：能画出来、能点能拖，才说明消息这一路也正常
        var until = DateTime.UtcNow.AddSeconds(smoke ? 1.5 : 8);
        while (DateTime.UtcNow < until)
        {
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            Thread.Sleep(20);
        }
        StartupLog.Write("探针（Win32）：消息泵走完，这一路都正常");
        if (!smoke)
            MessageBoxW(hwnd, "刚才那个标题为「觅影 · Win32 窗口探针」的窗口你看到了吗？\n\n" +
                              "看到 → 系统层面的建窗与显示没问题，卡点在 WPF 自己身上\n" +
                              "没看到 → 这一层就被拦住了，跟觅影的代码无关",
                        "觅影探针", MbOk | MbSetForeground);
    }
}
