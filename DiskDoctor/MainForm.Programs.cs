using DiskDoctor.Core;
using DiskDoctor.UI;

namespace DiskDoctor;

public sealed partial class MainForm
{
    TabPage _progPage;
    ListView _lvProg;
    TextBox _progInfo, _progFilter;
    CheckBox _chkBroken, _chkHidden;
    List<InstalledProgram> _programs;

    TabPage BuildProgramsTab()
    {
        _progPage = new TabPage("Программы");
        _lvProg = Ui.List(false, ("Название", 330, false), ("Версия", 110, false), ("Издатель", 180, false), ("Размер", 85, true), ("Установлена", 90, false), ("Тип", 170, false), ("Состояние", 520, false));
        _lvProg.SelectedIndexChanged += (_, _) => ShowProgInfo();
        _lvProg.DoubleClick += (_, _) => ShowProgInfo();
        _progInfo = Ui.InfoBox();
        _progFilter = new TextBox { Width = 220, Margin = new Padding(3, 6, 3, 3), PlaceholderText = "Поиск по названию…" };
        _progFilter.TextChanged += (_, _) => FillPrograms();
        _chkBroken = new CheckBox { Text = "Только проблемные", AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
        _chkBroken.CheckedChanged += (_, _) => FillPrograms();
        _chkHidden = new CheckBox { Text = "Показать скрытые компоненты", AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
        _chkHidden.CheckedChanged += (_, _) => FillPrograms();

        var bar = Ui.Bar(
            _progFilter, _chkBroken, _chkHidden,
            Ui.Btn("⟳ Обновить", async (_, _) => await LoadPrograms()),
            Ui.Btn("🗑 Удалить штатно", async (_, _) => await UninstallSelected(), "Запускает родной деинсталлятор программы (как в «Программы и компоненты»).", _tt),
            Ui.Btn("💥 Принудительное удаление / хвосты…", (_, _) => ForceRemoveSelected(), "Для «сломанных» программ: ищет и удаляет записи установщика, папки, ключи реестра, службы и ярлыки.", _tt),
            Ui.Btn("📂 Папка", (_, _) => { var p = SelectedProgram(); if (p?.InstallLocation != null) Sys.OpenInExplorer(p.InstallLocation); }),
            Ui.Btn("🔑 В реестре", (_, _) => { var p = SelectedProgram(); if (p != null) Sys.OpenRegedit(p.FullKey); }),
            Ui.Btn("Программы и компоненты", (_, _) => Sys.ShellOpen("appwiz.cpl")));

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_lvProg);
        split.Panel2.Controls.Add(_progInfo);
        Load += (_, _) => split.SplitterDistance = Math.Max(100, split.Height - 170);
        _progPage.Controls.Add(split);
        _progPage.Controls.Add(bar);
        return _progPage;
    }

    InstalledProgram SelectedProgram() => _lvProg.SelectedItems.Count == 1 ? (_lvProg.SelectedItems[0].Tag as Row)?.Obj as InstalledProgram : null;

    async Task LoadPrograms()
    {
        _lvProg.Items.Clear();
        _programs = await Bg("Чтение списка программ и проверка установщика Windows…", () => ProgramList.Load());
        FillPrograms();
        int broken = _programs.Count(p => p.Broken && !p.Hidden);
        SetStatus($"Программ: {_programs.Count(p => !p.Hidden)}" + (broken > 0 ? $", с проблемами: {broken} (выделены красным)" : ", проблем не найдено"));
    }

    void FillPrograms()
    {
        if (_programs == null) return;
        string f = _progFilter.Text.Trim();
        _lvProg.BeginUpdate();
        _lvProg.Items.Clear();
        foreach (var p in _programs.OrderBy(p => p.Broken ? 0 : 1))
        {
            if (p.Hidden && !_chkHidden.Checked && !(p.Broken && _chkBroken.Checked)) continue;
            if (_chkBroken.Checked && !p.Broken) continue;
            if (f.Length > 0 && !(p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || (p.Publisher ?? "").Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
            string date = FormatDate(p.InstallDate);
            string type = (p.IsMsi ? "MSI" : "EXE") + ", " + p.Scope + (p.Hidden ? ", скрыт." : "");
            string state = p.Broken ? "⚠ " + string.Join("; ", p.Problems) : "OK";
            var it = new ListViewItem(new[] { p.Name, p.Version ?? "", p.Publisher ?? "", p.EstimatedSizeKB > 0 ? Fmt.Size(p.EstimatedSizeKB * 1024) : "", date, type, state })
            {
                Tag = new Row(p, p.Name, p.Version ?? "", p.Publisher ?? "", p.EstimatedSizeKB, p.InstallDate ?? "", type, p.Broken ? 0 : 1),
            };
            if (p.Broken) Ui.StyleRisk(it, Risk.Critical, 6);
            else if (p.Hidden) it.ForeColor = Color.Gray;
            _lvProg.Items.Add(it);
        }
        _lvProg.EndUpdate();
    }

    static string FormatDate(string d) =>
        d != null && d.Length == 8 && d.All(char.IsDigit) ? $"{d[6..8]}.{d[4..6]}.{d[..4]}" : d ?? "";

    void ShowProgInfo()
    {
        var p = SelectedProgram();
        if (p == null) return;
        var lines = new List<string>
        {
            $"{p.Name} {p.Version}   ({p.Publisher})",
            $"Раздел реестра: {p.FullKey}",
            $"Папка: {(string.IsNullOrEmpty(p.InstallLocation) ? "не указана" : p.InstallLocation + (Directory.Exists(p.InstallLocation) ? "" : "  — НЕ СУЩЕСТВУЕТ"))}",
            $"Команда удаления: {p.UninstallString}",
        };
        if (p.IsMsi) lines.Add($"Установщик MSI, код продукта {p.ProductCode}");
        if (p.Broken)
        {
            lines.Add("");
            lines.Add("⚠ ПРОБЛЕМЫ: " + string.Join("; ", p.Problems));
            lines.Add("Что делать: 1) попробуйте «Удалить штатно». 2) Если удаление/переустановка выдаёт ошибку — нажмите «Принудительное удаление / хвосты»: " +
                      "программа найдёт регистрацию в установщике Windows, записи реестра, папки, службы и ярлыки, сохранит резервную копию реестра и удалит отмеченное. " +
                      "3) Перезагрузите компьютер и установите программу заново.");
        }
        _progInfo.Text = string.Join("\r\n", lines);
    }

    async Task UninstallSelected()
    {
        var p = SelectedProgram();
        if (p == null) { Ui.Info(this, "Выберите программу в списке."); return; }
        if (!Ui.Confirm(this, $"Запустить штатное удаление «{p.Name}»?\n\nОткроется деинсталлятор программы — следуйте его инструкциям." +
                              (p.Broken ? "\n\n⚠ У программы найдены проблемы, деинсталлятор может завершиться ошибкой. Тогда используйте «Принудительное удаление»." : ""))) return;
        string logDir = AppDir.NewBackupDir();
        var proc = ProgramList.RunUninstaller(p, logDir);
        if (proc == null) { Ui.Warn(this, "Не удалось запустить деинсталлятор. Используйте «Принудительное удаление / хвосты»."); return; }
        BeginBusy($"Ожидание деинсталлятора «{p.Name}»…");
        int code = -1;
        try { await proc.WaitForExitAsync(); code = proc.ExitCode; } catch { }
        finally { EndBusy(); }
        Log.Write($"Деинсталлятор «{p.Name}» завершился с кодом {code}");
        await LoadPrograms();
        bool stillThere = _programs.Any(x => x.KeyPath == p.KeyPath && x.Hive == p.Hive);
        if (stillThere && code != 0 && code != 1602 && code != 3010)
        {
            if (Ui.Confirm(this, $"Деинсталлятор завершился с кодом {code}, программа осталась в списке.\n\nЗапустить принудительное удаление (поиск и удаление хвостов)?", "Удаление не удалось", MessageBoxIcon.Warning))
                StartLeftoverSearchFor(p);
        }
        else if (!stillThere)
        {
            if (Ui.Confirm(this, $"«{p.Name}» удалена. Поискать оставшиеся после неё папки и записи реестра?"))
                StartLeftoverSearchFor(p);
        }
    }

    void ForceRemoveSelected()
    {
        var p = SelectedProgram();
        if (p == null) { Ui.Info(this, "Выберите программу в списке."); return; }
        StartLeftoverSearchFor(p);
    }

    void StartLeftoverSearchFor(InstalledProgram p)
    {
        var terms = LeftoverFinder.TermsFor(p);
        string strong = string.Join("; ", terms.Where(t => t.Strong).Select(t => t.Text));
        string weak = string.Join("; ", terms.Where(t => !t.Strong).Select(t => t.Text));
        if (strong.Length == 0) strong = p.Name;
        StartLeftoverSearch(strong, weak, p);
    }
}
