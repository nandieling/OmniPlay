using System.Diagnostics;
using OmniPlay.Core.Interfaces;

namespace OmniPlay.Desktop.Services;

public sealed class ShellExternalLinkOpener : IExternalLinkOpener
{
    public bool TryOpen(string target, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(target))
        {
            errorMessage = "目标地址为空。";
            return false;
        }

        if (TryNormalizeLocalPath(target, out var normalizedLocalPath))
        {
            return StartProcess(normalizedLocalPath, out errorMessage);
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return StartProcess(uri.AbsoluteUri, out errorMessage);
        }

        errorMessage = "链接或文件地址无效。";
        return false;
    }

    private static bool TryNormalizeLocalPath(string target, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (File.Exists(target) || Directory.Exists(target))
        {
            normalizedPath = Path.GetFullPath(target);
            return true;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var localPath = uri.LocalPath;
            if (File.Exists(localPath) || Directory.Exists(localPath))
            {
                normalizedPath = localPath;
                return true;
            }
        }

        return false;
    }

    private static bool StartProcess(string target, out string? errorMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
