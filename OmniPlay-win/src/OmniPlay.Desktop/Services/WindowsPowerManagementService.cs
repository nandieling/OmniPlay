using System.Runtime.InteropServices;
using OmniPlay.Core.Interfaces;

namespace OmniPlay.Desktop.Services;

/// <summary>
/// 用 <c>SetThreadExecutionState</c> 在播放期间保持显示器与系统唤醒，
/// 对应 mac 版的 <c>Process(caffeinate)</c>。状态是进程级的，进程退出时系统自动复位。
/// </summary>
public sealed class WindowsPowerManagementService : IPowerManagementService, IDisposable
{
    private const uint ES_CONTINUOUS = 0x8000_0000;
    private const uint ES_SYSTEM_REQUIRED = 0x0000_0001;
    private const uint ES_DISPLAY_REQUIRED = 0x0000_0002;

    private static readonly object Gate = new();
    private static int activeCount;

    private bool isHolding;

    public void BeginPlayback()
    {
        if (isHolding)
        {
            return;
        }

        isHolding = true;
        lock (Gate)
        {
            activeCount++;
            if (activeCount == 1)
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            }
        }
    }

    public void EndPlayback()
    {
        if (!isHolding)
        {
            return;
        }

        isHolding = false;
        lock (Gate)
        {
            activeCount--;
            if (activeCount <= 0)
            {
                activeCount = 0;
                SetThreadExecutionState(ES_CONTINUOUS);
            }
        }
    }

    public void Dispose() => EndPlayback();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
