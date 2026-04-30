using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Application = System.Windows.Forms.Application;
using Button = System.Windows.Forms.Button;
using CheckBox = System.Windows.Forms.CheckBox;
using DialogResult = System.Windows.Forms.DialogResult;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using Form = System.Windows.Forms.Form;
using FormBorderStyle = System.Windows.Forms.FormBorderStyle;
using FormStartPosition = System.Windows.Forms.FormStartPosition;
using Label = System.Windows.Forms.Label;
using MessageBox = System.Windows.Forms.MessageBox;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;
using TextBox = System.Windows.Forms.TextBox;

namespace OmniPlay.Setup;

internal static class Program
{
    private const string ProductName = "览影";
    private const string LegacyProductName = "OmniPlay";
    private const string AppExeName = "OmniPlay.Desktop.exe";
    private const string InstalledSetupExeName = "setup.exe";
    private const string PayloadResourceName = "OmniPlay.Payload.zip";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OmniPlay";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var options = SetupOptions.Default;

        try
        {
            options = SetupOptions.Parse(args);

            if (options.VerifyOnly)
            {
                VerifyPayload();
                Notify("安装包校验通过。", options.Quiet, isError: false);
                return 0;
            }

            if (options.Uninstall)
            {
                Uninstall(options);
                Notify("览影已卸载。", options.Quiet, isError: false);
                return 0;
            }

            if (!Install(options))
            {
                return 0;
            }

            Notify("览影安装完成。", options.Quiet, isError: false);
            return 0;
        }
        catch (Exception ex)
        {
            Notify("览影安装程序运行失败：\n\n" + ex.Message, options.Quiet, isError: true);
            return 1;
        }
    }

    private static bool Install(SetupOptions options)
    {
        var installSelection = ResolveInstallSelection(options);
        if (installSelection is null)
        {
            return false;
        }

        var installRoot = installSelection.InstallRoot;
        EnsureSafeInstallRoot(installRoot);

        StopRunningApp();
        Directory.CreateDirectory(installRoot);
        CleanInstallDirectory(installRoot, GetCurrentExecutablePath());
        ExtractPayload(installRoot);

        var appExePath = Path.Combine(installRoot, AppExeName);
        if (!File.Exists(appExePath))
        {
            throw new FileNotFoundException("安装包内缺少 OmniPlay.Desktop.exe。", appExePath);
        }

        var installedSetupPath = Path.Combine(installRoot, InstalledSetupExeName);
        CopyCurrentSetup(installedSetupPath);
        DeleteShortcuts();
        CreateShortcuts(appExePath, installedSetupPath, installSelection.CreateDesktopShortcut);
        RegisterUninstaller(installRoot, appExePath, installedSetupPath);
        return true;
    }

    private static void Uninstall(SetupOptions options)
    {
        var installRoot = options.InstallDirectory is null
            ? GetRegisteredInstallRoot() ?? GetDefaultInstallRoot()
            : NormalizeInstallRoot(options.InstallDirectory);
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
            Notify("剩余安装文件将在安装程序退出后删除。", options.Quiet, isError: false);
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
            throw new InvalidOperationException("安装程序缺少应用载荷，请使用 windows\\package-setup.ps1 重新生成安装包。");
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
            Path.Combine(startMenuDirectory, ProductName + ".lnk"),
            appExePath,
            arguments: string.Empty,
            workingDirectory: Path.GetDirectoryName(appExePath) ?? string.Empty,
            iconLocation: appExePath + ",0");

        CreateShortcut(
            Path.Combine(startMenuDirectory, "卸载 " + ProductName + ".lnk"),
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
        DeleteFileIfExists(GetLegacyDesktopShortcutPath());

        var startMenuDirectory = GetStartMenuDirectory();
        DeleteFileIfExists(Path.Combine(startMenuDirectory, ProductName + ".lnk"));
        DeleteFileIfExists(Path.Combine(startMenuDirectory, "卸载 " + ProductName + ".lnk"));

        if (Directory.Exists(startMenuDirectory) && !Directory.EnumerateFileSystemEntries(startMenuDirectory).Any())
        {
            Directory.Delete(startMenuDirectory);
        }

        var legacyStartMenuDirectory = GetLegacyStartMenuDirectory();
        DeleteFileIfExists(Path.Combine(legacyStartMenuDirectory, LegacyProductName + ".lnk"));
        DeleteFileIfExists(Path.Combine(legacyStartMenuDirectory, "卸载 " + LegacyProductName + ".lnk"));

        if (Directory.Exists(legacyStartMenuDirectory) && !Directory.EnumerateFileSystemEntries(legacyStartMenuDirectory).Any())
        {
            Directory.Delete(legacyStartMenuDirectory);
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
                // Best effort: file operations below will report the real failure if files remain locked.
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

    private static InstallSelection? ResolveInstallSelection(SetupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.InstallDirectory))
        {
            return new InstallSelection(
                NormalizeInstallRoot(options.InstallDirectory),
                options.CreateDesktopShortcut);
        }

        var defaultInstallRoot = GetRegisteredInstallRoot() ?? GetDefaultInstallRoot();
        if (options.Quiet)
        {
            return new InstallSelection(
                NormalizeInstallRoot(defaultInstallRoot),
                options.CreateDesktopShortcut);
        }

        return ShowInstallDirectoryDialog(defaultInstallRoot, options.CreateDesktopShortcut);
    }

    private static InstallSelection? ShowInstallDirectoryDialog(string defaultInstallRoot, bool createDesktopShortcut)
    {
        using var form = new Form
        {
            Text = "览影安装程序",
            ClientSize = new Size(560, 214),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen
        };

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Location = new Point(16, 16),
            Text = "选择安装目录"
        };

        var hintLabel = new Label
        {
            AutoSize = false,
            Location = new Point(16, 42),
            Size = new Size(528, 34),
            Text = "如果选择的目录不是览影，将自动创建览影子目录。"
        };

        var pathBox = new TextBox
        {
            Location = new Point(16, 82),
            Size = new Size(418, 27),
            Text = NormalizeInstallRoot(defaultInstallRoot)
        };

        var browseButton = new Button
        {
            Location = new Point(444, 80),
            Size = new Size(100, 30),
            Text = "浏览..."
        };

        var desktopShortcutCheckBox = new CheckBox
        {
            AutoSize = true,
            Checked = createDesktopShortcut,
            Location = new Point(16, 122),
            Text = "创建桌面快捷方式"
        };

        var installButton = new Button
        {
            Location = new Point(338, 166),
            Size = new Size(100, 32),
            Text = "安装"
        };

        var cancelButton = new Button
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(444, 166),
            Size = new Size(100, 32),
            Text = "取消"
        };

        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择览影安装目录",
                ShowNewFolderButton = true,
                SelectedPath = GetNearestExistingDirectory(pathBox.Text) ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (dialog.ShowDialog(form) == DialogResult.OK)
            {
                pathBox.Text = NormalizeInstallRoot(dialog.SelectedPath);
            }
        };

        installButton.Click += (_, _) =>
        {
            try
            {
                pathBox.Text = NormalizeInstallRoot(pathBox.Text);
                EnsureSafeInstallRoot(pathBox.Text);
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(form, ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        form.Controls.Add(titleLabel);
        form.Controls.Add(hintLabel);
        form.Controls.Add(pathBox);
        form.Controls.Add(browseButton);
        form.Controls.Add(desktopShortcutCheckBox);
        form.Controls.Add(installButton);
        form.Controls.Add(cancelButton);
        form.AcceptButton = installButton;
        form.CancelButton = cancelButton;

        return form.ShowDialog() == DialogResult.OK
            ? new InstallSelection(pathBox.Text, desktopShortcutCheckBox.Checked)
            : null;
    }

    private static string NormalizeInstallRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("安装目录不能为空。");
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        var fullPath = Path.GetFullPath(expandedPath);
        var trimmedPath = Path.TrimEndingDirectorySeparator(fullPath);

        if (!IsAllowedInstallLeaf(Path.GetFileName(trimmedPath)))
        {
            trimmedPath = Path.Combine(trimmedPath, ProductName);
        }

        return Path.GetFullPath(trimmedPath);
    }

    private static string? GetNearestExistingDirectory(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (!Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                current = parent;
            }

            return current;
        }
        catch
        {
            return null;
        }
    }

    private static string GetDefaultInstallRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProductName);
    }

    private static string? GetRegisteredInstallRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("InstallLocation") as string;
    }

    private static string GetStartMenuDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            ProductName);
    }

    private static string GetLegacyStartMenuDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            LegacyProductName);
    }

    private static string GetDesktopShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ProductName + ".lnk");
    }

    private static string GetLegacyDesktopShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            LegacyProductName + ".lnk");
    }

    private static string GetCurrentExecutablePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前安装程序路径。");
    }

    private static bool IsCurrentExecutableUnder(string directory)
    {
        var currentExecutable = Path.GetFullPath(GetCurrentExecutablePath());
        var normalizedDirectory = EnsureTrailingSeparator(Path.GetFullPath(directory));
        return currentExecutable.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSafeInstallRoot(string installRoot)
    {
        var fullInstallRoot = Path.GetFullPath(Path.TrimEndingDirectorySeparator(installRoot));
        var leafName = Path.GetFileName(fullInstallRoot);
        if (!IsAllowedInstallLeaf(leafName))
        {
            throw new InvalidOperationException("安装目录必须命名为览影，或选择一个可创建览影子目录的父目录。");
        }

        if (File.Exists(fullInstallRoot))
        {
            throw new InvalidOperationException("安装目录指向了一个已存在的文件。");
        }

        var driveRoot = Path.GetPathRoot(fullInstallRoot);
        if (string.IsNullOrWhiteSpace(driveRoot)
            || string.Equals(fullInstallRoot, Path.TrimEndingDirectorySeparator(driveRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装目录不能是磁盘根目录。");
        }

        if (IsSameOrUnderPath(fullInstallRoot, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            || IsSameOrUnderPath(fullInstallRoot, Environment.GetFolderPath(Environment.SpecialFolder.System))
            || IsSameOrUnderPath(fullInstallRoot, Path.GetTempPath()))
        {
            throw new InvalidOperationException("该目录不适合安装应用文件。");
        }
    }

    private static bool IsAllowedInstallLeaf(string? leafName)
    {
        return string.Equals(leafName, ProductName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(leafName, LegacyProductName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrUnderPath(string path, string parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return false;
        }

        var normalizedPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var normalizedParent = EnsureTrailingSeparator(Path.GetFullPath(parentPath));
        return normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
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

    private sealed record InstallSelection(string InstallRoot, bool CreateDesktopShortcut);

    private sealed record SetupOptions(
        bool Quiet,
        bool Uninstall,
        bool VerifyOnly,
        bool CreateDesktopShortcut,
        string? InstallDirectory)
    {
        public static SetupOptions Default { get; } = new(
            Quiet: false,
            Uninstall: false,
            VerifyOnly: false,
            CreateDesktopShortcut: true,
            InstallDirectory: null);

        public static SetupOptions Parse(IReadOnlyList<string> args)
        {
            var quiet = false;
            var uninstall = false;
            var verifyOnly = false;
            var createDesktopShortcut = true;
            string? installDirectory = null;

            for (var i = 0; i < args.Count; i++)
            {
                var normalized = args[i].Trim();
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (Matches(normalized, "/quiet", "/silent", "--quiet", "--silent"))
                {
                    quiet = true;
                    continue;
                }

                if (Matches(normalized, "/uninstall", "--uninstall"))
                {
                    uninstall = true;
                    continue;
                }

                if (Matches(normalized, "/verify", "--verify"))
                {
                    verifyOnly = true;
                    continue;
                }

                if (Matches(normalized, "/nodesktop", "--no-desktop"))
                {
                    createDesktopShortcut = false;
                    continue;
                }

                if (TryReadInlineValue(normalized, out var inlineValue, "/installDir", "--install-dir", "/dir", "--dir"))
                {
                    installDirectory = inlineValue;
                    continue;
                }

                if (Matches(normalized, "/installDir", "--install-dir", "/dir", "--dir"))
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new InvalidOperationException("安装目录参数缺少路径值。");
                    }

                    installDirectory = args[++i];
                }
            }

            return new SetupOptions(quiet, uninstall, verifyOnly, createDesktopShortcut, installDirectory);
        }

        private static bool Matches(string value, params string[] names)
        {
            return names.Any(name => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryReadInlineValue(string value, out string optionValue, params string[] names)
        {
            foreach (var name in names)
            {
                var prefix = name + "=";
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    optionValue = value[prefix.Length..].Trim().Trim('"');
                    return true;
                }
            }

            optionValue = string.Empty;
            return false;
        }
    }
}
