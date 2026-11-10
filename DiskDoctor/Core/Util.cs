using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DiskDoctor.Core;

public static class Fmt
{
    public static string Size(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} Б" : $"{v:0.##} {u[i]}";
    }

    public static string Pct(long part, long whole) =>
        whole <= 0 ? "" : $"{100.0 * part / whole:0.0}%";

    public static string Count(long n) => n.ToString("N0");
}

public static class Log
{
    static readonly object Gate = new();
    public static event Action<string> Line;
    public static string FilePath { get; } = Path.Combine(AppDir.Data, "DiskDoctor.log");

    public static void Write(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}", Encoding.UTF8); } catch { }
        }
        try { Line?.Invoke(line); } catch { }
    }
}

public static class AppDir
{
    public static string Data
    {
        get
        {
            string dir = AppContext.BaseDirectory;
            try
            {
                string test = Path.Combine(dir, ".write_test");
                File.WriteAllText(test, "");
                File.Delete(test);
                return dir;
            }
            catch
            {
                string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskDoctor");
                Directory.CreateDirectory(alt);
                return alt;
            }
        }
    }

    public static string NewBackupDir()
    {
        string d = Path.Combine(Data, "Backups", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(d);
        return d;
    }
}

public static class Sys
{
    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string SystemDrive => Path.GetPathRoot(WinDir);

    static Encoding _oem;
    public static Encoding OemEncoding
    {
        get
        {
            if (_oem != null) return _oem;
            try { _oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { _oem = Encoding.UTF8; }
            return _oem;
        }
    }

    /// <summary>Запуск консольной утилиты с ожиданием и захватом вывода.</summary>
    public static (int code, string output) Run(string exe, string args, int timeoutMs = 600_000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = OemEncoding,
                StandardErrorEncoding = OemEncoding,
            };
            using var p = Process.Start(psi);
            p.StandardInput.Close();
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, (so.Result + se.Result).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { }
    }

    public static void ShellOpen(string target, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = true }); } catch (Exception ex) { Log.Write("Не удалось запустить " + target + ": " + ex.Message); }
    }

    /// <summary>Открыть консоль, выполнить команду и оставить окно открытым.</summary>
    public static void RunInConsole(string command)
    {
        try { Process.Start(new ProcessStartInfo("cmd.exe", "/k " + command) { UseShellExecute = true }); } catch { }
    }

    public static void OpenRegedit(string fullKey)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                k.SetValue("LastKey", "Компьютер\\" + fullKey);
            foreach (var p in Process.GetProcessesByName("regedit")) { try { p.Kill(); } catch { } }
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
        }
        catch { }
    }
}

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
    public const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Переместить файл/папку в Корзину. Возвращает true при успехе.</summary>
    public static bool SendToRecycleBin(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI,
        };
        int r = SHFileOperation(ref op);
        return r == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }
}
