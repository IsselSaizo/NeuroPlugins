using System.Diagnostics;
using Microsoft.Win32;

namespace DiskDoctor.Core;

public sealed class MsiProduct
{
    public string ProductCode;
    public string Packed;
    public string Name;
    public string Version;
    public string LocalPackage;
    public bool InUserData;
    public bool InProductsKey;
}

public sealed class ComponentHealth
{
    public int Refs;      // сколько компонентов ссылаются на продукт
    public int Sampled;   // сколько файловых путей проверено
    public int Missing;   // сколько из них отсутствует
    public int RootMissing; // у скольких отсутствует даже папка установки (первые 3 уровня пути)
}

/// <summary>Работа с внутренней базой установщика Windows (MSI) через реестр.</summary>
public static class MsiRegistry
{
    const string UserDataKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";
    const string ClassesProducts = @"SOFTWARE\Classes\Installer\Products";

    public static Dictionary<string, MsiProduct> AllProducts()
    {
        var map = new Dictionary<string, MsiProduct>(StringComparer.OrdinalIgnoreCase);
        MsiProduct Get(string packed)
        {
            if (!map.TryGetValue(packed, out var p))
                map[packed] = p = new MsiProduct { Packed = packed, ProductCode = Reg.UnpackGuid(packed) };
            return p;
        }

        foreach (var sid in Reg.SubKeys(RegistryHive.LocalMachine, UserDataKey))
            foreach (var packed in Reg.SubKeys(RegistryHive.LocalMachine, $@"{UserDataKey}\{sid}\Products"))
            {
                if (packed.Length != 32) continue;
                using var k = Reg.Open(RegistryHive.LocalMachine, $@"{UserDataKey}\{sid}\Products\{packed}\InstallProperties");
                var p = Get(packed);
                p.InUserData = true;
                p.Name ??= Reg.Str(k, "DisplayName");
                p.Version ??= Reg.Str(k, "DisplayVersion");
                p.LocalPackage ??= Reg.Str(k, "LocalPackage");
            }

        void FromProductsKey(RegistryHive h, string path)
        {
            foreach (var packed in Reg.SubKeys(h, path))
            {
                if (packed.Length != 32) continue;
                using var k = Reg.Open(h, $@"{path}\{packed}");
                var p = Get(packed);
                p.InProductsKey = true;
                p.Name ??= Reg.Str(k, "ProductName");
            }
        }
        FromProductsKey(RegistryHive.LocalMachine, ClassesProducts);
        foreach (var sid in Reg.UserSids())
            FromProductsKey(RegistryHive.Users, $@"{sid}\Software\Microsoft\Installer\Products");
        return map;
    }

    /// <summary>Один проход по всем компонентам MSI: какие файлы продуктов реально существуют.</summary>
    public static Dictionary<string, ComponentHealth> ScanComponents(CancellationToken ct = default)
    {
        var res = new Dictionary<string, ComponentHealth>(StringComparer.OrdinalIgnoreCase);
        const int maxSample = 12;
        foreach (var sid in Reg.SubKeys(RegistryHive.LocalMachine, UserDataKey))
        {
            using var comps = Reg.Open(RegistryHive.LocalMachine, $@"{UserDataKey}\{sid}\Components");
            if (comps == null) continue;
            foreach (var c in comps.GetSubKeyNames())
            {
                if (ct.IsCancellationRequested) return res;
                try
                {
                    using var ck = comps.OpenSubKey(c);
                    if (ck == null) continue;
                    foreach (var v in ck.GetValueNames())
                    {
                        if (v.Length != 32) continue;
                        if (!res.TryGetValue(v, out var h)) res[v] = h = new ComponentHealth();
                        h.Refs++;
                        if (h.Sampled >= maxSample) continue;
                        string path = ck.GetValue(v) as string;
                        if (!LooksLikeFilePath(path, out string norm)) continue;
                        h.Sampled++;
                        if (!File.Exists(norm) && !Directory.Exists(norm)) h.Missing++;
                        var segs = norm.TrimEnd('\\').Split('\\');
                        string root = string.Join('\\', segs.Take(Math.Min(3, segs.Length - 1))) + "\\";
                        if (!Directory.Exists(root)) h.RootMissing++;
                    }
                }
                catch { }
            }
        }
        return res;
    }

