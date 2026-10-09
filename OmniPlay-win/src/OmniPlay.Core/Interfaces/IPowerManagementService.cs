namespace OmniPlay.Core.Interfaces;

/// <summary>播放期间阻止系统息屏/睡眠(对齐 mac 版的 caffeinate 行为)。</summary>
public interface IPowerManagementService
{
    void BeginPlayback();

    void EndPlayback();
}

/// <summary>不支持的平台使用的空实现。</summary>
public sealed class NullPowerManagementService : IPowerManagementService
{
    public static readonly NullPowerManagementService Instance = new();

    private NullPowerManagementService()
    {
    }

    public void BeginPlayback()
    {
    }

    public void EndPlayback()
    {
    }
}
