using DiskDoctor.Core;
using DiskDoctor.UI;

namespace DiskDoctor;

public sealed partial class MainForm
{
    TabPage _leftPage;
    ListView _lvLeft;
    TextBox _leftInfo, _termsStrong, _termsWeak;
    CheckBox _chkRecycle, _chkRestore;
    Label _leftTotal;
    InstalledProgram _leftProgram;
    List<LeftoverItem> _leftItems = new();
    Button _btnLeftSearch, _btnLeftRemove, _btnOrphans;

    TabPage BuildLeftoversTab()
    {
        _leftPage = new TabPage("Хвосты и остатки");
        _termsStrong = new TextBox { Width = 300, Margin = new Padding(3, 6, 3, 3), PlaceholderText = "например: DaVinci Resolve" };
        _termsWeak = new TextBox { Width = 200, Margin = new Padding(3, 6, 3, 3), PlaceholderText = "например: Blackmagic Design" };
        _tt.SetToolTip(_termsStrong, "Название программы. Несколько вариантов — через «;». Найденное по этим словам отмечается автоматически.");
        _tt.SetToolTip(_termsWeak, "Издатель или доп. слова. Найденное по ним НЕ отмечается автоматически — проверьте вручную.");
        _termsStrong.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = SearchLeftovers(); } };
        _btnLeftSearch = Ui.Btn("🔍 Искать хвосты", async (_, _) => { _leftProgram = null; await SearchLeftovers(); });
        _btnOrphans = Ui.Btn("🧩 Найти осиротевшие папки", async (_, _) => await FindOrphans(), "Папки в Program Files, ProgramData и AppData, для которых не найдено установленной программы.", _tt);
        var bar1 = Ui.Bar(Ui.Lbl("Программа:"), _termsStrong, Ui.Lbl("Издатель (необяз.):"), _termsWeak, _btnLeftSearch, _btnOrphans);

        _chkRecycle = new CheckBox { Text = "Файлы — в Корзину (можно вернуть)", Checked = true, AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
        _chkRestore = new CheckBox { Text = "Создать точку восстановления перед удалением", Checked = Sys.IsAdmin, Enabled = Sys.IsAdmin, AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
        _btnLeftRemove = Ui.Btn("🗑 Удалить отмеченное", async (_, _) => await RemoveLeftovers());
        _leftTotal = new Label { AutoSize = true, Margin = new Padding(12, 8, 3, 3), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        var bar2 = Ui.Bar(_btnLeftRemove,
            Ui.Btn("Отметить всё", (_, _) => SetAllChecked(true)),
            Ui.Btn("Снять всё", (_, _) => SetAllChecked(false)),
            _chkRecycle, _chkRestore, _leftTotal);

        _lvLeft = Ui.List(true, ("Тип", 200, false), ("Объект", 560, false), ("Размер", 90, true), ("Оценка", 140, false), ("Примечание", 600, false));
        _lvLeft.SelectedIndexChanged += (_, _) => ShowLeftInfo();
        _lvLeft.ItemChecked += (_, e) => { if ((e.Item.Tag as Row)?.Obj is LeftoverItem li) li.Checked = e.Item.Checked; UpdateLeftTotal(); };
        _lvLeft.DoubleClick += (_, _) =>
        {
            if (_lvLeft.SelectedItems.Count != 1 || (_lvLeft.SelectedItems[0].Tag as Row)?.Obj is not LeftoverItem li) return;
            if (li.Path != null) Sys.OpenInExplorer(li.Path);
            else if (li.Regs.Count > 0) Sys.OpenRegedit(li.Regs[0].KeyPath);
        };
        _leftInfo = Ui.InfoBox();

        var help = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(6, 4, 6, 0), ForeColor = Color.DimGray,
            Text = "Поиск остатков программы: регистрация в установщике Windows (из-за неё «не удаляется и не ставится заново»), записи «Программы и компоненты», папки, " +
                   "ключи реестра, службы, задания планировщика, автозапуск и ярлыки. Перед удалением сохраняется резервная копия реестра (.reg) — её можно вернуть двойным щелчком.",
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_lvLeft);
        split.Panel2.Controls.Add(_leftInfo);
        Load += (_, _) => split.SplitterDistance = Math.Max(100, split.Height - 150);
        _leftPage.Controls.Add(split);
        _leftPage.Controls.Add(bar2);
        _leftPage.Controls.Add(bar1);
        _leftPage.Controls.Add(help);
        return _leftPage;
    }

    void StartLeftoverSearch(string strong, string weak, InstalledProgram p)
    {
        _termsStrong.Text = strong;
        _termsWeak.Text = weak;
        _leftProgram = p;
        _tabs.SelectedTab = _leftPage;
        _ = SearchLeftovers();
    }

    static List<string> SplitTerms(string s) =>
        (s ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    async Task SearchLeftovers()
    {
        var strong = SplitTerms(_termsStrong.Text);
        var weak = SplitTerms(_termsWeak.Text);
        var bad = strong.Concat(weak).Where(LeftoverFinder.IsTooGeneric).ToList();
        if (bad.Count > 0)
        {
            Ui.Warn(this, "Слишком общие или короткие слова для поиска (найдётся много чужого): " + string.Join(", ", bad) + "\nУточните название.");
            return;
        }
        if (strong.Count == 0 && weak.Count == 0) { Ui.Info(this, "Введите название программы."); return; }
        var terms = strong.Select(t => new SearchTerm(t, true)).Concat(weak.Select(t => new SearchTerm(t, false))).ToList();
        var codes = new List<string>();
        if (_leftProgram?.IsMsi == true && _leftProgram.ProductCode != null) codes.Add(_leftProgram.ProductCode);

        _btnLeftSearch.Enabled = _btnLeftRemove.Enabled = _btnOrphans.Enabled = false;
        _lvLeft.Items.Clear();
        _leftInfo.Text = "";
        try
        {
            var prog = _leftProgram;
            _leftItems = await Bg("Поиск хвостов…", () =>
            {
                var items = LeftoverFinder.Search(terms, codes, s => SetStatus("Поиск: " + s), CancellationToken.None);
                // запись удаления выбранной программы — даже если название не совпало
                if (prog != null && !items.Any(i => i.Regs.Any(r => r.ValueName == null && r.KeyPath.Equals(prog.UninstallKey.KeyPath, StringComparison.OrdinalIgnoreCase))))
                    items.Insert(0, new LeftoverItem { Kind = LeftKind.Uninstall, Display = $"{prog.Name} — {prog.FullKey}", Regs = { prog.UninstallKey }, Checked = true, Risk = Risk.Caution, Note = "Запись выбранной программы в списке установленных." });
                return items;
            });
            FillLeftovers();
            SetStatus($"Найдено объектов: {_leftItems.Count}, отмечено: {_leftItems.Count(i => i.Checked)}. Проверьте список и нажмите «Удалить отмеченное».");
            if (_leftItems.Count == 0) Ui.Info(this, "Ничего не найдено. Попробуйте другое написание названия.");
        }
        finally { _btnLeftSearch.Enabled = _btnLeftRemove.Enabled = _btnOrphans.Enabled = true; }
    }

    async Task FindOrphans()
    {
        _btnLeftSearch.Enabled = _btnLeftRemove.Enabled = _btnOrphans.Enabled = false;
        try
        {
            _programs ??= await Bg("Чтение списка программ…", () => ProgramList.Load());
            var progs = _programs;
            _leftItems = await Bg("Поиск осиротевших папок…", () => LeftoverFinder.FindOrphans(progs, s => SetStatus(s), CancellationToken.None));
            _leftProgram = null;
            FillLeftovers();
            SetStatus($"Возможных остатков: {_leftItems.Count} на {Fmt.Size(_leftItems.Sum(i => i.Size))}. Ничего не отмечено — проверьте каждый вручную.");
        }
        finally { _btnLeftSearch.Enabled = _btnLeftRemove.Enabled = _btnOrphans.Enabled = true; }
    }

    void FillLeftovers()
    {
        _lvLeft.BeginUpdate();
        _lvLeft.Items.Clear();
        foreach (var li in _leftItems)
        {
            var it = new ListViewItem(new[] { li.KindName, li.Display, li.Size > 0 ? Fmt.Size(li.Size) : "", Classifier.RiskName(li.Risk), li.Note ?? "" })
            {
                Tag = new Row(li, li.KindName, li.Display, li.Size, (int)li.Risk, li.Note ?? ""),
                Checked = li.Checked,
            };
            Ui.StyleRisk(it, li.Risk, 3);
            _lvLeft.Items.Add(it);
        }
        _lvLeft.EndUpdate();
        UpdateLeftTotal();
    }

    void SetAllChecked(bool v)
    {
        _lvLeft.BeginUpdate();
        foreach (ListViewItem it in _lvLeft.Items)
        {
            var li = (it.Tag as Row)?.Obj as LeftoverItem;
            if (v && li?.Risk == Risk.Critical) continue; // системное не отмечаем массово
            it.Checked = v;
        }
        _lvLeft.EndUpdate();
    }

    void UpdateLeftTotal()
    {
        var sel = _leftItems.Where(i => i.Checked).ToList();
        _leftTotal.Text = sel.Count == 0 ? "" : $"Отмечено: {sel.Count}, файлов на {Fmt.Size(sel.Sum(i => i.Size))}";
    }

    void ShowLeftInfo()
    {
        if (_lvLeft.SelectedItems.Count != 1 || (_lvLeft.SelectedItems[0].Tag as Row)?.Obj is not LeftoverItem li) return;
        var lines = new List<string> { $"{li.KindName}: {li.Display}", li.Note ?? "" };
        if (li.Path != null) lines.Add("Путь: " + li.Path + "   (двойной щелчок — открыть)");
        if (li.Regs.Count > 0)
        {
            lines.Add($"Реестр ({li.Regs.Count}):");
            lines.AddRange(li.Regs.Take(40).Select(r => "  " + r));
            if (li.Regs.Count > 40) lines.Add($"  … и ещё {li.Regs.Count - 40}");
        }
        if (li.Files.Count > 0) lines.Add("Файлы: " + string.Join(", ", li.Files));
        _leftInfo.Text = string.Join("\r\n", lines);
    }

    async Task RemoveLeftovers()
    {
        var sel = _leftItems.Where(i => i.Checked).ToList();
        if (sel.Count == 0) { Ui.Info(this, "Ничего не отмечено."); return; }
        if (!Sys.IsAdmin && sel.Any(i => i.Kind is LeftKind.Msi or LeftKind.Service or LeftKind.Task || i.Regs.Any(r => r.Hive == Microsoft.Win32.RegistryHive.LocalMachine)))
        {
            RequireAdmin();
            return;
        }
        var crit = sel.Where(i => i.Risk == Risk.Critical).ToList();
        string text = $"Удалить {sel.Count} {Plural(sel.Count, "объект", "объекта", "объектов")}?\n\n" +
                      string.Join("\n", sel.Take(12).Select(i => $"• {i.KindName}: {Short(i.Display, 90)}")) + (sel.Count > 12 ? $"\n… и ещё {sel.Count - 12}" : "") +
                      "\n\nПеред удалением будет сохранена резервная копия реестра." +
                      (sel.Any(i => i.Kind == LeftKind.Msi) ? "\n\nРегистрация MSI будет удалена: Windows «забудет» об установке, и программу можно будет поставить заново." : "");
        if (crit.Count > 0) text += $"\n\n⚠ ВНИМАНИЕ: отмечено {crit.Count} системных объектов (драйверы/системные папки). Убедитесь, что понимаете, что делаете!";
        if (!Ui.Confirm(this, text, "Удаление хвостов", crit.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question)) return;

        _btnLeftSearch.Enabled = _btnLeftRemove.Enabled = _btnOrphans.Enabled = false;
        try
        {
            if (_chkRestore.Checked && Sys.IsAdmin) await CreateRestorePoint(false);
            bool recycle = _chkRecycle.Checked;
            string backup = null;
            var res = await Bg("Удаление хвостов…", () => LeftoverFinder.Remove(sel, recycle, s => SetStatus(s), out backup));
            string msg = $"Готово. Освобождено {Fmt.Size(res.Freed)}." +
                         (res.ScheduledOnReboot > 0 ? $"\n{res.ScheduledOnReboot} занятых файлов будут удалены при перезагрузке." : "") +
                         (res.Failed > 0 ? $"\nНе удалось удалить: {res.Failed} (см. вкладку «Журнал»)." : "") +
                         $"\n\nРезервная копия реестра:\n{backup}\n(чтобы вернуть — дважды щёлкните по файлу)." +
                         (sel.Any(i => i.Kind is LeftKind.Msi or LeftKind.Uninstall) ? "\n\nРекомендуется перезагрузить компьютер, затем установить программу заново." : "");
            foreach (var e in res.Errors.Take(30)) Log.Write("  ошибка: " + e);
            Ui.Info(this, msg, "Удаление хвостов");
            _programs = null; // список программ изменился
        }
        finally { _btnLeftSearch.Enabled = _btnLeftRemove.Enabled = _btnOrphans.Enabled = true; }
        // показать, что осталось
        if (_termsStrong.Text.Trim().Length > 0) await SearchLeftovers();
    }

    static string Short(string s, int n) => s == null ? "" : s.Length <= n ? s : s[..(n - 1)] + "…";
}