    static bool LooksLikeFilePath(string p, out string norm)
    {
        norm = null;
        if (string.IsNullOrEmpty(p) || p.Length < 4) return false;
        // форматы: "C:\Program Files\...", "C:\?Windows\system32\x.dll", "C?\..."; "01:\", "02:\" — это реестр
        if (!char.IsLetter(p[0]) || (p[1] != ':' && p[1] != '?')) return false;
        string s = p.Replace("?", "");
        if (s.Length < 4 || s[1] != '\\' && s[1] != ':') return false;
        if (s[1] == '\\') s = s[0] + ":" + s.Substring(1);
        if (s[2] != '\\') return false;
        norm = s;
        return true;
    }

    /// <summary>Все ключи/значения реестра, относящиеся к регистрации MSI-продукта, и его кэшированный .msi.</summary>
    public static (List<RegRef> refs, List<string> files, int componentRefs) RegistrationOf(string productCode, CancellationToken ct = default)
    {
        string packed = Reg.PackGuid(productCode);
        var refs = new List<RegRef>();
        var files = new List<string>();
        int compRefs = 0;
        if (packed == null) return (refs, files, 0);
        var LM = RegistryHive.LocalMachine;

        void KeyIf(RegistryHive h, string sub) { var r = new RegRef(h, sub); if (r.Exists()) refs.Add(r); }
        void UpgradeValues(RegistryHive h, string sub)
        {
            using var k = Reg.Open(h, sub);
            if (k == null) return;
            foreach (var uc in k.GetSubKeyNames())
            {
                using var uk = k.OpenSubKey(uc);
                if (uk != null && uk.GetValueNames().Contains(packed, StringComparer.OrdinalIgnoreCase))
                    refs.Add(new RegRef(h, $@"{sub}\{uc}", packed, true));
            }
        }

        // для всей машины
        KeyIf(LM, $@"{ClassesProducts}\{packed}");
        KeyIf(LM, $@"SOFTWARE\Classes\Installer\Features\{packed}");
        KeyIf(LM, $@"SOFTWARE\Classes\Installer\Dependencies\{productCode}");
        UpgradeValues(LM, @"SOFTWARE\Classes\Installer\UpgradeCodes");
        UpgradeValues(LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes");

        // UserData (для каждого SID)
        foreach (var sid in Reg.SubKeys(LM, UserDataKey))
        {
            string pk = $@"{UserDataKey}\{sid}\Products\{packed}";
            using (var ip = Reg.Open(LM, pk + @"\InstallProperties"))
            {
                var lp = Reg.Str(ip, "LocalPackage");
                if (!string.IsNullOrEmpty(lp) && File.Exists(lp)) files.Add(lp);
            }
            KeyIf(LM, pk);
            using var comps = Reg.Open(LM, $@"{UserDataKey}\{sid}\Components");
            if (comps == null) continue;
            foreach (var c in comps.GetSubKeyNames())
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var ck = comps.OpenSubKey(c);
                    if (ck != null && ck.GetValue(packed) != null)
                    {
                        refs.Add(new RegRef(LM, $@"{UserDataKey}\{sid}\Components\{c}", packed, true));
                        compRefs++;
                    }
                }
                catch { }
            }
        }

