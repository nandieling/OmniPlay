using System.Runtime.InteropServices;
using System.Text;

namespace OmniPlay.Infrastructure.Data;

internal static class MediaSourceAuthConfigProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const int CryptProtectUiForbidden = 0x1;

    public static string? ProtectForStorage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(value);
            var encryptedBytes = ProtectWithDpapi(plainBytes);
            return $"{DpapiPrefix}{Convert.ToBase64String(encryptedBytes)}";
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return value;
        }
    }

    public static string? UnprotectFromStorage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var encryptedBytes = Convert.FromBase64String(value[DpapiPrefix.Length..]);
            var plainBytes = UnprotectWithDpapi(encryptedBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is FormatException or PlatformNotSupportedException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static byte[] ProtectWithDpapi(byte[] plainBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return ExecuteProtect(plainBytes);
    }

    private static byte[] UnprotectWithDpapi(byte[] encryptedBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return ExecuteUnprotect(encryptedBytes);
    }

    private static byte[] ExecuteProtect(byte[] inputBytes)
    {
        var inputHandle = Marshal.AllocHGlobal(inputBytes.Length);
        Marshal.Copy(inputBytes, 0, inputHandle, inputBytes.Length);

        var input = new DataBlob
        {
            cbData = inputBytes.Length,
            pbData = inputHandle
        };

        DataBlob output = default;

        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output))
            {
                throw new InvalidOperationException($"DPAPI protect failed. Win32 Error={Marshal.GetLastWin32Error()}");
            }

            return CopyOutput(output);
        }
        finally
        {
            ReleaseBlobs(inputHandle, output);
        }
    }

    private static byte[] ExecuteUnprotect(byte[] inputBytes)
    {
        var inputHandle = Marshal.AllocHGlobal(inputBytes.Length);
        Marshal.Copy(inputBytes, 0, inputHandle, inputBytes.Length);

        var input = new DataBlob
        {
            cbData = inputBytes.Length,
            pbData = inputHandle
        };

        DataBlob output = default;

        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output))
            {
                throw new InvalidOperationException($"DPAPI unprotect failed. Win32 Error={Marshal.GetLastWin32Error()}");
            }

            return CopyOutput(output);
        }
        finally
        {
            ReleaseBlobs(inputHandle, output);
        }
    }

    private static byte[] CopyOutput(DataBlob output)
    {
        var result = new byte[output.cbData];
        if (output.cbData > 0)
        {
            Marshal.Copy(output.pbData, result, 0, output.cbData);
        }

        return result;
    }

    private static void ReleaseBlobs(IntPtr inputHandle, DataBlob output)
    {
        Marshal.FreeHGlobal(inputHandle);

        if (output.pbData != IntPtr.Zero)
        {
            _ = LocalFree(output.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
