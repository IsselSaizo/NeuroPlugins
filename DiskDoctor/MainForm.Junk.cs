using DiskDoctor.Core;
using DiskDoctor.UI;

namespace DiskDoctor;

public sealed partial class MainForm
{
    TabPage _junkPage;
    ListView _lvJunk;
    TextBox _junkInfo;
    Label _junkTotal;
    Button _btnJunkAnalyze, _btnJunkClean;
    List<JunkCategory> _junk;
    bool _junkAnalyzedOnce;

    TabPage BuildJunkTab()
    {
        _junkPage = new TabPage("Очистка мусора");
        _lvJunk = Ui.List(true, ("Что чистим", 380, false), ("Размер", 100, true), ("Файлов", 90, true), ("Безопасность", 140, false), ("Описание", 600, false));
        _lvJunk.SelectedIndexChanged += (_, _) => ShowJunkInfo();
        _lvJunk.ItemChecked += (_, _) => UpdateJunkTotal();
        _junkInfo = Ui.InfoBox();
        _junkTotal = new Label { AutoSize = true, Margin = new Padding(12, 8, 3, 3), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };

        _btnJunkAnalyze = Ui.Btn("🔍 Найти мусор", async (_, _) => await AnalyzeJunk());
        _btnJunkClean = Ui.Btn("🧹 Очистить отмеченное", async (_, _) => await CleanJunk());
        var top = Ui.Bar(_btnJunkAnalyze, _btnJunkClean, _junkTotal);

        // системные инструменты
        var tools = new GroupBox { Text = "Системные инструменты (освобождают больше всего места на диске C:)", Dock = DockStyle.Bottom, Height = 120 };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
        flow.Controls.Add(Ui.Btn("DISM: очистка хранилища компонентов", (_, _) =>
        {
            if (!RequireAdmin()) return;
            if (Ui.Confirm(this, "Будет запущено «Dism.exe /Online /Cleanup-Image /StartComponentCleanup» в отдельном окне.\nЭто безопасно — удаляются старые версии системных компонентов после обновлений. Занимает 5–30 минут.\n\nЗапустить?"))
                Sys.RunInConsole("Dism.exe /Online /Cleanup-Image /StartComponentCleanup");
        }, "Сжимает папку Windows\\WinSxS штатным способом Windows.", _tt));
        flow.Controls.Add(Ui.Btn("Отключить гибернацию (hiberfil.sys)", (_, _) => ToggleHibernation(), "Удаляет файл hiberfil.sys (обычно 40% объёма ОЗУ). Быстрый запуск Windows тоже отключится.", _tt));
        flow.Controls.Add(Ui.Btn("Точки восстановления…", async (_, _) =>
        {
            var (_, outp) = await Bg("Чтение теневых копий…", () => Sys.Run("vssadmin.exe", "list shadowstorage"));
            Log.Write("vssadmin list shadowstorage:\r\n" + outp);
            Ui.Info(this, "Сколько места занимают точки восстановления:\n\n" + (outp.Length > 1500 ? outp[..1500] : outp) +
                          "\n\nСейчас откроется окно «Защита системы»: выберите диск → «Настроить» → уменьшите «Максимальное использование» или удалите старые точки.");
            Sys.ShellOpen("SystemPropertiesProtection.exe");
        }, "Показывает и настраивает место под точки восстановления (System Volume Information).", _tt));
        flow.Controls.Add(Ui.Btn("Создать точку восстановления", async (_, _) => await CreateRestorePoint(true), "Рекомендуется перед удалением программ и чисткой реестра.", _tt));
        flow.Controls.Add(Ui.Btn("Очистка диска Windows", (_, _) => Sys.ShellOpen("cleanmgr.exe", "/d " + Sys.SystemDrive.TrimEnd('\\')), "Встроенная утилита Windows.", _tt));
        flow.Controls.Add(Ui.Btn("Параметры → Память", (_, _) => Sys.ShellOpen("ms-settings:storagesense")));
        flow.Controls.Add(Ui.Btn("Файл подкачки…", (_, _) => Sys.ShellOpen("SystemPropertiesPerformance.exe"), "Быстродействие → Дополнительно → Виртуальная память.", _tt));
        tools.Controls.Add(flow);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_lvJunk);
        split.Panel2.Controls.Add(_junkInfo);
        Load += (_, _) => split.SplitterDistance = Math.Max(100, split.Height - 130);

