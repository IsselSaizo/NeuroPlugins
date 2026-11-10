using System.Text;
using Microsoft.Win32;

namespace DiskDoctor.Core;

public enum LeftKind { Folder, File, Shortcut, RegKey, RegValue, Uninstall, Msi, Service, Task }

public sealed class LeftoverItem
{
    public LeftKind Kind;
    public string Display;
    public string Path;                 // файл/папка
    public List<RegRef> Regs = new();   // для реестра и MSI
    public List<string> Files = new();  // доп. файлы (кэшированный .msi)
    public string ServiceName;
    public string TaskName;
    public long Size;
    public bool Checked;
    public string Note;
    public Risk Risk = Risk.Caution;

    public string KindName => Kind switch
    {
        LeftKind.Folder => "Папка",
        LeftKind.File => "Файл",
        LeftKind.Shortcut => "Ярлык",
        LeftKind.RegKey => "Раздел реестра",
        LeftKind.RegValue => "Значение реестра",
        LeftKind.Uninstall => "Запись «Программы и компоненты»",
        LeftKind.Msi => "Регистрация установщика MSI",
        LeftKind.Service => "Служба",
        LeftKind.Task => "Задание планировщика",
        _ => "",
    };
}

public sealed class SearchTerm
{
    public string Text;
    public bool Strong;  // совпадение по названию программы (отмечать по умолчанию)
    public SearchTerm(string t, bool strong) { Text = t.Trim(); Strong = strong; }
}

