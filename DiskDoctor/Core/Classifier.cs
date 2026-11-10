using System.Text.RegularExpressions;

namespace DiskDoctor.Core;

public enum Risk
{
    Unknown,   // не классифицировано
    Safe,      // можно чистить
    User,      // ваши файлы — решайте сами
    Caution,   // осторожно: настройки/данные программ
    Program,   // установленная программа — удалять деинсталлятором
    Critical,  // система — не трогать
}

public sealed class RuleMatch
{
    public Risk Risk;
    public string Title;
    public string Advice;
    public bool Inherited;
    internal bool Generic;
    public static readonly RuleMatch None = new() { Risk = Risk.Unknown, Title = "", Advice = "" };
}

public static class Classifier
{
    sealed class Rule
    {
        public Regex Re; public Risk Risk; public string Title; public string Advice; public bool Generic; public int Len;
    }

    static readonly List<Rule> Rules = new();
    static readonly List<Rule> FileRules = new();

    public static string RiskName(Risk r) => r switch
    {
        Risk.Safe => "Можно удалить",
        Risk.User => "Ваши данные",
        Risk.Caution => "Осторожно",
        Risk.Program => "Программа",
        Risk.Critical => "Системное — не трогать",
        _ => "",
    };

    public static Color RiskColor(Risk r) => r switch
    {
        Risk.Safe => Color.FromArgb(0, 120, 40),
        Risk.User => Color.FromArgb(30, 80, 170),
        Risk.Caution => Color.FromArgb(170, 110, 0),
        Risk.Program => Color.FromArgb(200, 80, 0),
        Risk.Critical => Color.FromArgb(190, 0, 0),
        _ => SystemColors.WindowText,
    };

    public static Color RiskBack(Risk r) => r switch
    {
        Risk.Safe => Color.FromArgb(225, 245, 228),
        Risk.User => Color.FromArgb(228, 236, 250),
        Risk.Caution => Color.FromArgb(252, 244, 220),
        Risk.Program => Color.FromArgb(253, 233, 218),
        Risk.Critical => Color.FromArgb(252, 225, 225),
        _ => SystemColors.Window,
    };

