using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace OmniPlay.WpfSpike;

/// 核心调用器：只认 C ABI + UTF-8 JSON，界面不碰任何 Swift 类型。
/// 内存规则与 box-send 一致——响应缓冲由核心分配，用完必须还给它自己 free。
public sealed class CoreBridge : IDisposable
{
    private const string CoreLibrary = "omniplay-core";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeEventCallback(IntPtr context, IntPtr json, int length);

    [DllImport(CoreLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr omniplay_create();

    [DllImport(CoreLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr omniplay_invoke(IntPtr handle, byte[] request, int requestLength,
                                                 out int responseLength);

    [DllImport(CoreLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void omniplay_free_bytes(IntPtr handle, IntPtr bytes);

    [DllImport(CoreLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void omniplay_set_event_callback(IntPtr handle, IntPtr context,
                                                           NativeEventCallback? callback);

    [DllImport(CoreLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr omniplay_version();

    [DllImport(CoreLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void omniplay_destroy(IntPtr handle);

    private readonly IntPtr _handle;
    private readonly NativeEventCallback _callback;   // 委托必须被持有，否则会被 GC 收走
    private long _eventCount;

    public CoreBridge()
    {
        _handle = omniplay_create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("核心没能创建句柄");
        _callback = OnNativeEvent;
        omniplay_set_event_callback(_handle, IntPtr.Zero, _callback);
    }

    /// 核心版本，边界两侧对不上时界面要立刻喊出来。
    public string Version => Marshal.PtrToStringUTF8(omniplay_version()) ?? "未知";

    /// 已收到的推送条数，探针用它验证「事件能到宿主」。
    public long EventCount => Interlocked.Read(ref _eventCount);

    /// 推送到达。在核心线程上触发，界面自己 marshal 到 UI 线程。
    public event Action<string>? EventReceived;

    public JsonNode Invoke(string method, JsonNode? parameters = null)
    {
        var request = new JsonObject { ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        var payload = Encoding.UTF8.GetBytes(request.ToJsonString());
        var pointer = omniplay_invoke(_handle, payload, payload.Length, out var length);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException($"调用 {method} 拿到空响应");
        try
        {
            var json = Marshal.PtrToStringUTF8(pointer, length) ?? string.Empty;
            var envelope = JsonNode.Parse(json)
                           ?? throw new InvalidOperationException($"核心回的不是 JSON：{json}");
            if (envelope["ok"]?.GetValue<bool>() != true)
            {
                var message = envelope["error"]?.GetValue<string>() ?? "核心没给原因";
                throw new CoreCallException(method, message);
            }
            return envelope["result"] ?? JsonValue.Create(true)!;
        }
        finally
        {
            omniplay_free_bytes(_handle, pointer);
        }
    }

    /// 调用失败时把方法名和核心给的人话一起抛出去，界面直接显示即可。
    public sealed class CoreCallException : Exception
    {
        public CoreCallException(string method, string message) : base($"{method}：{message}") { }
    }

    private void OnNativeEvent(IntPtr context, IntPtr json, int length)
    {
        if (json == IntPtr.Zero || length <= 0) return;
        Interlocked.Increment(ref _eventCount);
        EventReceived?.Invoke(Marshal.PtrToStringUTF8(json, length) ?? string.Empty);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) omniplay_destroy(_handle);
    }
}
