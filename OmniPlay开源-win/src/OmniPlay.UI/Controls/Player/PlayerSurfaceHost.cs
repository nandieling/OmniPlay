using Avalonia.Controls;
using Avalonia.Platform;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OmniPlay.UI.Controls.Player;

public sealed class PlayerSurfaceHost : NativeControlHost
{
    private IPlatformHandle? platformHandle;
    private TaskCompletionSource<IntPtr>? handleSource;
    private IntPtr previousWindowProcedure;
    private WindowProcedure? windowProcedure;

    public event EventHandler? NativePointerActivity;

    public event EventHandler<NativePointerActivityEventArgs>? NativePointerMoved;

    public event EventHandler? NativePrimaryButtonPressed;

    public event EventHandler<NativeKeyActivityEventArgs>? NativeKeyDown;

    public Task<IntPtr> GetHandleAsync(CancellationToken cancellationToken = default)
    {
        if (platformHandle is not null)
        {
            return Task.FromResult(platformHandle.Handle);
        }

        if (handleSource is null || handleSource.Task.IsCompleted)
        {
            handleSource = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return cancellationToken.CanBeCanceled
            ? handleSource.Task.WaitAsync(cancellationToken)
            : handleSource.Task;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateWindowEx(
                0,
                "static",
                string.Empty,
                WindowStyles.WS_CHILD | WindowStyles.WS_VISIBLE | WindowStyles.WS_CLIPCHILDREN | WindowStyles.WS_CLIPSIBLINGS,
                0,
                0,
                1,
                1,
                parent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 libmpv 播放宿主窗口。");
            }

            platformHandle = new PlatformHandle(handle, "HWND");
            SubclassNativeWindow(handle);
        }
        else
        {
            platformHandle = base.CreateNativeControlCore(parent);
        }

        handleSource?.TrySetResult(platformHandle.Handle);
        return platformHandle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        RestoreNativeWindowProcedure(control.Handle);
        platformHandle = null;
        handleSource = null;

        if (OperatingSystem.IsWindows() && control.Handle != IntPtr.Zero)
        {
            DestroyWindow(control.Handle);
            return;
        }

        base.DestroyNativeControlCore(control);
    }

    private static class WindowStyles
    {
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
    }

    private void SubclassNativeWindow(IntPtr handle)
    {
        windowProcedure = NativeWindowProcedure;
        previousWindowProcedure = SetWindowLongPtr(handle, WindowProcedureIndex, windowProcedure);
    }

    private void RestoreNativeWindowProcedure(IntPtr handle)
    {
        if (OperatingSystem.IsWindows() &&
            handle != IntPtr.Zero &&
            previousWindowProcedure != IntPtr.Zero)
        {
            SetWindowLongPtr(handle, WindowProcedureIndex, previousWindowProcedure);
        }

        previousWindowProcedure = IntPtr.Zero;
        windowProcedure = null;
    }

    private IntPtr NativeWindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WindowMessages.WM_MOUSEMOVE:
                NativePointerMoved?.Invoke(this, DecodePointerActivity(lParam));
                NativePointerActivity?.Invoke(this, EventArgs.Empty);
                break;
            case WindowMessages.WM_LBUTTONDOWN:
                NativePrimaryButtonPressed?.Invoke(this, EventArgs.Empty);
                break;
            case WindowMessages.WM_KEYDOWN:
            case WindowMessages.WM_SYSKEYDOWN:
                NativeKeyDown?.Invoke(this, new NativeKeyActivityEventArgs(wParam.ToInt32()));
                break;
        }

        return CallWindowProc(previousWindowProcedure, hwnd, message, wParam, lParam);
    }

    private const int WindowProcedureIndex = -4;

    private delegate IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static NativePointerActivityEventArgs DecodePointerActivity(IntPtr lParam)
    {
        var value = unchecked((int)lParam.ToInt64());
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new NativePointerActivityEventArgs(x, y);
    }

    private static class WindowMessages
    {
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentHandle,
        IntPtr menuHandle,
        IntPtr instanceHandle,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, WindowProcedure procedure);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProcedure,
        IntPtr handle,
        int message,
        IntPtr wParam,
        IntPtr lParam);
}

public sealed class NativePointerActivityEventArgs : EventArgs
{
    public NativePointerActivityEventArgs(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }

    public double Y { get; }
}

public sealed class NativeKeyActivityEventArgs : EventArgs
{
    public NativeKeyActivityEventArgs(int virtualKey)
    {
        VirtualKey = virtualKey;
    }

    public int VirtualKey { get; }
}