    // Шаблоны относительно корня диска. "*" — одна папка, "**" — любое число папок.
    static void R(string pattern, Risk risk, string title, string advice)
    {
        string rx = "^" + string.Join(@"\\", pattern.Split('\\').Select(seg =>
            seg == "**" ? "§§" : Regex.Escape(seg).Replace(@"\*", @"[^\\]*").Replace(@"\?", @"[^\\]")))
            .Replace(@"§§\\", @"(?:[^\\]+\\)*") + "$";
        var rule = new Rule { Re = new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), Risk = risk, Title = title, Advice = advice, Generic = pattern.StartsWith("**"), Len = rx.Length };
        Rules.Add(rule);
        var segs = pattern.Split('\\');
        string key;
        if (rule.Generic && !segs[^1].Contains('*')) key = "G|" + segs[^1].ToLowerInvariant();
        else if (!segs[0].Contains('*') && !pattern.Contains("**")) key = "A|" + segs[0].ToLowerInvariant() + "|" + segs.Length;
        else { Slow.Add(rule); return; }
        if (!Index.TryGetValue(key, out var l)) Index[key] = l = new List<Rule>();
        l.Add(rule);
    }

    static readonly Dictionary<string, List<Rule>> Index = new();
    static readonly List<Rule> Slow = new();

    static void F(string regex, Risk risk, string title, string advice) =>
        FileRules.Add(new Rule { Re = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.Compiled), Risk = risk, Title = title, Advice = advice });

    static Classifier()
    {
        const string browserCache = "Кэш браузера";
        const string cacheAdvice = "Кэш — восстанавливается автоматически. Безопасно чистится на вкладке «Очистка мусора» (закройте программу перед очисткой).";

        // --- Корень системы
        R(@"Windows", Risk.Critical, "Папка Windows", "Сама система. Ничего не удаляйте вручную. Для сжатия используйте «Очистку хранилища компонентов (DISM)» на вкладке очистки.");
        R(@"Windows\WinSxS", Risk.Critical, "Хранилище компонентов Windows", "Нельзя удалять вручную — Windows перестанет обновляться/загружаться. Реальный размер меньше показанного (много жёстких ссылок). Уменьшить: кнопка «DISM: очистка компонентов».");
        R(@"Windows\Installer", Risk.Critical, "Кэш установщика Windows (MSI)", "Нужен для удаления и обновления программ. Ручное удаление отсюда ломает деинсталляцию (типичная причина «программа не удаляется и не ставится заново»). Осиротевшие файлы можно убрать через вкладку очистки.");
        R(@"Windows\Installer\$PatchCache$", Risk.Caution, "Кэш патчей MSI", "Используется только для отката обновлений программ. Можно удалить, если места совсем мало.");
        R(@"Windows\Temp", Risk.Safe, "Временные файлы Windows", cacheAdvice);
        R(@"Windows\SoftwareDistribution", Risk.Caution, "Служба обновлений Windows", "Сама папка нужна. Подпапка Download — скачанные обновления, их можно чистить.");
        R(@"Windows\SoftwareDistribution\Download", Risk.Safe, "Скачанные обновления Windows", "Уже установленные или устаревшие пакеты. Безопасно чистится на вкладке «Очистка мусора».");
        R(@"Windows\Logs", Risk.Safe, "Журналы Windows", "Логи служб и обновлений. Старые можно удалить.");
        R(@"Windows\Prefetch", Risk.Safe, "Prefetch", "Ускоряет запуск программ. Можно очистить — пересоздастся, первые запуски будут чуть медленнее.");
        R(@"Windows\LiveKernelReports", Risk.Safe, "Отчёты ядра (дампы)", "Дампы сбоев драйверов (часто видеодрайвера). Нужны только для диагностики — можно удалять.");
        R(@"Windows\Minidump", Risk.Safe, "Мини-дампы BSOD", "Нужны только для анализа синих экранов. Можно удалять.");
        R(@"Windows\MEMORY.DMP", Risk.Safe, "Полный дамп памяти", "Остался после синего экрана. Можно удалить.");
        R(@"Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization", Risk.Safe, "Кэш оптимизации доставки", "Обновления для раздачи другим ПК. Можно чистить.");
        R(@"Windows\System32", Risk.Critical, "System32", "Ядро системы. Не трогать.");
        R(@"Windows\SysWOW64", Risk.Critical, "SysWOW64", "32-битные компоненты системы. Не трогать.");
        R(@"Windows\assembly", Risk.Critical, "Глобальный кэш сборок .NET", "Не трогать.");
        R(@"Windows\Microsoft.NET", Risk.Critical, ".NET Framework", "Не трогать.");
        R(@"Windows\Fonts", Risk.Critical, "Шрифты", "Удаляйте шрифты только через Параметры → Шрифты.");
        R(@"Windows\Downloaded Program Files", Risk.Safe, "Загруженные элементы ActiveX", "Устаревшее, можно удалить.");
        R(@"Windows\CSC", Risk.Caution, "Автономные файлы", "Кэш автономных файлов сетевых папок.");
        R(@"Windows.old", Risk.Caution, "Предыдущая версия Windows", "Нужна только для отката после обновления. Удаляется на вкладке «Очистка мусора» (откат станет невозможен).");
        R(@"$Windows.~BT", Risk.Safe, "Остатки обновления Windows", "Временные файлы обновления/установки Windows. Удаляются на вкладке очистки.");
        R(@"$Windows.~WS", Risk.Safe, "Остатки обновления Windows", "Временные файлы обновления/установки Windows. Удаляются на вкладке очистки.");
        R(@"$WINDOWS.~Q", Risk.Safe, "Остатки обновления Windows", "Временные файлы обновления.");
        R(@"$GetCurrent", Risk.Safe, "Остатки обновления Windows", "Временные файлы помощника обновления.");
        R(@"$SysReset", Risk.Safe, "Остатки сброса Windows", "Логи после сброса/восстановления системы. Можно удалить.");
        R(@"ESD", Risk.Safe, "Установочные файлы Windows (ESD)", "Остались от обновления/Media Creation Tool. Можно удалить.");
        R(@"$Recycle.Bin", Risk.Safe, "Корзина", "Удалённые файлы. Очищается на вкладке «Очистка мусора» или из Корзины.");
        R(@"RECYCLER", Risk.Safe, "Корзина (старый формат)", "Можно очистить.");
        R(@"System Volume Information", Risk.Critical, "Точки восстановления / служебные данные", "Удалять вручную нельзя. Размер точек восстановления меняется в Свойствах системы → Защита системы (кнопка на вкладке очистки).");
        R(@"pagefile.sys", Risk.Critical, "Файл подкачки", "Размер настраивается: Параметры системы → Дополнительно → Быстродействие → Виртуальная память. Не удалять вручную.");
        R(@"swapfile.sys", Risk.Critical, "Файл подкачки приложений Store", "Управляется системой.");
        R(@"hiberfil.sys", Risk.Caution, "Файл гибернации", "Занимает ~40% ОЗУ. Если не пользуетесь гибернацией — отключите командой powercfg /h off (кнопка на вкладке очистки). Вручную не удалять.");
        R(@"Recovery", Risk.Critical, "Среда восстановления", "Не трогать.");
        R(@"Boot", Risk.Critical, "Загрузчик", "Не трогать.");
        R(@"bootmgr", Risk.Critical, "Загрузчик", "Не трогать.");
        R(@"Config.Msi", Risk.Caution, "Откат установки MSI", "Остаётся после прерванной установки. Если сейчас ничего не устанавливается — можно удалить.");
        R(@"MSOCache", Risk.Caution, "Кэш установки MS Office", "Нужен для восстановления Office без диска. Можно удалить при нехватке места.");
        R(@"NVIDIA", Risk.Safe, "Распакованный драйвер NVIDIA", "Остатки установки драйвера. Можно удалить.");
        R(@"AMD", Risk.Safe, "Распакованный драйвер AMD", "Остатки установки драйвера. Можно удалить.");
        R(@"PerfLogs", Risk.Safe, "Журналы производительности", "Обычно пустая. Можно удалить содержимое.");
        R(@"OneDriveTemp", Risk.Safe, "Временные файлы OneDrive", "Можно удалить.");

        // --- Программы
        R(@"Program Files", Risk.Program, "Установленные программы", "Удаляйте программы через вкладку «Программы» (штатно), а не удалением папок.");
        R(@"Program Files (x86)", Risk.Program, "Установленные программы (32-бит)", "Удаляйте программы через вкладку «Программы».");
        R(@"Program Files\*", Risk.Program, "Папка программы", "Удаляйте через вкладку «Программы». Если программы там нет — это, вероятно, хвост: используйте вкладку «Хвосты».");
        R(@"Program Files (x86)\*", Risk.Program, "Папка программы", "Удаляйте через вкладку «Программы». Если программы там нет — это, вероятно, хвост.");
        R(@"Program Files\Common Files", Risk.Critical, "Общие компоненты программ", "Используются разными программами. Не трогать.");
        R(@"Program Files (x86)\Common Files", Risk.Critical, "Общие компоненты программ", "Используются разными программами. Не трогать.");
        R(@"Program Files\WindowsApps", Risk.Program, "Приложения Microsoft Store", "Удаляйте через Параметры → Приложения.");
        R(@"Program Files\Windows Defender", Risk.Critical, "Защитник Windows", "Не трогать.");
        R(@"Program Files\dotnet", Risk.Caution, ".NET", "Удаляйте версии через «Программы».");
        R(@"ProgramData", Risk.Caution, "Общие данные программ", "Настройки и базы программ для всех пользователей. Удаляйте только папки удалённых программ (вкладка «Хвосты»).");
        R(@"ProgramData\Microsoft", Risk.Critical, "Системные данные Microsoft", "Не трогать (кроме WER — отчётов об ошибках).");
        R(@"ProgramData\Microsoft\Windows\Start Menu", Risk.Caution, "Меню «Пуск»", "Ярлыки программ. Ярлыки удалённых программ можно удалить.");
        R(@"ProgramData\Microsoft\Windows\Start Menu\Programs\*", Risk.Safe, "Ярлыки меню «Пуск»", "Ярлыки программ. Удаление ярлыка не удаляет программу.");
        R(@"Users\*\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\*", Risk.Safe, "Ярлыки меню «Пуск»", "Ярлыки программ. Удаление ярлыка не удаляет программу.");
        R(@"ProgramData\Microsoft\Windows\WER", Risk.Safe, "Отчёты об ошибках Windows", "Можно удалить.");
        R(@"ProgramData\Package Cache", Risk.Caution, "Кэш установщиков (Visual Studio, VC++ и др.)", "Нужен для изменения/удаления этих программ. Удалять не рекомендуется.");
        R(@"ProgramData\NVIDIA Corporation\Downloader", Risk.Safe, "Скачанные драйверы NVIDIA", "Можно удалить.");
        R(@"ProgramData\NVIDIA Corporation\NV_Cache", Risk.Safe, "Кэш шейдеров NVIDIA", cacheAdvice);
        R(@"ProgramData\Blackmagic Design", Risk.Caution, "Данные Blackmagic / DaVinci Resolve", "Базы проектов DaVinci Resolve могут храниться здесь! Перед удалением сделайте резервную копию проектов.");

        // --- Пользователи
        R(@"Users", Risk.User, "Профили пользователей", "Ваши файлы и настройки.");
        R(@"Users\*", Risk.User, "Профиль пользователя", "Документы, загрузки, рабочий стол и настройки программ.");
        R(@"Users\*\Downloads", Risk.User, "Загрузки", "Часто здесь копятся старые установщики и архивы. Просмотрите и удалите ненужное.");
        R(@"Users\*\Desktop", Risk.User, "Рабочий стол", "Ваши файлы.");
        R(@"Users\*\Documents", Risk.User, "Документы", "Ваши файлы.");
        R(@"Users\*\Videos", Risk.User, "Видео", "Ваши файлы.");
        R(@"Users\*\Pictures", Risk.User, "Изображения", "Ваши файлы.");
        R(@"Users\*\AppData", Risk.Caution, "Данные программ (AppData)", "Настройки, кэши и сохранения программ. Чистите только известные кэши.");
        R(@"Users\*\AppData\Local\Temp", Risk.Safe, "Временные файлы пользователя", cacheAdvice);
        R(@"Users\*\AppData\Local\CrashDumps", Risk.Safe, "Дампы падений программ", "Можно удалить.");
        R(@"Users\*\AppData\Local\Microsoft\Windows\INetCache", Risk.Safe, "Кэш Интернета", cacheAdvice);
        R(@"Users\*\AppData\Local\Microsoft\Windows\WER", Risk.Safe, "Отчёты об ошибках", "Можно удалить.");
        R(@"Users\*\AppData\Local\Microsoft\Windows\Explorer", Risk.Safe, "Кэш эскизов и значков", "Пересоздаётся автоматически.");
        R(@"Users\*\AppData\Local\D3DSCache", Risk.Safe, "Кэш шейдеров DirectX", cacheAdvice);
        R(@"Users\*\AppData\Local\NVIDIA\DXCache", Risk.Safe, "Кэш шейдеров NVIDIA", cacheAdvice);
        R(@"Users\*\AppData\Local\NVIDIA\GLCache", Risk.Safe, "Кэш шейдеров NVIDIA", cacheAdvice);
        R(@"Users\*\AppData\Local\AMD\DxCache", Risk.Safe, "Кэш шейдеров AMD", cacheAdvice);
        R(@"Users\*\AppData\Local\AMD\DxcCache", Risk.Safe, "Кэш шейдеров AMD", cacheAdvice);
        R(@"Users\*\AppData\Local\AMD\VkCache", Risk.Safe, "Кэш шейдеров AMD", cacheAdvice);
        R(@"Users\*\AppData\Local\Packages", Risk.Program, "Данные приложений Store", "Удаляются вместе с приложением.");
        R(@"Users\*\AppData\Local\pip\cache", Risk.Safe, "Кэш pip (Python)", "Пакеты перескачаются при необходимости.");
        R(@"Users\*\AppData\Local\npm-cache", Risk.Safe, "Кэш npm", "Пакеты перескачаются при необходимости.");
        R(@"Users\*\AppData\Local\Yarn\Cache", Risk.Safe, "Кэш Yarn", "Пакеты перескачаются при необходимости.");
        R(@"Users\*\AppData\Local\NuGet", Risk.Safe, "Кэш NuGet", "Пакеты перескачаются при необходимости.");
        R(@"Users\*\AppData\Local\go-build", Risk.Safe, "Кэш сборки Go", cacheAdvice);
        R(@"Users\*\.nuget\packages", Risk.Caution, "Пакеты NuGet", "Перескачаются при сборке проектов, но это может занять время.");
        R(@"Users\*\.cache", Risk.Caution, "Кэш утилит (.cache)", "Часто здесь модели HuggingFace/Torch — перескачиваются, но они большие.");
        R(@"Users\*\.cache\huggingface", Risk.Caution, "Модели HuggingFace", "Скачанные нейросетевые модели. Удаляйте ненужные — перескачаются при использовании.");
        R(@"Users\*\.gradle\caches", Risk.Safe, "Кэш Gradle", "Перескачается при сборке.");
        R(@"Users\*\.android\avd", Risk.Caution, "Эмуляторы Android", "Образы эмуляторов — удаляйте ненужные через Android Studio.");
        R(@"Users\*\AppData\Roaming\Telegram Desktop\tdata\user_data", Risk.Safe, "Кэш Telegram", "Медиа из чатов — перескачается. Закройте Telegram перед очисткой.");
        R(@"Users\*\AppData\Roaming\Blackmagic Design", Risk.Caution, "Настройки DaVinci Resolve", "Настройки и, возможно, базы проектов. Сохраните проекты перед удалением.");

        // браузеры и Electron-приложения
        foreach (var b in new[] { @"Google\Chrome\User Data\*", @"Microsoft\Edge\User Data\*", @"Yandex\YandexBrowser\User Data\*", @"BraveSoftware\Brave-Browser\User Data\*", @"Vivaldi\User Data\*", @"Chromium\User Data\*" })
        {
            R($@"Users\*\AppData\Local\{b}\Cache", Risk.Safe, browserCache, cacheAdvice);
            R($@"Users\*\AppData\Local\{b}\Code Cache", Risk.Safe, browserCache, cacheAdvice);
            R($@"Users\*\AppData\Local\{b}\GPUCache", Risk.Safe, browserCache, cacheAdvice);
            R($@"Users\*\AppData\Local\{b}\Service Worker\CacheStorage", Risk.Safe, "Кэш сайтов", cacheAdvice);
        }
        R(@"Users\*\AppData\Local\Opera Software\*\Cache", Risk.Safe, browserCache, cacheAdvice);
        R(@"Users\*\AppData\Local\Mozilla\Firefox\Profiles\*\cache2", Risk.Safe, browserCache, cacheAdvice);
        R(@"**\GPUCache", Risk.Safe, "Кэш GPU (Electron/Chromium)", cacheAdvice);
        R(@"**\Code Cache", Risk.Safe, "Кэш кода (Electron/Chromium)", cacheAdvice);
        R(@"**\DXCache", Risk.Safe, "Кэш шейдеров DirectX", cacheAdvice);
        R(@"**\GLCache", Risk.Safe, "Кэш шейдеров OpenGL", cacheAdvice);
        R(@"**\ShaderCache", Risk.Safe, "Кэш шейдеров", cacheAdvice);
        R(@"**\shadercache", Risk.Safe, "Кэш шейдеров Steam", "Пересоздаётся при запуске игр.");
        R(@"**\steamapps\downloading", Risk.Safe, "Незавершённые загрузки Steam", "Если ничего не качается — можно удалить.");
        R(@"**\steamapps\common", Risk.Program, "Игры Steam", "Удаляйте игры через Steam.");
        R(@"**\node_modules", Risk.Safe, "Зависимости Node.js", "Восстанавливается командой npm install. Удаляйте в неактивных проектах.");
        R(@"**\__pycache__", Risk.Safe, "Кэш Python", "Пересоздаётся автоматически.");
        R(@"**\.venv", Risk.Caution, "Виртуальное окружение Python", "Восстанавливается переустановкой зависимостей проекта.");
        R(@"**\venv", Risk.Caution, "Виртуальное окружение Python", "Восстанавливается переустановкой зависимостей проекта.");
        R(@"**\CacheClip", Risk.Safe, "Кэш рендера DaVinci Resolve", "Пересоздаётся Resolve (Воспроизведение → Удалить кэш рендера). Безопасно удалить.");
        R(@"**\ProxyMedia", Risk.Caution, "Прокси-файлы DaVinci Resolve", "Можно удалить, Resolve пересоздаст прокси (долго).");
        R(@"**\.git", Risk.Caution, "История Git", "История версий проекта. Не удаляйте, если проект нужен.");
        R(@"**\obj", Risk.Caution, "Промежуточные файлы сборки", "Если это проект разработки — пересоздаётся при сборке.");

        // файлы по имени
        F(@"\.(tmp|temp)$", Risk.Safe, "Временный файл", "Обычно можно удалить, если программа, создавшая его, закрыта.");
        F(@"\.(dmp|mdmp|hdmp)$", Risk.Safe, "Дамп памяти", "Нужен только для диагностики сбоя. Можно удалить.");
        F(@"\.(log|etl)$", Risk.Safe, "Журнал", "Старые логи можно удалять.");
        F(@"\.(old|bak)$", Risk.Caution, "Резервная копия", "Проверьте, что оригинал на месте.");
        F(@"^thumbs\.db$", Risk.Safe, "Кэш эскизов", "Можно удалить.");
        F(@"\.(iso|img)$", Risk.User, "Образ диска", "Если уже установили/записали — можно удалить.");
        F(@"\.(msi|exe)$", Risk.User, "Установщик/программа", "Если это скачанный установщик в Загрузках — после установки он не нужен.");
        F(@"\.(vhd|vhdx|vmdk|vdi)$", Risk.Caution, "Виртуальный диск", "Диск виртуальной машины/WSL/Docker. Удаляйте через соответствующую программу.");
    }

    /// <summary>Путь относительно корня диска: "C:\Windows\Temp" → "Windows\Temp".</summary>
    static string Rel(string full)
    {
        string root = Path.GetPathRoot(full) ?? "";
        return full.Substring(root.Length).TrimEnd('\\');
    }

    static RuleMatch Match(string rel)
    {
        Rule best = null; int bestLen = -1;
        void Try(List<Rule> list)
        {
            if (list == null) return;
            foreach (var r in list)
                if (r.Len > bestLen && r.Re.IsMatch(rel)) { best = r; bestLen = r.Len; } // более конкретный (длинный) шаблон выигрывает
        }
        if (rel.Length == 0) return null;
        int segCount = 1, firstEnd = rel.IndexOf('\\');
        foreach (char c in rel) if (c == '\\') segCount++;
        string first = (firstEnd < 0 ? rel : rel.Substring(0, firstEnd)).ToLowerInvariant();
        int lastStart = rel.LastIndexOf('\\');
        string last = rel.Substring(lastStart + 1).ToLowerInvariant();
        Index.TryGetValue("A|" + first + "|" + segCount, out var a); Try(a);
        Index.TryGetValue("G|" + last, out var g); Try(g);
        Try(Slow);
        return best == null ? null : new RuleMatch { Risk = best.Risk, Title = best.Title, Advice = best.Advice, Generic = best.Generic };
    }

    /// <summary>Сколько места занимает каждая категория критичности (по всему дереву).</summary>
    public static Dictionary<Risk, long> Totals(DirNode root)
    {
        var t = Enum.GetValues<Risk>().ToDictionary(r => r, _ => 0L);
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            long own = n.OwnFiles;
            if (n.Parent == null)
            {
                // файлы в корне (pagefile.sys, hiberfil.sys…) классифицируем по имени
                try
                {
                    foreach (var f in new DirectoryInfo(n.FullPath).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                    {
                        long len; try { len = f.Length; } catch { continue; }
                        var r = ForPath(f.FullName).Risk;
                        if (r != Risk.Unknown) { t[r] += len; own -= len; }
                    }
                }
                catch { }
            }
            t[ForNode(n).Risk] += Math.Max(0, own);
            if (n.Children != null) foreach (var c in n.Children) stack.Push(c);
        }
        return t;
    }

    public static RuleMatch ForNode(DirNode n)
    {
        if (n.CachedClass != null) return n.CachedClass;
        RuleMatch m = null;
        if (n.Parent == null)
        {
            // корень сканирования: диск или произвольная папка
            n.CachedClass = Rel(n.FullPath).Length == 0 ? RuleMatch.None : ForPath(n.FullPath);
            return n.CachedClass;
        }
        m = Match(Rel(n.FullPath));
        // generic rules (node_modules, obj...) must not downgrade system folders
        if (m != null && m.Generic && n.Parent != null && ForNode(n.Parent).Risk == Risk.Critical) m = null;
        if (m == null)
        {
            var p = n.Parent == null ? RuleMatch.None : ForNode(n.Parent);
            m = p.Risk == Risk.Unknown ? RuleMatch.None : new RuleMatch { Risk = p.Risk, Title = p.Title, Advice = p.Advice, Inherited = true };
        }
        n.CachedClass = m;
        return m;
    }

    public static RuleMatch ForPath(string full)
    {
        string rel = Rel(full);
        if (rel.Length == 0) return RuleMatch.None;
        var m = Match(rel);
        if (m != null && m.Generic && ForPath(Path.GetDirectoryName(full) ?? "").Risk == Risk.Critical) m = null;
        if (m != null) return m;
        // правила по имени файла имеют приоритет перед наследованием от папки
        string name = Path.GetFileName(full);
        foreach (var f in FileRules)
            if (f.Re.IsMatch(name))
            {
                var parent = ForPath(Path.GetDirectoryName(full) ?? "");
                // в системных папках файл наследует критичность папки
                if (parent.Risk == Risk.Critical) return new RuleMatch { Risk = Risk.Critical, Title = parent.Title, Advice = parent.Advice, Inherited = true };
                return new RuleMatch { Risk = f.Risk, Title = f.Title, Advice = f.Advice };
            }
        string dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || dir == full) return RuleMatch.None;
        var pm = ForPath(dir);
        return pm.Risk == Risk.Unknown ? RuleMatch.None : new RuleMatch { Risk = pm.Risk, Title = pm.Title, Advice = pm.Advice, Inherited = true };
    }

    public static RuleMatch ForFile(DirNode dir, string fileName)
    {
        string full = Path.Combine(dir.FullPath, fileName);
        var m = Match(Rel(full));
        var parent = ForNode(dir);
        if (m != null && !(m.Generic && parent.Risk == Risk.Critical)) return m;
        foreach (var f in FileRules)
            if (f.Re.IsMatch(fileName))
            {
                if (parent.Risk == Risk.Critical) break;
                return new RuleMatch { Risk = f.Risk, Title = f.Title, Advice = f.Advice };
            }
        return parent.Risk == Risk.Unknown ? RuleMatch.None : new RuleMatch { Risk = parent.Risk, Title = parent.Title, Advice = parent.Advice, Inherited = true };
    }
}

