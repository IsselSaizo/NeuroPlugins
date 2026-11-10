using Microsoft.Win32;

namespace DiskDoctor.Core;

public sealed class JunkCategory
{
    public string Name;
    public string Description;
    public Risk Risk = Risk.Safe;
    public bool DefaultChecked = true;
    public int MinAgeHours;                    // не трогать файлы новее N часов
    public Func<IEnumerable<string>> Targets;  // папки (чистится содержимое) или файлы
    public bool DeleteTargetsThemselves;       // удалять саму папку, а не только содержимое
    public string[] StopServices;              // остановить службы на время очистки
    public bool NeedsOwnership;                // папки под TrustedInstaller
    public Func<(long size, long count)> CustomMeasure;
    public Func<DeleteResult> CustomClean;

    // результаты анализа
    public long Size;
    public long Count;
    public List<string> Found = new();
}

public static class JunkCleaner
{
    static string Win => Sys.WinDir;
    static string SysDrive => Sys.SystemDrive;
    static string PD => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>Папки профилей всех пользователей (C:\Users\*).</summary>
    public static IEnumerable<string> Profiles()
    {
        var list = new List<string>();
        try
        {
            using var k = Reg.Open(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (k != null)
                foreach (var sid in k.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-")))
                {
                    using var sk = k.OpenSubKey(sid);
                    var p = Environment.ExpandEnvironmentVariables(Reg.Str(sk, "ProfileImagePath") ?? "");
                    if (Directory.Exists(p)) list.Add(p);
                }
        }
        catch { }
        string own = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(own) && !list.Contains(own, StringComparer.OrdinalIgnoreCase)) list.Add(own);
        return list;
    }

    /// <summary>Раскрыть шаблон пути с "*" в сегментах. Возвращает существующие пути.</summary>
    public static IEnumerable<string> Glob(string pattern)
    {
        var parts = pattern.Split('\\');
        IEnumerable<string> cur = new[] { parts[0] + "\\" };
        for (int i = 1; i < parts.Length; i++)
        {
            string seg = parts[i];
            if (seg.Length == 0) continue;
            bool last = i == parts.Length - 1;
            var next = new List<string>();
            foreach (var c in cur)
            {
                if (seg.Contains('*') || seg.Contains('?'))
                {
                    try
                    {
                        next.AddRange(Directory.EnumerateDirectories(c, seg));
                        if (last) next.AddRange(Directory.EnumerateFiles(c, seg));
                    }
                    catch { }
                }
                else
                {
                    string p = Path.Combine(c, seg);
                    if (Directory.Exists(p) || (last && File.Exists(p))) next.Add(p);
                }
            }
            cur = next;
        }
        return cur.ToList();
    }

    static IEnumerable<string> InProfiles(params string[] rels) =>
        Profiles().SelectMany(p => rels.SelectMany(r => Glob(Path.Combine(p, r))));

    static readonly string[] ChromiumRoots =
    {
        @"AppData\Local\Google\Chrome\User Data\*",
        @"AppData\Local\Microsoft\Edge\User Data\*",
        @"AppData\Local\Yandex\YandexBrowser\User Data\*",
        @"AppData\Local\BraveSoftware\Brave-Browser\User Data\*",
        @"AppData\Local\Vivaldi\User Data\*",
        @"AppData\Local\Chromium\User Data\*",
        @"AppData\Local\Opera Software\Opera Stable",
        @"AppData\Local\Opera Software\Opera GX Stable",
        @"AppData\Roaming\Opera Software\Opera Stable",
        @"AppData\Roaming\Opera Software\Opera GX Stable",
    };

    public static List<JunkCategory> Build()
    {
        var list = new List<JunkCategory>
        {
            new()
            {
                Name = "Временные файлы пользователей",
                Description = "Папки %TEMP% всех профилей. Файлы моложе 24 часов не трогаются (их может использовать идущая установка).",
                MinAgeHours = 24,
                Targets = () => InProfiles(@"AppData\Local\Temp"),
            },
            new()
            {
                Name = "Временные файлы Windows",
                Description = @"Windows\Temp. Файлы моложе 24 часов не трогаются.",
                MinAgeHours = 24,
                Targets = () => new[] { Path.Combine(Win, "Temp") },
            },
            new()
            {
                Name = "Кэш обновлений Windows",
                Description = "Скачанные пакеты обновлений (SoftwareDistribution\\Download). Служба обновлений будет временно остановлена.",
                Targets = () => new[] { Path.Combine(Win, @"SoftwareDistribution\Download") },
                StopServices = new[] { "wuauserv", "bits" },
            },
            new()
            {
                Name = "Кэш оптимизации доставки",
                Description = "Копии обновлений для раздачи другим компьютерам.",
                Targets = () => new[] { Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
                StopServices = new[] { "DoSvc" },
            },
            new()
            {
                Name = "Отчёты об ошибках Windows",
                Description = "Архив и очередь отчётов WER (системные и пользовательские).",
                Targets = () => new[] { Path.Combine(PD, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(PD, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(PD, @"Microsoft\Windows\WER\Temp") }
                    .Concat(InProfiles(@"AppData\Local\Microsoft\Windows\WER")).Where(Directory.Exists),
            },
            new()
            {
                Name = "Дампы памяти и отчёты сбоев",
                Description = "MEMORY.DMP, Minidump, LiveKernelReports, CrashDumps программ. Нужны только для диагностики.",
                Targets = () => new[] { Path.Combine(Win, "MEMORY.DMP"), Path.Combine(Win, "Minidump"), Path.Combine(Win, "LiveKernelReports") }
                    .Concat(InProfiles(@"AppData\Local\CrashDumps")).Where(p => File.Exists(p) || Directory.Exists(p)),
            },
            new()
            {
                Name = "Кэш эскизов и значков",
                Description = "thumbcache_*.db / iconcache_*.db — пересоздаются Проводником. Занятые файлы будут пропущены.",
                Targets = () => InProfiles(@"AppData\Local\Microsoft\Windows\Explorer\thumbcache_*.db", @"AppData\Local\Microsoft\Windows\Explorer\iconcache_*.db"),
            },
            new()
            {
                Name = "Кэш браузеров",
                Description = "Chrome, Edge, Яндекс, Opera, Brave, Vivaldi, Firefox: Cache, Code Cache, GPUCache. Пароли, история и вкладки НЕ затрагиваются. Лучше закрыть браузеры.",
                Targets = () => Profiles().SelectMany(p =>
                    ChromiumRoots.SelectMany(r => new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" }
                        .SelectMany(c => Glob(Path.Combine(p, r, c))))
                    .Concat(Glob(Path.Combine(p, @"AppData\Local\Mozilla\Firefox\Profiles\*\cache2")))),
            },
            new()
            {
                Name = "Кэш приложений (Discord, VS Code, Teams, Steam…)",
                Description = "Кэши Electron-приложений и веб-кэш Steam. Восстанавливаются автоматически.",
                Targets = () => Profiles().SelectMany(p =>
                    new[] { @"AppData\Roaming\discord", @"AppData\Roaming\Code", @"AppData\Roaming\Microsoft\Teams", @"AppData\Roaming\Slack", @"AppData\Roaming\Spotify", @"AppData\Local\Spotify" }
                        .SelectMany(app => new[] { "Cache", "Code Cache", "GPUCache", "CachedData", "CachedExtensionVSIXs" }
                            .SelectMany(c => Glob(Path.Combine(p, app, c))))
                    .Concat(Glob(Path.Combine(p, @"AppData\Local\Steam\htmlcache")))),
            },
            new()
            {
                Name = "Кэш шейдеров видеокарты",
                Description = "DirectX/NVIDIA/AMD/Intel. Пересоздаётся при запуске игр (первый запуск может немного подтормаживать).",
                Targets = () => InProfiles(@"AppData\Local\D3DSCache", @"AppData\Local\NVIDIA\DXCache", @"AppData\Local\NVIDIA\GLCache",
                        @"AppData\Local\AMD\DxCache", @"AppData\Local\AMD\DxcCache", @"AppData\Local\AMD\GLCache", @"AppData\Local\AMD\VkCache",
                        @"AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache", @"AppData\LocalLow\Intel\ShaderCache")
                    .Concat(new[] { Path.Combine(PD, @"NVIDIA Corporation\NV_Cache") }.Where(Directory.Exists)),
            },
            new()
            {
                Name = "Остатки установки драйверов",
                Description = @"C:\NVIDIA, C:\AMD (распакованные установщики), скачанные NVIDIA драйверы.",
                DeleteTargetsThemselves = true,
                Targets = () => new[] { Path.Combine(SysDrive, "NVIDIA"), Path.Combine(SysDrive, "AMD"), Path.Combine(PD, @"NVIDIA Corporation\Downloader") }.Where(Directory.Exists),
            },
            new()
            {
                Name = "Журналы Windows",
                Description = @"Старые логи CBS/DISM/WindowsUpdate и файлы .etl из Windows\Logs (старше 3 дней).",
                MinAgeHours = 72,
                Targets = () => new[] { Path.Combine(Win, @"Logs\CBS"), Path.Combine(Win, @"Logs\DISM"), Path.Combine(Win, @"Logs\WindowsUpdate"), Path.Combine(Win, @"Logs\MeasuredBoot"), Path.Combine(Win, "Panther") }.Where(Directory.Exists),
            },
            new()
            {
                Name = "Корзина (все диски)",
                Description = "Окончательно удаляет то, что лежит в Корзине.",
                CustomMeasure = () =>
                {
                    long s = 0, c = 0;
                    foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                    {
                        var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
                        if (Native.SHQueryRecycleBin(d.RootDirectory.FullName, ref info) == 0) { s += info.i64Size; c += info.i64NumItems; }
                    }
                    return (s, c);
                },
                CustomClean = () =>
                {
                    var r = new DeleteResult();
                    var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
                    Native.SHQueryRecycleBin(null, ref info);
                    int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
                    if (hr == 0 || info.i64NumItems == 0) { r.Freed = info.i64Size; r.FilesDeleted = info.i64NumItems; }
                    else { r.Failed = 1; r.Errors.Add($"SHEmptyRecycleBin: 0x{hr:X8}"); }
                    return r;
                },
            },
            new()
            {
                Name = "Кэш разработчика (pip, npm, yarn, NuGet, Go, Gradle)",
                Description = "Скачанные пакеты. Перескачаются при следующей сборке/установке.",
                DefaultChecked = false,
                Targets = () => InProfiles(@"AppData\Local\pip\cache", @"AppData\Local\npm-cache", @"AppData\Local\Yarn\Cache",
                    @"AppData\Local\NuGet\v3-cache", @"AppData\Local\NuGet\Cache", @"AppData\Local\NuGet\plugins-cache", @"AppData\Local\go-build", @".gradle\caches",
                    @"AppData\Local\pnpm-cache", @"AppData\Local\uv\cache"),
            },
            new()
            {
                Name = "Кэш Telegram",
                Description = "Медиа из чатов (tdata\\user_data). Перескачается при просмотре. Закройте Telegram.",
                DefaultChecked = false,
                Targets = () => InProfiles(@"AppData\Roaming\Telegram Desktop\tdata\user_data", @"AppData\Roaming\Telegram Desktop\tdata\emoji"),
            },
            new()
            {
                Name = "Битые ярлыки (меню «Пуск», Рабочий стол)",
                Description = "Ярлыки, ведущие на несуществующие файлы (остатки удалённых программ). Ярлыки на отключённые диски не трогаются.",
                Targets = () => LeftoverFinder.BrokenShortcuts(),
            },
            new()
            {
                Name = "Prefetch",
                Description = "Данные ускорения запуска программ. Пересоздаются; первые запуски будут чуть медленнее.",
                DefaultChecked = false,
                Targets = () => new[] { Path.Combine(Win, "Prefetch") },
            },
            new()
            {
                Name = "Осиротевшие файлы установщиков (Windows\\Installer)",
                Description = ".msi/.msp, на которые не ссылается ни одна установленная программа или обновление (аналог PatchCleaner). Используемые файлы не трогаются.",
                Risk = Risk.Caution,
                DefaultChecked = false,
                Targets = () => MsiRegistry.OrphanedInstallerFiles(),
            },
            new()
            {
                Name = "Кэш патчей MSI ($PatchCache$)",
                Description = "Нужен только для отката обновлений программ (Office и др.).",
                Risk = Risk.Caution,
                DefaultChecked = false,
                NeedsOwnership = false,
                Targets = () => new[] { Path.Combine(Win, @"Installer\$PatchCache$") }.Where(Directory.Exists),
            },
            new()
            {
                Name = "Предыдущая установка Windows и остатки обновлений",
                Description = "Windows.old, $Windows.~BT, $Windows.~WS, $GetCurrent, ESD. После удаления откат к прошлой версии Windows будет невозможен.",
                Risk = Risk.Caution,
                DefaultChecked = false,
                DeleteTargetsThemselves = true,
                NeedsOwnership = true,
                Targets = () => new[] { "Windows.old", "$Windows.~BT", "$Windows.~WS", "$WINDOWS.~Q", "$GetCurrent", "$SysReset", "ESD" }
                    .Select(n => Path.Combine(SysDrive, n)).Where(Directory.Exists),
            },
        };
        return list;
    }

    public static void Analyze(JunkCategory c, CancellationToken ct)
    {
        c.Size = 0; c.Count = 0; c.Found.Clear();
        try
        {
            if (c.CustomMeasure != null) { (c.Size, c.Count) = c.CustomMeasure(); return; }
            foreach (var t in c.Targets().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (ct.IsCancellationRequested) return;
                var (s, n) = FileOps.Measure(t, c.MinAgeHours, ct);
                if (s == 0 && n == 0) continue;
                c.Found.Add(t);
                c.Size += s; c.Count += n;
            }
        }
        catch (Exception ex) { Log.Write($"Анализ «{c.Name}»: {ex.Message}"); }
    }

    public static DeleteResult Clean(JunkCategory c, CancellationToken ct)
    {
        Log.Write($"Очистка: {c.Name}");
        if (c.CustomClean != null) return c.CustomClean();
        var total = new DeleteResult();
        var stopped = new List<string>();
        try
        {
            if (c.StopServices != null)
                foreach (var s in c.StopServices)
                {
                    var (code, _) = Sys.Run("net.exe", $"stop {s} /y", 120_000);
                    if (code == 0) stopped.Add(s);
                }
            foreach (var t in c.Found)
            {
                if (ct.IsCancellationRequested) break;
                if (c.NeedsOwnership && Sys.IsAdmin) FileOps.TakeOwnership(t);
                var r = FileOps.DeleteTree(t, c.DeleteTargetsThemselves, c.MinAgeHours, false, ct);
                total.Merge(r);
            }
        }
        finally
        {
            foreach (var s in stopped) Sys.Run("net.exe", $"start {s}", 120_000);
        }
        Log.Write($"  освобождено {Fmt.Size(total.Freed)}, удалено файлов {total.FilesDeleted}, пропущено {total.Failed}");
        return total;
    }
}
