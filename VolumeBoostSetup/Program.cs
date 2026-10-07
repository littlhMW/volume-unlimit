using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace VolumeBoostSetup;

internal static class Program
{
    private const string AppName = "音量破限 / Volume Unlimit";
    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VolumeBoost");
    private static readonly string AppExe = Path.Combine(InstallDir, "VolumeBoost.exe");
    private static readonly string Uninstaller = Path.Combine(InstallDir, "VolumeBoost-卸载.exe");
    private const string UninstallKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\VolumeBoost";

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            var silent = args.Any(x => string.Equals(x, "/silent", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "/quiet", StringComparison.OrdinalIgnoreCase));
            if (args.Any(x => string.Equals(x, "/uninstall", StringComparison.OrdinalIgnoreCase))) Uninstall();
            else Install(silent);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "VolumeBoost-Setup-error.txt"), ex.ToString()); } catch { }
            MessageBox(IntPtr.Zero, ex.Message, AppName, 0x10);
            Environment.ExitCode = 1;
        }
    }

    private static void Install(bool silent)
    {
        Directory.CreateDirectory(InstallDir);
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("VolumeBoostPayload.exe")
            ?? throw new InvalidOperationException("安装包中的程序文件缺失。");
        using var output = File.Create(AppExe);
        input.CopyTo(output);
        using var native = Assembly.GetExecutingAssembly().GetManifestResourceStream("ApplicationLoopback.dll")
            ?? throw new InvalidOperationException("安装包中的音频组件缺失。");
        using var nativeOutput = File.Create(Path.Combine(InstallDir, "ApplicationLoopback.dll"));
        native.CopyTo(nativeOutput);
        using var notices = Assembly.GetExecutingAssembly().GetManifestResourceStream("THIRD-PARTY-NOTICES.md")
            ?? throw new InvalidOperationException("安装包中的许可证文件缺失。");
        using var noticesOutput = File.Create(Path.Combine(InstallDir, "THIRD-PARTY-NOTICES.md"));
        notices.CopyTo(noticesOutput);
        File.Copy(Environment.ProcessPath!, Uninstaller, true);

        // Remove shortcuts created by versions before the product was renamed.
        DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "程序音量增益.lnk"));
        DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "程序音量增益.lnk"));
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "音量破限.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(startMenu)!);
        CreateShortcut(startMenu, AppExe, InstallDir);
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "音量破限.lnk");
        CreateShortcut(desktop, AppExe, InstallDir);

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key!.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", "1.1.0");
            key.SetValue("Publisher", "littlhMW");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("DisplayIcon", AppExe);
            key.SetValue("UninstallString", $"\"{Uninstaller}\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        if (!silent) MessageBox(IntPtr.Zero, "安装完成。桌面和开始菜单已创建快捷方式。", AppName, 0x40);
        try { Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true }); } catch { }
    }

    private static void Uninstall()
    {
        foreach (var process in Process.GetProcessesByName("VolumeBoost"))
        {
            try { process.CloseMainWindow(); if (!process.WaitForExit(1000)) process.Kill(true); } catch { }
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "音量破限.lnk"));
        DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "音量破限.lnk"));
        // Remove shortcuts created by versions before the product was renamed.
        DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "程序音量增益.lnk"));
        DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "程序音量增益.lnk"));

        var self = Environment.ProcessPath!;
        var tempCopy = Path.Combine(Path.GetTempPath(), $"VolumeBoost-uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(self, tempCopy, true);
        var escapedSelf = tempCopy.Replace("'", "''");
        var escapedDir = InstallDir.Replace("'", "''");
        var cleanup = $"Start-Sleep -Seconds 2; Remove-Item -LiteralPath '{escapedSelf}' -Force -ErrorAction SilentlyContinue; for ($i=0; $i -lt 10; $i++) {{ try {{ Remove-Item -LiteralPath '{escapedDir}' -Recurse -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Seconds 1 }} }}";
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{cleanup}\"")
        { UseShellExecute = false, CreateNoWindow = true });
        // The cleanup helper removes the install directory after this process exits.
    }

    private static void DeleteIfExists(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static void CreateShortcut(string path, string target, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("无法创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = AppName;
        shortcut.Save();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
