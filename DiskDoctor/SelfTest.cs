using System.Diagnostics;
using System.Text;
using DiskDoctor.Core;

namespace DiskDoctor;

/// <summary>Режим проверки без интерфейса и без удаления: DiskDoctor.exe --selftest report.txt [путь_для_сканирования]</summary>
static class SelfTest
{
    /// <summary>Проверка резервного копирования/удаления/восстановления реестра на тестовом ключе HKCU.</summary>
    static void RegTest(Action<string> W)
    {
        const string key = @"Software\DiskDoctorSelfTest";
        var H = Microsoft.Win32.RegistryHive.CurrentUser;
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key))
        {
            k.SetValue("str", "C:\\Path \"quoted\"");
            k.SetValue("dw", unchecked((int)0xDEADBEEF), Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("qw", 0x1122334455667788L, Microsoft.Win32.RegistryValueKind.QWord);
            k.SetValue("exp", "%SystemRoot%\\x", Microsoft.Win32.RegistryValueKind.ExpandString);
            k.SetValue("multi", new[] { "a", "бв" }, Microsoft.Win32.RegistryValueKind.MultiString);
            k.SetValue("bin", new byte[] { 1, 2, 255 }, Microsoft.Win32.RegistryValueKind.Binary);
            k.SetValue("", "default");
            using var s = k.CreateSubKey("Sub\\Deep"); s.SetValue("v", "1");
            using var c = k.CreateSubKey("Comp"); c.SetValue("187DDEBC87A62C244BC5AF58C4255584", "C:\\x.dll");
        }
        var refs = new List<RegRef> { new(H, key + "\\Comp", "187DDEBC87A62C244BC5AF58C4255584", true), new(H, key) };
        string file = Path.Combine(Path.GetTempPath(), "dd_regtest.reg");
        using (var b = new RegBackup(file)) foreach (var r in refs) b.Add(r);
        foreach (var r in refs) W($"delete {r}: {Reg.Delete(r, out var e)} {e}");
        W("exists after delete: " + new RegRef(H, key).Exists());
        var (code, outp) = Sys.Run("reg.exe", $"import \"{file}\"");
        W($"reg import: {code} {outp}");
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
        {
            W($"restored: str={k?.GetValue("str")} dw={(uint)(int)(k?.GetValue("dw") ?? 0):X} qw={k?.GetValue("qw"):X} exp={k?.GetValue("exp", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)} " +
              $"multi={string.Join("|", (string[])k?.GetValue("multi") ?? Array.Empty<string>())} bin={BitConverter.ToString((byte[])k?.GetValue("bin") ?? Array.Empty<byte>())} def={k?.GetValue("")} " +
              $"deep={k?.OpenSubKey("Sub\\Deep")?.GetValue("v")} comp={k?.OpenSubKey("Comp")?.GetValue("187DDEBC87A62C244BC5AF58C4255584")}");
        }
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false);
    }

    public static int Run(string outFile, string[] rest)
    {
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        var sw = Stopwatch.StartNew();
        try
        {
            W("== Тест реестра (HKCU) ==");
            try { RegTest(W); } catch (Exception ex) { W("RegTest error: " + ex); }
            W("== Тест удаления файлов ==");
            try
            {
                string d = Path.Combine(Path.GetTempPath(), "dd_filetest");
                Directory.CreateDirectory(Path.Combine(d, "sub"));
                File.WriteAllText(Path.Combine(d, "new.txt"), "new");
                File.WriteAllText(Path.Combine(d, "sub", "old.txt"), new string('x', 1000));
                File.SetLastWriteTime(Path.Combine(d, "sub", "old.txt"), DateTime.Now.AddDays(-5));
                File.SetAttributes(Path.Combine(d, "sub", "old.txt"), FileAttributes.ReadOnly);
                var r1 = FileOps.DeleteTree(d, false, 24);
                W($"minAge: freed={r1.Freed} files={r1.FilesDeleted} failed={r1.Failed}; new exists={File.Exists(Path.Combine(d, "new.txt"))}, old exists={File.Exists(Path.Combine(d, "sub", "old.txt"))}, sub exists={Directory.Exists(Path.Combine(d, "sub"))}");
                var r2 = FileOps.DeleteItem(d, true, false, false);
                W($"recycle: freed={r2.Freed} gone={!Directory.Exists(d)}");
            }
            catch (Exception ex) { W("FileTest error: " + ex); }
            W($"Администратор: {Sys.IsAdmin}");
            W($"PackGuid: {Reg.PackGuid("{CBEDD781-6A78-42C2-B45C-FA854C525548}")} / Unpack: {Reg.UnpackGuid(Reg.PackGuid("{CBEDD781-6A78-42C2-B45C-FA854C525548}"))}");
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                W($"Диск {d.Name} {d.VolumeLabel} {Fmt.Size(d.TotalSize - d.TotalFreeSpace)} / {Fmt.Size(d.TotalSize)}");

            W("\n== Классификатор ==");
            foreach (var p in new[] { @"C:\Windows\WinSxS", @"C:\Windows\Temp\x.tmp", @"C:\Users\A\AppData\Local\Temp", @"C:\Users\A\AppData\Local\Google\Chrome\User Data\Default\Cache",
                         @"C:\Windows\System32\node_modules", @"D:\proj\node_modules", @"C:\hiberfil.sys", @"C:\Program Files\Foo", @"C:\Program Files\Common Files\X", @"C:\Users\A\Downloads\setup.exe", @"C:\Windows\Installer\abc.msi" })
            {
                var m = Classifier.ForPath(p);
                W($"{p} → {m.Risk} {m.Title}{(m.Inherited ? " (унасл.)" : "")}");
            }

            string scanPath = rest.Length > 0 ? rest[0] : null;
            if (scanPath != null)
            {
                W($"\n== Сканирование {scanPath} ==");
                var sc = new DiskScanner();
                var t0 = sw.Elapsed;
                var root = sc.Scan(scanPath, CancellationToken.None);
                W($"Время: {(sw.Elapsed - t0).TotalSeconds:0.0} c, файлов {sc.FilesScanned:N0}, папок {sc.DirsScanned:N0}, без доступа {sc.DeniedDirs}, всего {Fmt.Size(root.Size)}");
                foreach (var c in root.Children?.Take(25) ?? Enumerable.Empty<DirNode>())
                {
                    var m = Classifier.ForNode(c);
                    W($"  {Fmt.Size(c.Size),12}  {c.Name,-40} {m.Risk,-9} {m.Title}");
                }
                W("Топ файлов:");
                foreach (var f in sc.TopFiles().Take(15)) W($"  {Fmt.Size(f.Size),12}  {f.Path}  [{Classifier.ForPath(f.Path).Risk}]");
                W("Типы:");
                foreach (var e in sc.Extensions().Take(10)) W($"  {Fmt.Size(e.Size),12}  {e.Ext} ({FileGroups.Of(e.Ext)}) x{e.Count}");
            }

            W("\n== Мусор (только анализ) ==");
            foreach (var c in JunkCleaner.Build())
            {
                JunkCleaner.Analyze(c, CancellationToken.None);
                W($"  {Fmt.Size(c.Size),12}  {c.Count,8:N0} ф.  {c.Name}  [{c.Found.Count} мест]");
            }

            W("\n== Программы ==");
            var t1 = sw.Elapsed;
            var progs = ProgramList.Load();
            W($"Всего {progs.Count} (скрытых {progs.Count(p => p.Hidden)}), проблемных {progs.Count(p => p.Broken)}, время {(sw.Elapsed - t1).TotalSeconds:0.0} c");
            foreach (var p in progs.Where(p => p.Broken))
                W($"  ! {p.Name} {p.Version} [{p.Scope}{(p.Hidden ? ", скрыт" : "")}]: {string.Join("; ", p.Problems)}");

            W("\n== Хвосты: DaVinci Resolve / Blackmagic Design ==");
            var dv = progs.FirstOrDefault(p => p.Name.Contains("DaVinci", StringComparison.OrdinalIgnoreCase));
            var terms = dv != null ? LeftoverFinder.TermsFor(dv) : new List<SearchTerm> { new("DaVinci Resolve", true), new("Blackmagic Design", false) };
            W("Слова: " + string.Join(", ", terms.Select(t => t.Text + (t.Strong ? "" : " (слабое)"))));
            var t2 = sw.Elapsed;
            var items = LeftoverFinder.Search(terms, dv?.ProductCode != null ? new[] { dv.ProductCode } : Array.Empty<string>(), null, CancellationToken.None);
            W($"Найдено {items.Count}, время {(sw.Elapsed - t2).TotalSeconds:0.0} c");
            foreach (var i in items)
            {
                W($"  [{(i.Checked ? "x" : " ")}] {i.KindName}: {i.Display} {(i.Size > 0 ? Fmt.Size(i.Size) : "")} — {i.Note}");
                if (i.Kind == LeftKind.Msi) foreach (var r in i.Regs.Where(r => r.ValueName == null || !r.SubKey.Contains("Components"))) W($"        {r}");
            }

            W("\n== Возможные осиротевшие папки ==");
            var t3 = sw.Elapsed;
            var orphans = LeftoverFinder.FindOrphans(progs, null, CancellationToken.None);
            W($"Найдено {orphans.Count}, время {(sw.Elapsed - t3).TotalSeconds:0.0} c");
            foreach (var o in orphans.Take(40)) W($"  {Fmt.Size(o.Size),12}  {o.Path}");

            W("\n== Осиротевшие файлы Windows\\Installer ==");
            var oi = MsiRegistry.OrphanedInstallerFiles().ToList();
            W($"{oi.Count} файлов, {Fmt.Size(oi.Sum(f => new FileInfo(f).Length))}");
        }
        catch (Exception ex) { W("ИСКЛЮЧЕНИЕ: " + ex); }
        W($"\nОбщее время: {sw.Elapsed.TotalSeconds:0.0} c");
        File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        return 0;
    }
}
