using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;

namespace OmniPlay.Player.Mpv.Interop;

internal static class MpvNative
{
    private static readonly object SyncRoot = new();
    private static readonly string[] CandidateLibraries =
    [
        "libmpv-2.dll",
        "mpv-2.dll",
        "libmpv.dll",
        "mpv.dll"
    ];

    private static IntPtr loadedLibraryHandle;
    private static string? loadedLibraryName;

    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, ResolveDllImport);
    }

    private static IntPtr ResolveDllImport(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        return CandidateLibraries.Any(candidate =>
            string.Equals(libraryName, Path.GetFileNameWithoutExtension(candidate), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(libraryName, candidate, StringComparison.OrdinalIgnoreCase))
            && TryLoadLibrary(out _)
                ? loadedLibraryHandle
                : IntPtr.Zero;
    }

    public static bool TryLoadLibrary(out string? libraryName)
    {
        lock (SyncRoot)
        {
            if (loadedLibraryHandle != IntPtr.Zero)
            {
                libraryName = loadedLibraryName;
                return true;
            }

            foreach (var candidate in CandidateLibraries)
            {
                var candidatePaths = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, candidate),
                    Path.Combine(AppContext.BaseDirectory, "Native", "mpv", candidate)
                };

                foreach (var fullPath in candidatePaths)
                {
                    if (File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out loadedLibraryHandle))
                    {
                        loadedLibraryName = fullPath;
                        libraryName = loadedLibraryName;
                        return true;
                    }
                }

                if (NativeLibrary.TryLoad(candidate, out loadedLibraryHandle))
                {
                    loadedLibraryName = candidate;
                    libraryName = loadedLibraryName;
                    return true;
                }
            }

            libraryName = null;
            return false;
        }
    }

    public static IntPtr Create() => mpv_create();

    public static int Initialize(IntPtr handle) => mpv_initialize(handle);

    public static void TerminateDestroy(IntPtr handle) => mpv_terminate_destroy(handle);

    public static int SetOptionString(IntPtr handle, string name, string value) => mpv_set_option_string(handle, name, value);

    public static int SetPropertyString(IntPtr handle, string name, string value) => mpv_set_property_string(handle, name, value);

    public static string? GetPropertyString(IntPtr handle, string name)
    {
        var ptr = mpv_get_property_string(handle, name);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            mpv_free(ptr);
        }
    }

    public static int Command(IntPtr handle, params string[] args)
    {
        var stringPointers = new IntPtr[args.Length + 1];
        IntPtr argsPointer = IntPtr.Zero;

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                stringPointers[i] = MarshalUtf8(args[i]);
            }

            argsPointer = Marshal.AllocHGlobal(IntPtr.Size * stringPointers.Length);
            Marshal.Copy(stringPointers, 0, argsPointer, stringPointers.Length);
            return mpv_command(handle, argsPointer);
        }
        finally
        {
            if (argsPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(argsPointer);
            }

            foreach (var ptr in stringPointers)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
    }

    private static IntPtr MarshalUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_create();

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_initialize(IntPtr ctx);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(IntPtr ctx, string name, string value);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property_string(IntPtr ctx, string name, string value);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free(IntPtr data);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(IntPtr ctx, IntPtr args);
}