public static class LeftoverFinder
{
    static readonly HashSet<string> TooGeneric = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "system", "program", "programs", "files", "common", "data", "app", "apps", "update", "updater",
        "setup", "install", "installer", "software", "corporation", "corp", "inc", "ltd", "llc", "gmbh", "the", "for", "and",
        "tools", "tool", "service", "services", "driver", "drivers", "runtime", "x64", "x86", "64-bit", "32-bit", "version", "edition",
        "free", "pro", "studio", "player", "manager", "client", "launcher", "helper", "user", "users", "local", "cache", "temp",
        "intel", "nvidia", "amd", "google", "apple", "adobe", "realtek", "net", "framework", "visual", "redistributable",
    };

    public static bool IsTooGeneric(string term) => term.Trim().Length < 3 || TooGeneric.Contains(term.Trim());

    /// <summary>Поисковые слова для программы: её название без версии и издатель (слабое совпадение).</summary>
    public static List<SearchTerm> TermsFor(InstalledProgram p)
    {
        var terms = new List<SearchTerm>();
        string name = CleanName(p.Name);
        if (!IsTooGeneric(name)) terms.Add(new SearchTerm(name, true));
        if (!string.IsNullOrWhiteSpace(p.InstallLocation))
        {
            string leaf = System.IO.Path.GetFileName(p.InstallLocation.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(leaf) && !IsTooGeneric(leaf) && !terms.Any(t => t.Text.Equals(leaf, StringComparison.OrdinalIgnoreCase)))
                terms.Add(new SearchTerm(leaf, true));
        }
        string pub = CleanName(p.Publisher ?? "");
        foreach (var suffix in new[] { " Inc.", " Inc", " Ltd.", " Ltd", " LLC", " GmbH", " Corporation", " Corp.", " Co.", " Pty", " s.r.o.", "," })
            pub = pub.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
        pub = pub.Trim();
        if (pub.Length >= 4 && !IsTooGeneric(pub) && !terms.Any(t => t.Text.Contains(pub, StringComparison.OrdinalIgnoreCase)))
            terms.Add(new SearchTerm(pub, false));
        return terms;
    }

    /// <summary>"DaVinci Resolve 18.6 (x64)" → "DaVinci Resolve"</summary>
    public static string CleanName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var words = s.Replace("(x64)", "").Replace("(x86)", "").Replace("(64-bit)", "").Replace("(32-bit)", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && (char.IsDigit(words[^1][0]) || words[^1].StartsWith("v", StringComparison.OrdinalIgnoreCase) && words[^1].Length > 1 && char.IsDigit(words[^1][1]) || words[^1] == "-"))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words).Trim();
    }

    static SearchTerm MatchTerm(string s, List<SearchTerm> terms)
    {
        if (string.IsNullOrEmpty(s)) return null;
        SearchTerm weak = null;
        foreach (var t in terms)
        {
            if (Contains(s, t.Text))
            {
                if (t.Strong) return t;
                weak ??= t;
            }
        }
        return weak;
    }

    static bool Contains(string s, string term)
    {
        if (s.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        // «DaVinci Resolve» ↔ «DaVinciResolve» / «DaVinci_Resolve»
        return Norm(s).Contains(Norm(term));
    }

    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static List<LeftoverItem> Search(List<SearchTerm> terms, IEnumerable<string> productCodes, Action<string> progress, CancellationToken ct)
    {
        var items = new List<LeftoverItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        terms = terms.Where(t => t.Text.Length >= 3).ToList();
        if (terms.Count == 0 && !productCodes.Any()) return items;

        // ---------- MSI ----------
        progress?.Invoke("База установщика Windows…");
        var codes = new HashSet<string>(productCodes.Where(c => c != null), StringComparer.OrdinalIgnoreCase);
        var strongCodes = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        var msiNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in MsiRegistry.AllProducts().Values)
        {
            var t = MatchTerm(p.Name, terms);
            if (t != null && p.ProductCode != null)
            {
                codes.Add(p.ProductCode);
                if (t.Strong) strongCodes.Add(p.ProductCode);
            }
            if (p.ProductCode != null) msiNames[p.ProductCode] = p.Name;
        }
        var arpKeysCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            if (ct.IsCancellationRequested) return items;
            progress?.Invoke($"Регистрация MSI {code}…");
            var (refs, files, compRefs) = MsiRegistry.RegistrationOf(code, ct);
            if (refs.Count == 0 && files.Count == 0) continue;
            foreach (var r in refs.Where(r => r.ValueName == null)) arpKeysCovered.Add(r.KeyPath);
            msiNames.TryGetValue(code, out var nm);
            var it = new LeftoverItem
            {
                Kind = LeftKind.Msi,
                Display = $"{nm ?? "?"} {code}",
                Regs = refs,
                Files = files,
                Size = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }),
                Checked = strongCodes.Contains(code),
                Risk = Risk.Caution,
                Note = $"Разделов/значений реестра: {refs.Count} (из них компонентов: {compRefs})" + (files.Count > 0 ? $"; кэшированный установщик: {string.Join(", ", files)}" : "") +
                       ". Удаление «отвязывает» программу от установщика Windows — после этого её можно установить заново. Сначала попробуйте штатное удаление.",
            };
            items.Add(it);
        }

        // ---------- Записи удаления (не MSI) ----------
        progress?.Invoke("Список установленных программ…");
        void ArpRoot(RegistryHive h, string root)
        {
            foreach (var name in Reg.SubKeys(h, root))
            {
                using var k = Reg.Open(h, $@"{root}\{name}");
                string dn = Reg.Str(k, "DisplayName");
                var t = MatchTerm(dn, terms) ?? MatchTerm(Reg.Str(k, "InstallLocation"), terms);
                if (t == null) continue;
                var rr = new RegRef(h, $@"{root}\{name}");
                if (arpKeysCovered.Contains(rr.KeyPath)) continue;
                items.Add(new LeftoverItem
                {
                    Kind = LeftKind.Uninstall, Display = $"{dn} — {rr.KeyPath}", Regs = { rr }, Checked = t.Strong, Risk = Risk.Caution,
                    Note = "Запись в списке установленных программ. Удаляйте, если штатное удаление не работает.",
                });
            }
        }
        ArpRoot(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        ArpRoot(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        foreach (var sid in Reg.UserSids()) ArpRoot(RegistryHive.Users, $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Uninstall");

        // ---------- Папки и ярлыки ----------
        var profiles = JunkCleaner.Profiles().ToList();
        var roots = new List<(string path, int depth, bool userData)>();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        roots.Add((pf, 2, false)); roots.Add((pf86, 2, false));
        roots.Add((System.IO.Path.Combine(pf, "Common Files"), 2, false)); roots.Add((System.IO.Path.Combine(pf86, "Common Files"), 2, false));
        roots.Add((pd, 2, false));
        roots.Add((System.IO.Path.Combine(pd, @"Microsoft\Windows\Start Menu\Programs"), 2, false));
        roots.Add((Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), 1, false));
        foreach (var prof in profiles)
        {
            roots.Add((System.IO.Path.Combine(prof, @"AppData\Local"), 2, false));
            roots.Add((System.IO.Path.Combine(prof, @"AppData\Roaming"), 2, false));
            roots.Add((System.IO.Path.Combine(prof, @"AppData\LocalLow"), 2, false));
            roots.Add((System.IO.Path.Combine(prof, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"), 2, false));
            roots.Add((System.IO.Path.Combine(prof, "Desktop"), 1, true));
            roots.Add((System.IO.Path.Combine(prof, "Documents"), 1, true));
        }
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            roots.Add((d.RootDirectory.FullName, 1, true));

        foreach (var (root, depth, userData) in roots)
        {
            if (ct.IsCancellationRequested) return items;
            progress?.Invoke("Папки: " + root);
            ScanFolder(root, depth, userData, terms, items, seenPaths, ct);
        }

        // ---------- Реестр: разделы программ ----------
        progress?.Invoke("Реестр: настройки программ…");
        var regRoots = new List<(RegistryHive, string)> { (RegistryHive.LocalMachine, "SOFTWARE"), (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node") };
        foreach (var sid in Reg.UserSids()) { regRoots.Add((RegistryHive.Users, $@"{sid}\Software")); regRoots.Add((RegistryHive.Users, $@"{sid}\Software\WOW6432Node")); }
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft", "Classes", "Policies", "WOW6432Node", "Wow6432Node", "RegisteredApplications", "Clients", "ODBC", "Windows", "Intel", "Khronos" };
        foreach (var (h, root) in regRoots)
        {
            foreach (var k1 in Reg.SubKeys(h, root))
            {
                if (skip.Contains(k1)) continue;
                var t = MatchTerm(k1, terms);
                if (t != null) { AddReg(items, h, $@"{root}\{k1}", t); continue; }
                foreach (var k2 in Reg.SubKeys(h, $@"{root}\{k1}"))
                {
                    var t2 = MatchTerm(k2, terms);
                    if (t2 != null) AddReg(items, h, $@"{root}\{k1}\{k2}", t2);
                }
            }
        }

        // ---------- Автозапуск ----------
        progress?.Invoke("Автозапуск…");
        var runKeys = new List<(RegistryHive, string)>
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        };
        foreach (var sid in Reg.UserSids()) runKeys.Add((RegistryHive.Users, $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Run"));
        foreach (var (h, path) in runKeys)
        {
            using var k = Reg.Open(h, path);
            if (k == null) continue;
            foreach (var v in k.GetValueNames())
            {
                string data = Reg.Str(k, v);
                var t = MatchTerm(v, terms) ?? MatchTerm(data, terms);
                if (t == null) continue;
                items.Add(new LeftoverItem
                {
                    Kind = LeftKind.RegValue, Display = $"Автозапуск «{v}»: {data}", Regs = { new RegRef(h, path, v) },
                    Checked = t.Strong, Risk = Risk.Safe, Note = "Запуск программы при входе в Windows.",
                });
            }
        }

        // App Paths
        foreach (var name in Reg.SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
        {
            using var k = Reg.Open(RegistryHive.LocalMachine, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}");
            string exe = Reg.Str(k, "");
            var t = MatchTerm(exe, terms);
            if (t == null) continue;
            string clean = (exe ?? "").Trim('"');
            bool missing = Path.IsPathRooted(clean) && !File.Exists(clean);
            items.Add(new LeftoverItem
            {
                Kind = LeftKind.RegKey, Display = $"App Paths\\{name} → {exe}", Regs = { new RegRef(RegistryHive.LocalMachine, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}") },
                Checked = t.Strong || missing, Risk = Risk.Safe, Note = missing ? "Ссылается на несуществующий файл." : "Регистрация исполняемого файла.",
            });
        }

        // ---------- Службы ----------
        progress?.Invoke("Службы…");
        foreach (var svc in Reg.SubKeys(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services"))
        {
            using var k = Reg.Open(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{svc}");
            string dn = Reg.Str(k, "DisplayName");
            string img = Reg.Str(k, "ImagePath");
            if (dn != null && dn.StartsWith("@")) dn = null;
            var t = MatchTerm(svc, terms) ?? MatchTerm(dn, terms) ?? MatchTerm(img, terms);
            if (t == null) continue;
            var (exe, _) = ProgramList.SplitCommand(img ?? "");
            bool missing = exe != null && Path.IsPathRooted(exe) && !File.Exists(exe);
            bool driver = (img ?? "").Contains(@"\drivers\", StringComparison.OrdinalIgnoreCase) || (k?.GetValue("Type") is int ty && ty <= 2);
            items.Add(new LeftoverItem
            {
                Kind = LeftKind.Service, ServiceName = svc, Display = $"{svc} ({dn}) → {img}",
                Checked = missing && !driver, Risk = driver ? Risk.Critical : Risk.Caution,
                Note = driver ? "Это ДРАЙВЕР. Удаляйте, только если уверены, что устройство/программа больше не используется."
                     : missing ? "Файл службы отсутствует — это остаток." : "Служба существует и её файл на месте — возможно, программа ещё установлена.",
            });
        }

        // ---------- Планировщик ----------
        progress?.Invoke("Планировщик заданий…");
        string tasksDir = Path.Combine(Sys.WinDir, @"System32\Tasks");
        try
        {
            foreach (var f in Directory.EnumerateFiles(tasksDir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                string rel = f.Substring(tasksDir.Length);
                if (rel.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                var t = MatchTerm(rel, terms);
                if (t == null)
                {
                    try { if (new FileInfo(f).Length < 64 * 1024) t = MatchTerm(File.ReadAllText(f), terms); } catch { }
                    if (t != null && !t.Strong) t = null;
                }
                if (t == null) continue;
                items.Add(new LeftoverItem { Kind = LeftKind.Task, TaskName = rel, Display = rel, Checked = t.Strong, Risk = Risk.Safe, Note = "Задание планировщика (обновления, автозапуск)." });
            }
        }
        catch { }

        return items;
    }

    static void AddReg(List<LeftoverItem> items, RegistryHive h, string path, SearchTerm t)
    {
        var rr = new RegRef(h, path);
        if (items.Any(i => i.Regs.Any(r => r.ValueName == null && r.KeyPath.Equals(rr.KeyPath, StringComparison.OrdinalIgnoreCase)))) return;
        items.Add(new LeftoverItem
        {
            Kind = LeftKind.RegKey, Display = rr.KeyPath, Regs = { rr }, Checked = t.Strong, Risk = Risk.Caution,
            Note = t.Strong ? "Настройки программы в реестре." : "Совпадение по издателю — может относиться к другим его программам.",
        });
    }

    static readonly HashSet<string> NeverTouchFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "Microsoft", "Common Files", "WindowsApps",
        "Packages", "Temp", "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "PerfLogs",
    };

    static void ScanFolder(string dir, int depth, bool userData, List<SearchTerm> terms, List<LeftoverItem> items, HashSet<string> seen, CancellationToken ct)
    {
        if (depth <= 0 || !Directory.Exists(dir) || ct.IsCancellationRequested) return;
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
        catch { return; }
        foreach (var e in entries)
        {
            if (NeverTouchFolders.Contains(e.Name)) continue;
            bool isDir = e is DirectoryInfo;
            bool isLnk = !isDir && e.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            var t = MatchTerm(e.Name, terms);
            if (t != null && (isDir || isLnk || !userData))
            {
                if (!seen.Add(e.FullName)) continue;
                var cls = Classifier.ForPath(e.FullName);
                long size = isDir ? FileOps.Measure(e.FullName, 0, ct).size : ((FileInfo)e).Length;
                var it = new LeftoverItem
                {
                    Kind = isDir ? LeftKind.Folder : isLnk ? LeftKind.Shortcut : LeftKind.File,
                    Path = e.FullName, Display = e.FullName, Size = size,
                    Checked = t.Strong && !userData && cls.Risk != Risk.Critical,
                    Risk = userData ? Risk.User : (cls.Risk == Risk.Critical ? Risk.Critical : (isLnk ? Risk.Safe : Risk.Caution)),
                    Note = userData ? "Папка/файл с вашими данными — проверьте вручную." : (!t.Strong ? "Совпадение по издателю — может относиться к другим его программам." : ""),
                };
                if (isLnk)
                {
                    bool broken = IsBrokenLnk(e.FullName);
                    it.Checked = t.Strong || broken; it.Risk = Risk.Safe;
                    it.Note = broken ? "Битый ярлык: файл, на который он ведёт, не существует." : "Ярлык.";
                }
                bool startMenu = e.FullName.Contains(@"\Start Menu\", StringComparison.OrdinalIgnoreCase);
                if (isDir && startMenu)
                {
                    var lnks = Directory.EnumerateFiles(e.FullName, "*.lnk", SearchOption.AllDirectories).ToList();
                    bool allBroken = lnks.All(IsBrokenLnk);
                    it.Risk = Risk.Safe;
                    it.Checked = t.Strong || allBroken;
                    it.Note = lnks.Count == 0 ? "Пустая папка меню «Пуск» — остаток." :
                        allBroken ? $"Папка меню «Пуск»: все ярлыки ({lnks.Count}) битые." : $"Папка меню «Пуск» ({lnks.Count} ярлыков).";
                }
                else if (isDir)
                {
                    string warn = UserDataWarning(e.FullName);
                    if (warn != null) { it.Checked = false; it.Risk = Risk.User; it.Note = warn; }
                }
                if (cls.Risk == Risk.Critical) it.Note = "Системная папка — не удалять. " + it.Note;
                items.Add(it);
                continue;
            }
            if (isDir && depth > 1 && (e.Attributes & FileAttributes.ReparsePoint) == 0)
                ScanFolder(e.FullName, depth - 1, userData, terms, items, seen, ct);
        }
    }

    /// <summary>Проверка: нет ли внутри пользовательских данных (например, баз проектов DaVinci Resolve).</summary>
    static string UserDataWarning(string dir)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
            foreach (var d in Directory.EnumerateDirectories(dir, "*", opts))
            {
                string n = Path.GetFileName(d);
                if (n.Equals("Resolve Disk Database", StringComparison.OrdinalIgnoreCase) || n.Equals("Resolve Project Library", StringComparison.OrdinalIgnoreCase) || n.Equals("Resolve Projects", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasProjects = Directory.EnumerateFiles(d, "*.db", opts).Any() || Directory.EnumerateFiles(d, "*.drp", opts).Any() || Directory.EnumerateFiles(d, "Project.db", opts).Any();
                    if (hasProjects) return "⚠ Внутри базы проектов DaVinci Resolve (" + d + "). Скопируйте их, если проекты нужны, затем отметьте вручную.";
                }
            }
            if (Directory.EnumerateFiles(dir, "*.drp", opts).Any()) return "⚠ Внутри есть проекты DaVinci Resolve (*.drp). Сохраните их перед удалением.";
            if (Directory.EnumerateFiles(dir, "*.sav", opts).Take(1).Any() || Directory.EnumerateDirectories(dir, "Saves", opts).Any() || Directory.EnumerateDirectories(dir, "SaveGames", opts).Any())
                return "⚠ Похоже на сохранения игры. Проверьте перед удалением.";
        }
        catch { }
        return null;
    }

    // ---------- Удаление ----------

    public static DeleteResult Remove(List<LeftoverItem> items, bool toRecycleBin, Action<string> progress, out string backupFile)
    {
        var total = new DeleteResult();
        string bdir = AppDir.NewBackupDir();
        backupFile = Path.Combine(bdir, "registry_backup.reg");
        // 1) резервная копия реестра
        using (var bk = new RegBackup(backupFile))
        {
            foreach (var it in items)
                foreach (var r in it.Regs) bk.Add(r);
            foreach (var it in items.Where(i => i.Kind == LeftKind.Service))
                bk.Add(new RegRef(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{it.ServiceName}"));
        }
        Log.Write($"Резервная копия реестра: {backupFile}");
        // кэшированные .msi копируем в резервную папку
        foreach (var it in items.Where(i => i.Kind == LeftKind.Msi))
            foreach (var f in it.Files)
                try { File.Copy(f, Path.Combine(bdir, Path.GetFileName(f)), true); } catch { }

        // 2) службы и задания — первыми (могут держать файлы)
        foreach (var it in items.Where(i => i.Kind == LeftKind.Service))
        {
            progress?.Invoke("Служба " + it.ServiceName);
            Sys.Run("sc.exe", $"stop \"{it.ServiceName}\"", 60_000);
            var (code, outp) = Sys.Run("sc.exe", $"delete \"{it.ServiceName}\"", 60_000);
            Log.Write($"Служба {it.ServiceName}: удаление → {code} {outp}");
            if (code != 0) total.Failed++;
        }
        foreach (var it in items.Where(i => i.Kind == LeftKind.Task))
        {
            progress?.Invoke("Задание " + it.TaskName);
            var (code, outp) = Sys.Run("schtasks.exe", $"/Delete /TN \"{it.TaskName}\" /F", 60_000);
            Log.Write($"Задание {it.TaskName}: {code} {outp}");
            if (code != 0) total.Failed++;
        }

        // 3) реестр
        foreach (var it in items)
        {
            if (it.Regs.Count == 0) continue;
            progress?.Invoke("Реестр: " + it.Display);
            int ok = 0, fail = 0;
            foreach (var r in it.Regs)
            {
                if (Reg.Delete(r, out var err)) ok++;
                else { fail++; if (total.Errors.Count < 200) total.Errors.Add($"{r}: {err}"); }
            }
            total.Failed += fail;
            Log.Write($"Реестр [{it.KindName}] {it.Display}: удалено {ok}, ошибок {fail}");
        }

        // 4) файлы и папки
        foreach (var it in items)
        {
            var paths = new List<string>(it.Files);
            if (it.Path != null) paths.Add(it.Path);
            foreach (var p in paths)
            {
                if (!File.Exists(p) && !Directory.Exists(p)) continue;
                progress?.Invoke("Удаление: " + p);
                var r = FileOps.DeleteItem(p, toRecycleBin && it.Kind != LeftKind.Msi, true, true);
                total.Merge(r);
                Log.Write($"Удалено: {p} — {Fmt.Size(r.Freed)}" + (r.ScheduledOnReboot > 0 ? $", {r.ScheduledOnReboot} объектов будет удалено после перезагрузки" : "") + (r.Failed > 0 ? $", ошибок {r.Failed}" : ""));
            }
        }
        return total;
    }

    // ---------- Поиск «осиротевших» папок ----------

    static readonly HashSet<string> OrphanWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "Temp", "Common Files", "Package Cache", "Microsoft.NET", "Internet Explorer", "WindowsApps",
        "ModifiableWindowsApps", "Uninstall Information", "Application Data", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "PeerDistRepub",
        "Programs", "VirtualStore", "History", "CrashDumps", "Publishers", "regid.1991-06.com.microsoft", "ssh", "USOPrivate", "USOShared",
        "SoftwareDistribution", "PlaceholderTileLogoFolder", "IsolatedStorage", "Downloaded Installations", "Desktop", "Documents",
        "Start Menu", "Templates", "Favorites", "Microsoft Help", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security",
        "Windows Sidebar", "WindowsPowerShell", "Reference Assemblies", "MSBuild", "dotnet", "Microsoft Update Health Tools", "Mozilla Maintenance Service",
        "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "Realtek", "Package", "Installer", "Sun", "Oracle", "Java", "Google", "Adobe", "Apple",
        "Microsoft Office", "Microsoft OneDrive", "OneDrive", "Steam", "Epic", "fontconfig", "pip", "npm-cache", "npm", "Yarn", "NuGet", ".NET",
        "IconCache.db", "Low", "Mozilla", "Sandbox", "SquirrelTemp", "CEF", "Chromium", "assembly", "GameDVR", "Diagnostics", "Ssh", "WinGet",
        "IIS", "IIS Express", "RUXIM", "Windows Kits", "Microsoft SDKs", "PCHealthCheck", "Online Services", "Microsoft Analysis Services",
        "Uninstall", "Programs", "ProgramData", "regid.1991-06.com.microsoft", "WindowsHolographicDevices", "Speech", "Application Verifier",
        "PackageManagement", "SystemTemp",
    };

    /// <summary>Куда ведёт ярлык (.lnk). null — не удалось определить.</summary>
    public static string LnkTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic sh = Activator.CreateInstance(t);
            try
            {
                dynamic sc = sh.CreateShortcut(lnk);
                string target = sc.TargetPath;
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
                return target;
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh); }
        }
        catch { return null; }
    }

    /// <summary>Ярлык битый: цель — локальный файл на существующем диске, но его нет.</summary>
    public static bool IsBrokenLnk(string lnk)
    {
        string t = LnkTarget(lnk);
        if (string.IsNullOrWhiteSpace(t) || t.StartsWith(@"\\") || !Path.IsPathRooted(t)) return false;
        string root = Path.GetPathRoot(t);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false; // флешка/сетевой диск отключены
        if (t.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false;
        return !File.Exists(t) && !Directory.Exists(t);
    }

    public static IEnumerable<string> BrokenShortcuts()
    {
        var dirs = new List<(string, bool)>
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
        };
        foreach (var p in JunkCleaner.Profiles())
        {
            dirs.Add((Path.Combine(p, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"), true));
            dirs.Add((Path.Combine(p, "Desktop"), false));
        }
        var res = new List<string>();
        foreach (var (d, rec) in dirs)
        {
            if (!Directory.Exists(d)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*.lnk", new EnumerationOptions { RecurseSubdirectories = rec, IgnoreInaccessible = true }))
                    if (IsBrokenLnk(f)) res.Add(f);
            }
            catch { }
        }
        return res;
    }

    public static List<LeftoverItem> FindOrphans(List<InstalledProgram> programs, Action<string> progress, CancellationToken ct)
    {
        var known = new List<string>();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddKnown(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = Environment.ExpandEnvironmentVariables(s);
            known.Add(Norm(s));
            foreach (var w in s.Split(new[] { ' ', '\\', '/', '.', '-', '_', '(', ')', ',', '"', ':' }, StringSplitOptions.RemoveEmptyEntries))
                if (w.Length >= 4 && !TooGeneric.Contains(w)) tokens.Add(Norm(w));
        }
        foreach (var p in programs)
        {
            AddKnown(p.Name); AddKnown(p.Publisher); AddKnown(p.InstallLocation); AddKnown(p.DisplayIcon); AddKnown(p.UninstallString);
        }
        // службы и автозапуск тоже «владеют» папками (портативные программы, драйверы)
        foreach (var svc in Reg.SubKeys(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services"))
        {
            using var k = Reg.Open(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{svc}");
            AddKnown(Reg.Str(k, "ImagePath"));
        }
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
        {
            using var k = Reg.Open(RegistryHive.LocalMachine, path);
            if (k != null) foreach (var v in k.GetValueNames()) AddKnown(Reg.Str(k, v));
        }
        foreach (var sid in Reg.UserSids())
        {
            using var k = Reg.Open(RegistryHive.Users, $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Run");
            if (k != null) foreach (var v in k.GetValueNames()) AddKnown(Reg.Str(k, v));
            // разделы HKCU\Software — программа, которая всё ещё пишет настройки, вероятно, используется
        }

        var candidates = new List<string>();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var root in new[] { pf, pf86, pd }.Concat(JunkCleaner.Profiles().SelectMany(p => new[] { Path.Combine(p, @"AppData\Local"), Path.Combine(p, @"AppData\Roaming"), Path.Combine(p, @"AppData\LocalLow") })))
        {
            try { candidates.AddRange(Directory.EnumerateDirectories(root)); } catch { }
        }

        var result = new List<LeftoverItem>();
        DateTime recent = DateTime.Now.AddDays(-30);
        foreach (var dir in candidates)
        {
            if (ct.IsCancellationRequested) break;
            string name = Path.GetFileName(dir);
            if (OrphanWhitelist.Contains(name) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(".") || name.StartsWith("{")) continue;
            try { if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
            string n = Norm(name);
            if (n.Length < 3) continue;
            if (known.Any(k => k.Contains(n))) continue;
            var words = name.Split(new[] { ' ', '.', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4 && !TooGeneric.Contains(w)).Select(Norm);
            if (words.Any(w => tokens.Contains(w))) continue;
            progress?.Invoke("Проверка: " + dir);
            var (size, files, lastWrite) = MeasureWithDate(dir, ct);
            if (size < 1024 * 1024) continue;
            if (lastWrite > recent) continue; // папкой пользуются
            var cls = Classifier.ForPath(dir);
            if (cls.Risk == Risk.Critical) continue;
            result.Add(new LeftoverItem
            {
                Kind = LeftKind.Folder, Path = dir, Display = dir, Size = size, Checked = false, Risk = Risk.Caution,
                Note = $"Возможный остаток: нет установленной программы с похожим названием. Последнее изменение: {lastWrite:dd.MM.yyyy}. " +
                       (dir.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ? "Часто это сохранения/настройки игр (Steam, Epic) или портативных программ — удаляйте, только если они не нужны." : "Проверьте вручную (может быть портативной программой)."),
            });
        }
        return result.OrderByDescending(r => r.Size).ToList();
    }

    static (long size, long files, DateTime last) MeasureWithDate(string path, CancellationToken ct)
    {
        long size = 0, files = 0; DateTime last = DateTime.MinValue;
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (ct.IsCancellationRequested) break;
                size += f.Length; files++;
                if (f.LastWriteTime > last) last = f.LastWriteTime;
            }
        }
        catch { }
        if (last == DateTime.MinValue) try { last = Directory.GetLastWriteTime(path); } catch { }
        return (size, files, last);
    }
}