        // Managed (политики)
        foreach (var sid in Reg.SubKeys(LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed"))
        {
            string b = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed\{sid}\Installer";
            KeyIf(LM, $@"{b}\Products\{packed}");
            KeyIf(LM, $@"{b}\Features\{packed}");
            UpgradeValues(LM, $@"{b}\UpgradeCodes");
        }

        // установки «для пользователя»
        foreach (var sid in Reg.UserSids())
        {
            string b = $@"{sid}\Software\Microsoft\Installer";
            KeyIf(RegistryHive.Users, $@"{b}\Products\{packed}");
            KeyIf(RegistryHive.Users, $@"{b}\Features\{packed}");
            UpgradeValues(RegistryHive.Users, $@"{b}\UpgradeCodes");
            KeyIf(RegistryHive.Users, $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Uninstall\{productCode}");
        }

        // запись в «Программы и компоненты»
        KeyIf(LM, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{productCode}");
        KeyIf(LM, $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{productCode}");
        return (refs, files, compRefs);
    }

    /// <summary>Файлы в Windows\Installer, на которые не ссылается ни один продукт или патч.</summary>
    public static IEnumerable<string> OrphanedInstallerFiles()
    {
        string dir = Path.Combine(Sys.WinDir, "Installer");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int products = 0;
        foreach (var sid in Reg.SubKeys(RegistryHive.LocalMachine, UserDataKey))
        {
            foreach (var p in Reg.SubKeys(RegistryHive.LocalMachine, $@"{UserDataKey}\{sid}\Products"))
            {
                using var k = Reg.Open(RegistryHive.LocalMachine, $@"{UserDataKey}\{sid}\Products\{p}\InstallProperties");
                var lp = Reg.Str(k, "LocalPackage");
                if (lp != null) { used.Add(Path.GetFileName(lp)); products++; }
            }
            foreach (var p in Reg.SubKeys(RegistryHive.LocalMachine, $@"{UserDataKey}\{sid}\Patches"))
            {
                using var k = Reg.Open(RegistryHive.LocalMachine, $@"{UserDataKey}\{sid}\Patches\{p}");
                var lp = Reg.Str(k, "LocalPackage");
                if (lp != null) used.Add(Path.GetFileName(lp));
            }
        }
        if (products < 5) return Array.Empty<string>(); // не удалось прочитать базу — ничего не трогаем
        List<string> all;
        try
        {
            all = Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msp", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch { return Array.Empty<string>(); }
        var orphans = all.Where(f => !used.Contains(Path.GetFileName(f))).ToList();
        if (all.Count > 10 && orphans.Count > all.Count * 0.8) return Array.Empty<string>(); // подозрительно — перестраховка
        return orphans;
    }
}

public sealed class InstalledProgram
{
    public string Name, Version, Publisher, InstallLocation, UninstallString, QuietUninstallString, DisplayIcon, InstallDate, InstallSource;
    public long EstimatedSizeKB;
    public RegistryHive Hive;
    public string KeyPath;        // подраздел реестра записи удаления
    public string KeyName;
    public string Scope;          // 64-бит / 32-бит / пользователь
    public bool IsMsi;
    public string ProductCode;
    public bool Hidden;           // SystemComponent / обновление
    public List<string> Problems = new();
    public bool Broken => Problems.Count > 0;
    public RegRef UninstallKey => new(Hive, KeyPath);
    public string FullKey => Reg.HiveName(Hive) + "\\" + KeyPath;
}

public static class ProgramList
{
    static readonly string[] Roots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    public static List<InstalledProgram> Load(CancellationToken ct = default)
    {
        var list = new List<InstalledProgram>();
        void Read(RegistryHive hive, string root, string scope)
        {
            foreach (var name in Reg.SubKeys(hive, root))
            {
                using var k = Reg.Open(hive, $@"{root}\{name}");
                if (k == null) continue;
                string dn = Reg.Str(k, "DisplayName");
                if (string.IsNullOrWhiteSpace(dn)) continue;
                var p = new InstalledProgram
                {
                    Name = dn.Trim(),
                    Version = Reg.Str(k, "DisplayVersion"),
                    Publisher = Reg.Str(k, "Publisher"),
                    InstallLocation = Expand(Reg.Str(k, "InstallLocation"))?.Trim().Trim('"'),
                    UninstallString = Reg.Str(k, "UninstallString"),
                    QuietUninstallString = Reg.Str(k, "QuietUninstallString"),
                    DisplayIcon = Reg.Str(k, "DisplayIcon"),
                    InstallDate = Reg.Str(k, "InstallDate"),
                    InstallSource = Reg.Str(k, "InstallSource"),
                    Hive = hive, KeyPath = $@"{root}\{name}", KeyName = name, Scope = scope,
                };
                try { p.EstimatedSizeKB = Convert.ToInt64(k.GetValue("EstimatedSize") ?? 0L); } catch { }
                object wi = k.GetValue("WindowsInstaller");
                p.IsMsi = (wi is int i && i == 1) || (Reg.IsGuid(name) && (p.UninstallString ?? "").Contains("msiexec", StringComparison.OrdinalIgnoreCase));
                if (Reg.IsGuid(name)) p.ProductCode = name.ToUpperInvariant();
                p.Hidden = (k.GetValue("SystemComponent") is int sc && sc == 1) || Reg.Str(k, "ParentKeyName") != null || Reg.Str(k, "ReleaseType") is "Update" or "Hotfix" or "Security Update";
                list.Add(p);
            }
        }
        Read(RegistryHive.LocalMachine, Roots[0], "Все пользователи (64)");
        Read(RegistryHive.LocalMachine, Roots[1], "Все пользователи (32)");
        foreach (var sid in Reg.UserSids())
            Read(RegistryHive.Users, $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Uninstall", "Пользователь");

        // проверки целостности
        Dictionary<string, MsiProduct> msi = null;
        Dictionary<string, ComponentHealth> comps = null;
        try { msi = MsiRegistry.AllProducts(); } catch { }
        try { comps = MsiRegistry.ScanComponents(ct); } catch { }
        foreach (var p in list) Check(p, msi, comps);
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string Expand(string s) => s == null ? null : Environment.ExpandEnvironmentVariables(s);

    /// <summary>Извлечь путь к исполняемому файлу из командной строки.</summary>
    public static (string exe, string args) SplitCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return (null, null);
        cmd = Expand(cmd.Trim());
        if (cmd.StartsWith("\""))
        {
            int q = cmd.IndexOf('"', 1);
            if (q > 0) return (cmd.Substring(1, q - 1), cmd.Substring(q + 1).Trim());
        }
        int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (e > 0) return (cmd.Substring(0, e + 4), cmd.Substring(e + 4).Trim());
        int sp = cmd.IndexOf(' ');
        return sp > 0 ? (cmd.Substring(0, sp), cmd.Substring(sp + 1)) : (cmd, "");
    }

    static bool IsSystemTool(string exe)
    {
        string n = Path.GetFileName(exe ?? "").ToLowerInvariant();
        return n is "msiexec.exe" or "msiexec" or "rundll32.exe" or "rundll32" or "cmd.exe" or "cmd" or "powershell.exe" or "wscript.exe";
    }

    static void Check(InstalledProgram p, Dictionary<string, MsiProduct> msi, Dictionary<string, ComponentHealth> comps)
    {
        if (!string.IsNullOrEmpty(p.InstallLocation) && p.InstallLocation.Length > 3 && !Directory.Exists(p.InstallLocation) && !File.Exists(p.InstallLocation))
            p.Problems.Add($"Папка установки отсутствует ({p.InstallLocation})");

        if (p.IsMsi && p.ProductCode != null && msi != null)
        {
            string packed = Reg.PackGuid(p.ProductCode);
            if (!msi.TryGetValue(packed, out var mp))
                p.Problems.Add("Запись-призрак: установщик Windows не знает об этой программе — штатное удаление невозможно");
            else
            {
                if (!string.IsNullOrEmpty(mp.LocalPackage) && !File.Exists(mp.LocalPackage))
                    p.Problems.Add("Кэшированный установщик (.msi) удалён — удаление/обновление/переустановка сломаны");
                if (comps != null && comps.TryGetValue(packed, out var h) && h.Sampled >= 3 && h.Missing >= h.Sampled * 0.8 && h.RootMissing >= h.Sampled * 0.5
                    && (string.IsNullOrEmpty(p.InstallLocation) || !Directory.Exists(p.InstallLocation)))
                    p.Problems.Add($"Файлы программы удалены ({h.Missing} из {h.Sampled} проверенных отсутствуют), а регистрация осталась — переустановка, скорее всего, блокируется");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(p.UninstallString))
            {
                if (!p.Hidden) p.Problems.Add("Нет команды удаления");
            }
            else
            {
                var (exe, _) = SplitCommand(p.UninstallString);
                if (exe != null && !IsSystemTool(exe) && Path.IsPathRooted(exe) && !File.Exists(exe))
                    p.Problems.Add($"Деинсталлятор не найден ({exe})");
            }
        }

        if (!string.IsNullOrEmpty(p.DisplayIcon) && p.Problems.Count == 0)
        {
            string icon = Expand(p.DisplayIcon).Split(',')[0].Trim().Trim('"');
            if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(icon) && !File.Exists(icon) && !IsSystemTool(icon)
                && (string.IsNullOrEmpty(p.InstallLocation) || !Directory.Exists(p.InstallLocation)))
                p.Problems.Add($"Исполняемый файл программы не найден ({icon})");
        }
    }

    /// <summary>Штатное удаление. Возвращает запущенный процесс (или null).</summary>
    public static Process RunUninstaller(InstalledProgram p, string logDir)
    {
        try
        {
            if (p.IsMsi && p.ProductCode != null)
            {
                string log = Path.Combine(logDir, "msi_uninstall_" + p.ProductCode.Trim('{', '}') + ".log");
                Log.Write($"Запуск: msiexec /x {p.ProductCode} (журнал: {log})");
                return Process.Start(new ProcessStartInfo("msiexec.exe", $"/x {p.ProductCode} /l*v \"{log}\"") { UseShellExecute = true });
            }
            var (exe, args) = SplitCommand(p.UninstallString);
            if (exe == null) return null;
            Log.Write($"Запуск деинсталлятора: {p.UninstallString}");
            return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Не удалось запустить деинсталлятор: {ex.Message}");
            return null;
        }
    }
}
