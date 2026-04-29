using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OmniPlay.Infrastructure.Library;

internal sealed class SmbNetworkConnection : IDisposable
{
    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 0x00000004;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorSuccess = 0;

    private SmbNetworkConnection()
    {
    }

    public static SmbNetworkConnection Connect(string uncPath, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password))
        {
            return new SmbNetworkConnection();
        }

        var resource = new NetResource
        {
            ResourceType = ResourceTypeDisk,
            RemoteName = uncPath
        };

        var result = WNetAddConnection2(
            resource,
            string.IsNullOrEmpty(password) ? null : password,
            string.IsNullOrWhiteSpace(username) ? null : username,
            ConnectTemporary);

        if (result is not ErrorSuccess and not ErrorAlreadyAssigned)
        {
            throw new InvalidOperationException(new Win32Exception(result).Message);
        }

        return new SmbNetworkConnection();
    }

    public void Dispose()
    {
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        NetResource netResource,
        string? password,
        string? username,
        int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NetResource
    {
        public int Scope;

        public int ResourceType;

        public int DisplayType;

        public int Usage;

        public string? LocalName;

        public string? RemoteName;

        public string? Comment;

        public string? Provider;
    }
}
