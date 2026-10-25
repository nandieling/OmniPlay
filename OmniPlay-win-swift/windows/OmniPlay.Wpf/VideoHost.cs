using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OmniPlay.WpfSpike;

/// 播放画面用的子 HWND。mpv 的 gpu 输出要一个真实 HWND；而 WPF 的 airspace 规则规定
/// 子 HWND 永远画在 WPF 内容之上，所以控制条不能贴在它下面，得走 Popup（见 SpikeWindow）。
public sealed class VideoHost : HwndHost
{
    private const uint WsChild = 0x40000000;      // WS_CHILD
    private const uint WsVisible = 0x10000000;    // WS_VISIBLE
    private const uint CsOwnDc = 0x00000020;      // CS_OWNDC：mpv 想独占设备上下文
    private const uint WmEraseBkgnd = 0x0014;

    private delegate IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string windowName,
                                                 uint style, int x, int y, int width, int height,
                                                 IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    private const string ClassName = "OmniPlayVideoChild";

    // 类只需注册一次；委托放静态字段里防止被回收。
    private static readonly WindowProc ProcStub = OnMessage;
    private static readonly ushort ClassAtom = Register();

    private static ushort Register()
    {
        var wrapper = new WindowClass
        {
            cbSize = Marshal.SizeOf<WindowClass>(),
            style = CsOwnDc,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ProcStub),
            hInstance = GetModuleHandleW(null),
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = ClassName,
        };
        var atom = RegisterClassExW(ref wrapper);
        if (atom == 0) throw new InvalidOperationException("注册播放子窗口类失败：" + Marshal.GetLastWin32Error());
        return atom;
    }

    private static IntPtr OnMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // 背景由 mpv 覆盖，自己擦会闪白
        if (message == WmEraseBkgnd) return new IntPtr(1);
        return DefWindowProcW(hWnd, message, wParam, lParam);
    }

    /// 交给 mpv 的 --wid。BuildWindowCore 之后才有效。
    public IntPtr RenderHandle { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var width = (int)Math.Max(1d, Math.Floor(ActualWidth));
        var height = (int)Math.Max(1d, Math.Floor(ActualHeight));
        RenderHandle = CreateWindowExW(0, ClassName, string.Empty, WsChild | WsVisible,
                                       0, 0, width, height, hwndParent.Handle,
                                       IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (RenderHandle == IntPtr.Zero)
            throw new InvalidOperationException("创建播放子窗口失败：" + Marshal.GetLastWin32Error());
        return new HandleRef(this, RenderHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        RenderHandle = IntPtr.Zero;
    }
}
