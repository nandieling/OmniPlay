using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Application = System.Windows.Forms.Application;
using MessageBox = System.Windows.Forms.MessageBox;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;

namespace OmniPlay.Setup;

internal static class Program
{
    private const string ProductName = "OmniPlay";
    private const string AppExeName = "OmniPlay.Desktop.exe";
    private const string PayloadResourceName = "OmniPlay.Payload.zip";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OmniPlay";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var options = SetupOptions.Parse(args);

        try
        {
            if (options.VerifyOnly)
            {
                VerifyPayload();
                Notify("安装包校验通过。", options.Quiet, isError: false);
                return 0;
            }

            if (options.Uninstall)
            {
                Uninstall(options.Quiet);
                Notify("OmniPlay 已卸载。", options.Quiet, isError: false);
                return 0;
            }

            Install(options);
            Notify("OmniPlay 安装完成。", options.Quiet, isError: false);
            return 0;
        }
        catch (Exception ex)
        {
            Notify("OmniPlay 安装器运行失败：\n\n" + ex.Message, options.Quiet, isError: true);
            return 1;
        }
    }

    private static void Install(SetupOptions options)
    {
        var installRoot = GetInstallRoot();
        EnsureSafeInstallRoot(installRoot);

        StopRunningApp();
        Directory.CreateDirectory(installRoot);
        CleanInstallDirectory(installRoot, GetCurrentExecutablePath());
        ExtractPayload(installRoot);

        var appExePath = Path.Combine(installRoot, AppExeName);
        if (!File.Exists(appExePath))
        {
            throw new FileNotFoundException("安装包内缺少主程序。", appExePath);
        }

        var installedSetupPath = Path.Combine(installRoot, "setup.exe");
        CopyCurrentSetup(installedSetupPath);
        CreateShortcuts(appExePath, installedSetupPath, options.CreateDesktopShortcut);
        RegisterUninstaller(installRoot, appExePath, installedSetupPath);
    }

    private static void Uninstall(bool quiet)
    {
        var installRoot = GetInstallRoot();
        EnsureSafeInstallRoot(installRoot);

        StopRunningApp();
        DeleteShortcuts();
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        if (!Directory.Exists(installRoot))
        {
            return;
        }

        if (IsCurrentExecutableUnder(installRoot))
        {
            ScheduleDirectoryRemovalAfterExit(installRoot);
            Notify("OmniPlay 已卸载，剩余安装文件将在安装器退出后清理。", quiet, isError: false);
            return;
        }

        Directory.Delete(installRoot, recursive: true);
    }

    private static void VerifyPayload()
    {
        using var payload = OpenPayloadStream();
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        if (!archive.Entries.Any(entry => string.Equals(entry.FullName.Replace('/', '\\'), AppExeName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("安装包内没有找到 OmniPlay.Desktop.exe。");
        }
    }

    private static void ExtractPayload(string installRoot)
    {
        using var payload = OpenPayloadStream();
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        var normalizedInstallRoot = EnsureTrailingSeparator(Path.GetFullPath(installRoot));

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(installRoot, entry.FullName));
            if (!destinationPath.StartsWith(normalizedInstallRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安装包内包含非法路径。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static Stream OpenPayloadStream()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException("安装器缺少程序载荷，请使用 windows\\package-setup.ps1 重新生成 setup.exe。");
        }

        return stream;
    }

    private static void CleanInstallDirectory(string installRoot, string currentSetupPath)
    {
        var currentSetupFullPath = Path.GetFullPath(currentSetupPath);
        foreach (var item in new DirectoryInfo(installRoot).EnumerateFileSystemInfos())
        {
            if (string.Equals(Path.GetFullPath(item.FullName), currentSetupFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((item.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
            {
                Directory.Delete(item.FullName, recursive: true);
            }
            else
            {
                item.Attributes = FileAttributes.Normal;
                item.Delete();
            }
        }
    }

    private static void CopyCurrentSetup(string installedSetupPath)
    {
        var currentSetupPath = GetCurrentExecutablePath();
        if (string.Equals(Path.GetFullPath(currentSetupPath), Path.GetFullPath(installedSetupPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Copy(currentSetupPath, installedSetupPath, overwrite: true);
    }

    private static void CreateShortcuts(string appExePath, string setupExePath, bool createDesktopShortcut)
    {
        var startMenuDirectory = GetStartMenuDirectory();
        Directory.CreateDirectory(startMenuDirectory);

        CreateShortcut(
            Path.Combine(startMenuDirectory, "OmniPlay.lnk"),
            appExePath,
            arguments: string.Empty,
            workingDirectory: Path.GetDirectoryName(appExePath) ?? string.Empty,
            iconLocation: appExePath + ",0");

        CreateShortcut(
            Path.Combine(startMenuDirectory, "卸载 OmniPlay.lnk"),
            setupExePath,
            arguments: "/uninstall",
            workingDirectory: Path.GetDirectoryName(setupExePath) ?? string.Empty,
            iconLocation: setupExePath + ",0");

        if (createDesktopShortcut)
        {
            CreateShortcut(
                GetDesktopShortcutPath(),
                appExePath,
                arguments: string.Empty,
                workingDirectory: Path.GetDirectoryName(appExePath) ?? string.Empty,
                iconLocation: appExePath + ",0");
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory, string iconLocation)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });
            if (shortcut is null)
            {
                return;
            }

            SetComProperty(shortcut, "TargetPath", targetPath);
            SetComProperty(shortcut, "Arguments", arguments);
            SetComProperty(shortcut, "WorkingDirectory", workingDirectory);
            SetComProperty(shortcut, "IconLocation", iconLocation);
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, binder: null, target: shortcut, args: Array.Empty<object>());
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void SetComProperty(object instance, string name, object value)
    {
        instance.GetType().InvokeMember(name, BindingFlags.SetProperty, binder: null, target: instance, args: new[] { value });
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private static void RegisterUninstaller(string installRoot, string appExePath, string setupExePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        if (key is null)
        {
            return;
        }

        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", GetSetupVersion());
        key.SetValue("Publisher", ProductName);
        key.SetValue("DisplayIcon", appExePath + ",0");
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("UninstallString", Quote(setupExePath) + " /uninstall");
        key.SetValue("QuietUninstallString", Quote(setupExePath) + " /uninstall /quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void DeleteShortcuts()
    {
        DeleteFileIfExists(GetDesktopShortcutPath());

        var startMenuDirectory = GetStartMenuDirectory();
        DeleteFileIfExists(Path.Combine(startMenuDirectory, "OmniPlay.lnk"));
        DeleteFileIfExists(Path.Combine(startMenuDirectory, "卸载 OmniPlay.lnk"));

        if (Directory.Exists(startMenuDirectory) && !Directory.EnumerateFileSystemEntries(startMenuDirectory).Any())
        {
            Directory.Delete(startMenuDirectory);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void StopRunningApp()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Best effort: the following file operations will report the real failure if files remain locked.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void ScheduleDirectoryRemovalAfterExit(string installRoot)
    {
        var escapedInstallRoot = installRoot.Replace("\"", "\\\"");
        var arguments = "/c timeout /t 2 /nobreak > nul & rmdir /s /q \"" + escapedInstallRoot + "\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });
    }

    private static string GetInstallRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            ProductName);
    }

    private static string GetStartMenuDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            ProductName);
    }

    private static string GetDesktopShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "OmniPlay.lnk");
    }

    private static string GetCurrentExecutablePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前安装器路径。");
    }

    private static bool IsCurrentExecutableUnder(string directory)
    {
        var currentExecutable = Path.GetFullPath(GetCurrentExecutablePath());
        var normalizedDirectory = EnsureTrailingSeparator(Path.GetFullPath(directory));
        return currentExecutable.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSafeInstallRoot(string installRoot)
    {
        var fullInstallRoot = EnsureTrailingSeparator(Path.GetFullPath(installRoot));
        var allowedParent = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs")));

        if (!fullInstallRoot.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(fullInstallRoot)), ProductName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装目录不在允许范围内。");
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string GetSetupVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    }

    private static void Notify(string message, bool quiet, bool isError)
    {
        if (quiet)
        {
            return;
        }

        MessageBox.Show(
            message,
            ProductName,
            MessageBoxButtons.OK,
            isError ? MessageBoxIcon.Error : MessageBoxIcon.Information);
    }

    private sealed record SetupOptions(bool Quiet, bool Uninstall, bool VerifyOnly, bool CreateDesktopShortcut)
    {
        public static SetupOptions Parse(IReadOnlyCollection<string> args)
        {
            var quiet = false;
            var uninstall = false;
            var verifyOnly = false;
            var createDesktopShortcut = true;

            foreach (var arg in args)
            {
                var normalized = arg.Trim();
                if (normalized.Equals("/quiet", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("/silent", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("--quiet", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("--silent", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                    continue;
                }

                if (normalized.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    uninstall = true;
                    continue;
                }

                if (normalized.Equals("/verify", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("--verify", StringComparison.OrdinalIgnoreCase))
                {
                    verifyOnly = true;
                    continue;
                }

                if (normalized.Equals("/nodesktop", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("--no-desktop", StringComparison.OrdinalIgnoreCase))
                {
                    createDesktopShortcut = false;
                }
            }

            return new SetupOptions(quiet, uninstall, verifyOnly, createDesktopShortcut);
        }
    }
}