        _junkPage.Controls.Add(split);
        _junkPage.Controls.Add(tools);
        _junkPage.Controls.Add(top);
        return _junkPage;
    }

    bool RequireAdmin()
    {
        if (Sys.IsAdmin) return true;
        Ui.Warn(this, "Для этого действия нужны права администратора. Перезапустите программу кнопкой в жёлтой полосе вверху окна.");
        return false;
    }

    async Task AnalyzeJunk()
    {
        _junkAnalyzedOnce = true;
        _btnJunkAnalyze.Enabled = _btnJunkClean.Enabled = false;
        var checkedBefore = _lvJunk.Items.Cast<ListViewItem>().ToDictionary(i => ((JunkCategory)((Row)i.Tag).Obj).Name, i => i.Checked);
        _junk = JunkCleaner.Build();
        _lvJunk.Items.Clear();
        foreach (var c in _junk)
        {
            var it = new ListViewItem(new[] { c.Name, "…", "", Classifier.RiskName(c.Risk), c.Description })
            {
                Tag = new Row(c, c.Name, 0L, 0L, (int)c.Risk, c.Description),
                Checked = checkedBefore.TryGetValue(c.Name, out var ch) ? ch : c.DefaultChecked,
            };
            Ui.StyleRisk(it, c.Risk, 3);
            _lvJunk.Items.Add(it);
        }
        BeginBusy("Поиск мусора…");
        try
        {
            foreach (ListViewItem it in _lvJunk.Items)
            {
                var c = (JunkCategory)((Row)it.Tag).Obj;
                SetStatus("Анализ: " + c.Name);
                await Task.Run(() => JunkCleaner.Analyze(c, CancellationToken.None));
                it.SubItems[1].Text = c.Size > 0 ? Fmt.Size(c.Size) : "—";
                it.SubItems[2].Text = c.Count > 0 ? Fmt.Count(c.Count) : "";
                ((Row)it.Tag).Keys[1] = c.Size; ((Row)it.Tag).Keys[2] = c.Count;
                if (c.Size == 0 && c.Count == 0) it.ForeColor = Color.Gray;
                UpdateJunkTotal();
            }
            long total = _junk.Sum(c => c.Size);
            SetStatus($"Найдено мусора: {Fmt.Size(total)}." + (Sys.IsAdmin ? "" : " Часть системных папок недоступна без прав администратора."));
        }
        finally
        {
            EndBusy();
            _btnJunkAnalyze.Enabled = _btnJunkClean.Enabled = true;
        }
    }

    void UpdateJunkTotal()
    {
        if (_junk == null) return;
        long sel = _lvJunk.Items.Cast<ListViewItem>().Where(i => i.Checked).Sum(i => ((JunkCategory)((Row)i.Tag).Obj).Size);
        _junkTotal.Text = $"Будет освобождено примерно: {Fmt.Size(sel)}";
    }

    void ShowJunkInfo()
    {
        if (_lvJunk.SelectedItems.Count == 0) return;
        var c = (JunkCategory)((Row)_lvJunk.SelectedItems[0].Tag).Obj;
        _junkInfo.Text = $"{c.Name} — {Classifier.RiskName(c.Risk)}\r\n{c.Description}\r\n" +
                         (c.MinAgeHours > 0 ? $"Файлы моложе {c.MinAgeHours} ч не удаляются.\r\n" : "") +
                         (c.Found.Count > 0 ? "Где найдено:\r\n" + string.Join("\r\n", c.Found.Take(60).Select(f => "  " + f)) + (c.Found.Count > 60 ? $"\r\n  … и ещё {c.Found.Count - 60}" : "") : "");
    }

    async Task CleanJunk()
    {
        if (_junk == null) { await AnalyzeJunk(); return; }
        var cats = _lvJunk.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (JunkCategory)((Row)i.Tag).Obj).Where(c => c.Size > 0 || c.Count > 0).ToList();
        if (cats.Count == 0) { Ui.Info(this, "Ничего не отмечено (или в отмеченных категориях нет мусора)."); return; }
        string text = "Будут очищены:\n\n" + string.Join("\n", cats.Select(c => $"• {c.Name} — {Fmt.Size(c.Size)}")) +
                      $"\n\nВсего ≈ {Fmt.Size(cats.Sum(c => c.Size))}.";
        if (cats.Any(c => c.Name.StartsWith("Кэш браузеров"))) text += "\n\nЗакройте браузеры перед очисткой (иначе часть файлов будет пропущена).";
        if (cats.Any(c => c.Risk == Risk.Caution)) text += "\n\n⚠ Отмечены категории с пометкой «Осторожно» — прочитайте их описание.";
        if (!Ui.Confirm(this, text + "\n\nПродолжить?", "Очистка", cats.Any(c => c.Risk == Risk.Caution) ? MessageBoxIcon.Warning : MessageBoxIcon.Question)) return;

        _btnJunkAnalyze.Enabled = _btnJunkClean.Enabled = false;
        var total = new DeleteResult();
        BeginBusy("Очистка…");
        try
        {
            foreach (var c in cats)
            {
                SetStatus("Очистка: " + c.Name);
                var r = await Task.Run(() => JunkCleaner.Clean(c, CancellationToken.None));
                total.Merge(r);
            }
        }
        finally { EndBusy(); _btnJunkAnalyze.Enabled = _btnJunkClean.Enabled = true; }
        string msg = $"Очистка завершена. Освобождено {Fmt.Size(total.Freed)} ({Fmt.Count(total.FilesDeleted)} файлов)." +
                     (total.Failed > 0 ? $"\n\n{Fmt.Count(total.Failed)} файлов пропущено — они заняты работающими программами или защищены. Это нормально." : "");
        Log.Write(msg.Replace("\n", " "));
        Ui.Info(this, msg);
        await AnalyzeJunk();
    }

    void ToggleHibernation()
    {
        if (!RequireAdmin()) return;
        string hib = Path.Combine(Sys.SystemDrive, "hiberfil.sys");
        long size = 0;
        try { size = new FileInfo(hib).Length; } catch { }
        bool exists = File.Exists(hib);
        if (exists)
        {
            if (!Ui.Confirm(this, $"Отключить гибернацию? Файл hiberfil.sys ({Fmt.Size(size)}) будет удалён.\n\nПобочные эффекты: пропадёт режим «Гибернация» и «Быстрый запуск» Windows (компьютер будет включаться на пару секунд дольше).\nВернуть: эта же кнопка или команда powercfg /h on.")) return;
            var (code, outp) = Sys.Run("powercfg.exe", "/h off");
            Log.Write($"powercfg /h off → {code} {outp}");
            Ui.Info(this, code == 0 ? $"Гибернация отключена, освобождено ~{Fmt.Size(size)}." : "Не удалось: " + outp);
        }
        else if (Ui.Confirm(this, "Файла hiberfil.sys нет — гибернация уже отключена. Включить её обратно?"))
        {
            var (code, outp) = Sys.Run("powercfg.exe", "/h on");
            Log.Write($"powercfg /h on → {code} {outp}");
        }
    }

    async Task<bool> CreateRestorePoint(bool interactive)
    {
        if (!Sys.IsAdmin) { if (interactive) RequireAdmin(); return false; }
        var (code, outp) = await Bg("Создание точки восстановления…", () =>
            Sys.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Enable-ComputerRestore -Drive '" + Sys.SystemDrive + "' -ErrorAction SilentlyContinue; Checkpoint-Computer -Description 'DiskDoctor' -RestorePointType MODIFY_SETTINGS\"", 600_000));
        Log.Write($"Точка восстановления: код {code} {outp}");
        if (interactive)
        {
            if (code == 0 && !outp.Contains("WARNING", StringComparison.OrdinalIgnoreCase) && !outp.Contains("ПРЕДУПРЕЖДЕНИЕ", StringComparison.OrdinalIgnoreCase))
                Ui.Info(this, "Точка восстановления создана.");
            else Ui.Warn(this, "Точку восстановления создать не удалось (Windows разрешает не чаще одной в сутки, либо защита системы отключена):\n\n" + outp);
        }
        return code == 0;
    }
}
